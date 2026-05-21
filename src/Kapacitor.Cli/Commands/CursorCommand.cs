using System.Text;
using System.Text.Json;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Imports Cursor IDE Composer/Agent sessions to the Capacitor server.
/// </summary>
static class CursorCommand {
    public static async Task<int> RunAsync(
        string[]      args,
        string        baseUrl,
        CursorPaths?  pathsOverride = null,
        CancellationToken ct        = default
    ) {
        if (args.Length < 1 || args[0] != "import") {
            var help = EmbeddedResources.TryLoad("help-cursor.txt");
            await Console.Out.WriteAsync(help ?? "Usage: kapacitor cursor import [--workspace P] [--all]");

            return 2;
        }

        // Parse flags
        var allFlag       = args.Contains("--all");
        string? workspace = GetArg(args, "--workspace");

        var paths = pathsOverride ?? CursorPaths.Resolve();

        // Build an authenticated HttpClient using the shared helper (token discovery, provider check)
        using var http = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl, ct);

        var workspaces = ResolveWorkspaces(paths, workspace, allFlag).ToList();

        if (workspaces.Count == 0) {
            await Console.Error.WriteLineAsync("[cursor] No matching Cursor workspace found.");

            return 1;
        }

        var failed = 0;

        foreach (var (folderPath, wsDbPath) in workspaces) {
            if (!File.Exists(wsDbPath)) {
                Console.Error.WriteLine($"[cursor] Workspace DB not found: {wsDbPath}");

                continue;
            }

            IReadOnlyList<string> composerIds;

            try {
                composerIds = await CursorStateReader.ListWorkspaceComposerIdsAsync(wsDbPath, ct);
            } catch (Exception ex) {
                Console.Error.WriteLine($"[cursor] Failed to read workspace DB {wsDbPath}: {ex.Message}");

                continue;
            }

            foreach (var composerId in composerIds) {
                if (!File.Exists(paths.GlobalStateDb)) {
                    Console.Error.WriteLine($"[cursor] Global state DB not found: {paths.GlobalStateDb}");

                    break;
                }

                RawComposerHeader? header;

                try {
                    header = await CursorStateReader.GetComposerHeaderAsync(paths.GlobalStateDb, composerId, ct);
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[cursor] Failed to read header for {composerId}: {ex.Message}");

                    continue;
                }

                if (header is null) {
                    Console.Error.WriteLine($"[cursor] No header found for {composerId}, skipping.");

                    continue;
                }

                if (!string.Equals(header.UnifiedMode, "agent", StringComparison.OrdinalIgnoreCase)) {
                    Console.Error.WriteLine($"[cursor] {composerId} mode={header.UnifiedMode}, skipping.");

                    continue;
                }

                // Read composerData up-front to check for in-flight bubbles (Bug #1)
                string? composerDataRaw;

                try {
                    composerDataRaw = await CursorStateReader.GetComposerDataAsync(paths.GlobalStateDb, composerId, ct);
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[cursor] Failed to read composerData for {composerId}: {ex.Message}");

                    continue;
                }

                if (composerDataRaw is not null) {
                    try {
                        using var doc            = JsonDocument.Parse(composerDataRaw);
                        var       root           = doc.RootElement;
                        var       generatingIds  = root.TryGetProperty("generatingBubbleIds", out var gbEl)
                            ? gbEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                            : [];

                        if (generatingIds.Count > 0) {
                            Console.Error.WriteLine($"[skip] {composerId} in-flight bubbles");

                            continue;
                        }
                    } catch {
                        // Best-effort — if we can't parse, continue and let the server decide
                    }
                }

                // Check watermark
                if (await IsWatermarkCurrentAsync(http, baseUrl, composerId, header.LastUpdatedAtMs, ct)) {
                    Console.Error.WriteLine($"[skip] {composerId} unchanged");

                    continue;
                }

                // Build payload (re-uses already-read composerDataRaw to avoid double read)
                CursorImportPayload payload;

                try {
                    payload = await BuildPayload(paths, composerId, folderPath, composerDataRaw, ct);
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[cursor] Failed to build payload for {composerId}: {ex.Message}");
                    failed++;

                    continue;
                }

                // Serialize and size-check
                var payloadJson = JsonSerializer.Serialize(payload, CursorJsonContext.Default.CursorImportPayload);

                if (Encoding.UTF8.GetByteCount(payloadJson) > CursorPayloadAssembler.PayloadHardCapBytes) {
                    Console.Error.WriteLine($"[cursor] Payload for {composerId} exceeds 10 MB hard cap — skipping. Reduce session size or contact support.");

                    continue;
                }

                // POST to server
                var name = header.Name ?? composerId;

                try {
                    using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    var       resp    = await http.PostWithRetryAsync($"{baseUrl}/hooks/cursor-import", content, ct: ct);

                    if (!resp.IsSuccessStatusCode) {
                        Console.Error.WriteLine($"[fail] {composerId} HTTP {(int)resp.StatusCode}");
                        failed++;
                    } else {
                        Console.Error.WriteLine($"[{(int)resp.StatusCode}] {composerId} ({name})");
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[cursor] POST failed for {composerId}: {ex.Message}");
                    failed++;
                }
            }
        }

        return failed > 0 ? 1 : 0;
    }

    static async Task<bool> IsWatermarkCurrentAsync(
        HttpClient        http,
        string            baseUrl,
        string            composerId,
        long              headerLastUpdatedAtMs,
        CancellationToken ct
    ) {
        try {
            var resp = await http.GetWithRetryAsync($"{baseUrl}/api/cursor/{composerId}/watermark", ct: ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;

            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("last_updated_at_ms", out var lv)) {
                var serverMs = lv.GetInt64();

                return serverMs >= headerLastUpdatedAtMs;
            }

            return false;
        } catch {
            return false;
        }
    }

    static async Task<CursorImportPayload> BuildPayload(
        CursorPaths       paths,
        string            composerId,
        string            workspaceFolder,
        string?           composerDataRaw,
        CancellationToken ct
    ) {
        // composerDataRaw is pre-read by the caller (already checked for in-flight bubbles)
        CursorComposerData composerData;

        if (composerDataRaw is not null) {
            using var doc  = JsonDocument.Parse(composerDataRaw);
            var       root = doc.RootElement;

            var modelConfig = new CursorModelConfig {
                ModelName      = root.TryGetProperty("modelConfig", out var mc) && mc.TryGetProperty("modelName", out var mn) && mn.ValueKind == JsonValueKind.String ? mn.GetString() : null,
                SelectedModels = root.TryGetProperty("modelConfig", out var mc2) && mc2.TryGetProperty("selectedModels", out var sm)
                    ? sm.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : null
            };

            var headers = root.TryGetProperty("fullConversationHeadersOnly", out var hdrsEl)
                ? hdrsEl.EnumerateArray()
                    .Where(h => h.ValueKind == JsonValueKind.Object)
                    .Select(h => new CursorTurnHeader {
                        BubbleId = h.TryGetProperty("bubbleId", out var bidEl) && bidEl.ValueKind == JsonValueKind.String ? bidEl.GetString()! : "",
                        Type     = h.TryGetProperty("type",     out var tyEl)  && tyEl.ValueKind  == JsonValueKind.Number ? tyEl.GetInt32()    : 0
                    }).ToList<CursorTurnHeader>()
                : new List<CursorTurnHeader>();

            var generatingIds = root.TryGetProperty("generatingBubbleIds", out var gbEl)
                ? gbEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : new List<string>();

            var status = root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;

            composerData = new CursorComposerData {
                ModelConfig                 = modelConfig,
                FullConversationHeadersOnly = headers,
                GeneratingBubbleIds         = generatingIds,
                Status                      = status
            };
        } else {
            composerData = new CursorComposerData {
                ModelConfig                 = new CursorModelConfig { ModelName = null, SelectedModels = null },
                FullConversationHeadersOnly = [],
                GeneratingBubbleIds         = [],
                Status                      = null
            };
        }

        // Read bubbles from global DB
        var rawBubbles = await CursorStateReader.ListBubblesAsync(paths.GlobalStateDb, composerId, ct);

        // Collect content blob keys referenced by edit_file_v2 bubbles
        var contentBlobKeys = new HashSet<string>(StringComparer.Ordinal);
        var assembledBubbles = new List<CursorBubble>(rawBubbles.Count);

        foreach (var (_, bubbleJson) in rawBubbles) {
            var bubble = CursorPayloadAssembler.AssembleBubble(bubbleJson, workspaceFolder);
            assembledBubbles.Add(bubble);

            // Extract content blob refs from edit_file_v2 result JSON
            if (bubble.ToolFormerData is { Name: "edit_file_v2", Result: { } resultJson }) {
                try {
                    using var doc  = JsonDocument.Parse(resultJson);
                    var       root = doc.RootElement;

                    if (root.TryGetProperty("beforeContentId", out var bci)) {
                        var key = bci.GetString();

                        if (!string.IsNullOrEmpty(key)) contentBlobKeys.Add(key);
                    }

                    if (root.TryGetProperty("afterContentId", out var aci)) {
                        var key = aci.GetString();

                        if (!string.IsNullOrEmpty(key)) contentBlobKeys.Add(key);
                    }
                } catch {
                    // Best-effort — don't fail the whole composer on a bad result JSON
                }
            }
        }

        // Load content blobs and apply size cap
        var contentBlobs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in contentBlobKeys) {
            var blob = await CursorStateReader.GetContentBlobAsync(paths.GlobalStateDb, key, ct);

            if (blob is null) continue;

            var (k, v) = CursorPayloadAssembler.MaybeTruncateBlob(key, blob);
            contentBlobs[k] = v;
        }

        // Read header again (it was already verified to exist by caller)
        var header = await CursorStateReader.GetComposerHeaderAsync(paths.GlobalStateDb, composerId, ct);

        var cursorHeader = new CursorHeader {
            Name              = header!.Name,
            UnifiedMode       = header.UnifiedMode,
            CreatedAtMs       = header.CreatedAtMs,
            LastUpdatedAtMs   = header.LastUpdatedAtMs,
            TrackedGitRepos   = null,  // not in the raw header; server may enrich
            TotalLinesAdded   = 0,
            TotalLinesRemoved = 0,
            FilesChangedCount = 0,
            Subtitle          = null
        };

        return new CursorImportPayload {
            Vendor              = "cursor",
            ComposerId          = composerId,
            SchemaSourceVersion = new CursorSchemaVersion { ComposerData = 1, Bubble = 1 },
            Header              = cursorHeader,
            ComposerData        = composerData,
            Bubbles             = assembledBubbles,
            ContentBlobs        = contentBlobs
        };
    }

    /// <summary>
    /// Walks <see cref="CursorPaths.WorkspaceStorageDir"/>, reads each subdir's
    /// <c>workspace.json</c> for a <c>folder</c> URI, and yields
    /// <c>(folderPath, wsDbPath)</c> tuples.
    /// </summary>
    internal static IEnumerable<(string FolderPath, string WsDbPath)> ResolveWorkspaces(
        CursorPaths paths,
        string?     selectedWorkspace,
        bool        all
    ) {
        if (!Directory.Exists(paths.WorkspaceStorageDir)) yield break;

        var cwd = Environment.CurrentDirectory;

        foreach (var subdir in Directory.EnumerateDirectories(paths.WorkspaceStorageDir)) {
            var wsJsonPath = Path.Combine(subdir, "workspace.json");

            string? folderPath = null;

            if (File.Exists(wsJsonPath)) {
                try {
                    var json = File.ReadAllText(wsJsonPath);

                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("folder", out var folderEl)) {
                        var uri = folderEl.GetString();

                        if (uri is not null) {
                            // Strip file:// prefix and URI-decode
                            folderPath = StripFileUri(uri);
                        }
                    }
                } catch {
                    // If we can't read workspace.json, skip this directory
                    continue;
                }
            }

            if (folderPath is null) continue;

            var wsDbPath = Path.Combine(subdir, "state.vscdb");

            if (all) {
                yield return (folderPath, wsDbPath);

                continue;
            }

            var target = selectedWorkspace ?? cwd;
            var normalizedTarget = NormalizePath(target);
            var normalizedFolder = NormalizePath(folderPath);

            if (string.Equals(normalizedTarget, normalizedFolder, StringComparison.OrdinalIgnoreCase)) {
                yield return (folderPath, wsDbPath);
            }
        }
    }

    static string StripFileUri(string uri) {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) {
            // file:///path → /path  or  file://host/path (rare)
            var path = uri.Substring("file://".Length);

            // On Windows the URI is file:///C:/... → strip leading /
            // On Unix the URI is file:///home/... → strip two leading slashes leaving /
            if (path.StartsWith('/') && path.Length > 2 && path[2] == ':') {
                path = path.TrimStart('/'); // Windows: /C:/foo → C:/foo
            }

            return Uri.UnescapeDataString(path);
        }

        return uri;
    }

    static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
