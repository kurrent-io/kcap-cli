using System.Net;
using System.Text;
using System.Text.Json;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Discover + classify + import Cursor IDE Composer/Agent sessions. Unlike
/// Claude / Codex (which read <c>.jsonl</c> transcript files), Cursor stores
/// state in two SQLite DBs: a per-workspace <c>state.vscdb</c> that lists
/// composer ids, and a global <c>state.vscdb</c> that holds the composer
/// headers, bubbles, and content blobs.
///
/// <para>
/// Discovery walks the workspace storage directory, picks the workspaces
/// matching <c>--cursor-workspace</c> / <c>--cursor-all-workspaces</c> / the
/// current cwd, enumerates composer ids per workspace, and detects each
/// workspace's git remote ONCE (the result is reused for every composer in
/// that workspace).
/// </para>
///
/// <para>
/// Classification queries <c>GET /api/cursor/{composerId}/watermark</c> per
/// composer and marks it <c>AlreadyLoaded</c> when the server's high-water
/// mark covers the header's <c>lastUpdatedAtMs</c>, otherwise <c>New</c>.
/// </para>
///
/// <para>
/// ImportSessionAsync assembles a <see cref="CursorImportPayload"/> from the
/// SQLite state, stamps <c>cli_owner</c>/<c>cli_repo</c> (recovered from the
/// workspace's git remote during Discover), and POSTs to
/// <c>/hooks/cursor-import</c>.
/// </para>
/// </summary>
internal sealed class CursorImportSource : IImportSource {
    readonly CursorPaths _paths;

    public CursorImportSource(CursorPaths? pathsOverride = null) {
        _paths = pathsOverride ?? CursorPaths.Resolve();
    }

    public string Vendor => "cursor";

    public bool IsAvailable => File.Exists(_paths.GlobalStateDb);

    public bool SupportsTitleGeneration => false;

    /// <summary>
    /// Normalize a Cursor composer id by stripping dashes. The server stores
    /// Cursor sessions under <c>AgentSession-{dashless}</c> streams, so the
    /// <c>--session</c> filter must compare dashless on both sides.
    /// </summary>
    public static string NormalizeCursorSessionId(string id) => id.Replace("-", "");

    public async Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var workspaces = CursorCommand.ResolveWorkspaces(_paths, filters.CursorWorkspace, filters.CursorAllWorkspaces).ToList();

        var sessionFilter = filters.FilterSession is { } sf ? NormalizeCursorSessionId(sf) : null;

        var result = new List<DiscoveredSession>();

        foreach (var (folderPath, wsDbPath) in workspaces) {
            if (!File.Exists(wsDbPath)) continue;

            IReadOnlyList<string> composerIds;

            try {
                composerIds = await CursorStateReader.ListWorkspaceComposerIdsAsync(wsDbPath, ct);
            } catch {
                continue;
            }

            if (composerIds.Count == 0) continue;

            // Detect git remote once per workspace (not per composer). Null when
            // the workspace folder isn't a git repo with a parseable origin.
            string? cliOwner = null;
            string? cliRepo  = null;

            try {
                var repo = await RepositoryDetection.DetectRepositoryAsync(folderPath);
                cliOwner = repo?.Owner;
                cliRepo  = repo?.RepoName;
            } catch {
                // Best-effort — leave nulls.
            }

            foreach (var composerId in composerIds) {
                var dashless = NormalizeCursorSessionId(composerId);

                if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal)) {
                    continue;
                }

                result.Add(new DiscoveredSession(
                    SessionId:      dashless,
                    Vendor:         Vendor,
                    Cwd:            folderPath,
                    FirstTimestamp: null,
                    SourceMeta:     new Dictionary<string, object?> {
                        ["ComposerId"]    = composerId,
                        ["WorkspacePath"] = folderPath,
                        ["GlobalDbPath"]  = _paths.GlobalStateDb,
                        ["CliOwner"]      = cliOwner,
                        ["CliRepo"]       = cliRepo,
                    }
                ));
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                   ctx,
            CancellationToken                 ct
        ) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var composerId   = (string)s.SourceMeta["ComposerId"]!;
            var globalDbPath = (string)s.SourceMeta["GlobalDbPath"]!;

            var meta = new SessionMetadata {
                SessionId = s.SessionId,
                Cwd       = s.Cwd,
            };

            RawComposerHeader? header = null;

            try {
                header = await CursorStateReader.GetComposerHeaderAsync(globalDbPath, composerId, ct);
            } catch {
                // Treat read failure as ProbeError so the orchestrator surfaces it.
            }

            if (header is null) {
                results.Add(new() {
                    SessionId        = s.SessionId,
                    FilePath         = "",
                    EncodedCwd       = "",
                    Meta             = meta,
                    Status           = ImportCommand.ClassificationStatus.ProbeError,
                    Vendor           = Vendor,
                    ProbeErrorReason = "no composer header",
                    SourceMeta       = s.SourceMeta,
                });
                continue;
            }

            // Mirror CursorCommand.RunAsync: skip non-agent composers (chat / inline / ask).
            // These are not agent sessions and shouldn't be ingested. ProbeErrorReason is
            // kept short ("mode={X}") so a future Plan-grid sub-row can render it.
            if (!IsAgentMode(header)) {
                results.Add(new() {
                    SessionId        = s.SessionId,
                    FilePath         = "",
                    EncodedCwd       = "",
                    Meta             = meta,
                    Status           = ImportCommand.ClassificationStatus.Excluded,
                    Vendor           = Vendor,
                    ProbeErrorReason = $"mode={header.UnifiedMode}",
                    SourceMeta       = s.SourceMeta,
                });
                continue;
            }

            // Mirror CursorCommand.RunAsync: skip composers with non-empty generatingBubbleIds
            // (an LLM is actively writing). Importing now would push a mid-write payload.
            string? composerDataRaw;
            try {
                composerDataRaw = await CursorStateReader.GetComposerDataAsync(globalDbPath, composerId, ct);
            } catch {
                composerDataRaw = null;  // best-effort; let downstream BuildPayload re-attempt
            }

            if (HasInFlightBubbles(composerDataRaw)) {
                results.Add(new() {
                    SessionId        = s.SessionId,
                    FilePath         = "",
                    EncodedCwd       = "",
                    Meta             = meta,
                    Status           = ImportCommand.ClassificationStatus.Excluded,
                    Vendor           = Vendor,
                    ProbeErrorReason = "in-flight bubbles",
                    SourceMeta       = s.SourceMeta,
                });
                continue;
            }

            meta.FirstTimestamp = header.CreatedAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAtMs)
                : null;
            meta.LastTimestamp = header.LastUpdatedAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(header.LastUpdatedAtMs)
                : null;

            var status = await IsWatermarkCurrentAsync(ctx.HttpClient, ctx.BaseUrl, composerId, header.LastUpdatedAtMs, ct)
                ? ImportCommand.ClassificationStatus.AlreadyLoaded
                : ImportCommand.ClassificationStatus.New;

            results.Add(new() {
                SessionId  = s.SessionId,
                FilePath   = "",
                EncodedCwd = "",
                Meta       = meta,
                Status     = status,
                Vendor     = Vendor,
                SourceMeta = s.SourceMeta,
            });
        }

        return results;
    }

    public async Task<ImportOutcome> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) {
        // Direct casts on required keys so a refactor renaming a SourceMeta key throws
        // loudly rather than silently swallowing the rename via `as string ?? ""`.
        var composerId    = (string)classification.SourceMeta!["ComposerId"]!;
        var workspacePath = (string)classification.SourceMeta!["WorkspacePath"]!;
        var globalDbPath  = (string)classification.SourceMeta!["GlobalDbPath"]!;
        var cliOwner      = (string?)classification.SourceMeta!["CliOwner"];
        var cliRepo       = (string?)classification.SourceMeta!["CliRepo"];

        CursorImportPayload payload;

        try {
            payload = await BuildPayload(globalDbPath, composerId, workspacePath, ct);
        } catch {
            return ImportOutcome.Failed;
        }

        payload = payload with { CliOwner = cliOwner, CliRepo = cliRepo };

        var payloadJson = JsonSerializer.Serialize(payload, CursorJsonContext.Default.CursorImportPayload);

        if (Encoding.UTF8.GetByteCount(payloadJson) > CursorPayloadAssembler.PayloadHardCapBytes) {
            return ImportOutcome.Failed;
        }

        try {
            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            var       resp    = await ctx.HttpClient.PostWithRetryAsync($"{ctx.BaseUrl}/hooks/cursor-import", content, ct: ct);

            return resp.IsSuccessStatusCode ? ImportOutcome.Loaded : ImportOutcome.Failed;
        } catch {
            return ImportOutcome.Failed;
        }
    }

    /// <summary>
    /// True when the composer header's <c>unifiedMode</c> is "agent" (case-insensitive).
    /// Cursor chat / inline / ask composers are not agent sessions and shouldn't be ingested.
    /// </summary>
    internal static bool IsAgentMode(RawComposerHeader header) =>
        string.Equals(header.UnifiedMode, "agent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the composer's <c>composerData</c> JSON has non-empty <c>generatingBubbleIds</c>,
    /// i.e. an LLM is actively writing. Returns <c>false</c> on null input or parse failure
    /// (best-effort — the import path will re-attempt the parse downstream).
    /// </summary>
    internal static bool HasInFlightBubbles(string? composerDataRaw) {
        if (composerDataRaw is null) return false;

        try {
            using var doc           = JsonDocument.Parse(composerDataRaw);
            var       generatingIds = doc.RootElement.TryGetProperty("generatingBubbleIds", out var gb)
                ? gb.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];

            return generatingIds.Count > 0;
        } catch {
            return false;
        }
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

            if (resp.StatusCode == HttpStatusCode.NotFound) return false;

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

    /// <summary>
    /// Assembles a <see cref="CursorImportPayload"/> from the SQLite state.
    /// Logic mirrors <c>CursorCommand.BuildPayload</c>; that helper is private
    /// and the orchestrator removal task (F1) will delete CursorCommand
    /// entirely, so duplicating the logic here is the lower-risk move.
    /// </summary>
    static async Task<CursorImportPayload> BuildPayload(
            string            globalDbPath,
            string            composerId,
            string            workspaceFolder,
            CancellationToken ct
        ) {
        var composerDataRaw = await CursorStateReader.GetComposerDataAsync(globalDbPath, composerId, ct);

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
                : [];

            var generatingIds = root.TryGetProperty("generatingBubbleIds", out var gbEl)
                ? gbEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];

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

        var rawBubbles = await CursorStateReader.ListBubblesAsync(globalDbPath, composerId, ct);

        // Build a position map from fullConversationHeadersOnly so bubbles are
        // ordered as the conversation prescribes, not by SQLite storage order.
        var orderMap = new Dictionary<string, int>(StringComparer.Ordinal);
        if (composerDataRaw is not null) {
            try {
                using var ordDoc  = JsonDocument.Parse(composerDataRaw);
                var       ordRoot = ordDoc.RootElement;
                if (ordRoot.TryGetProperty("fullConversationHeadersOnly", out var headersArr)
                    && headersArr.ValueKind == JsonValueKind.Array) {
                    var i = 0;
                    foreach (var h in headersArr.EnumerateArray()) {
                        if (h.TryGetProperty("bubbleId", out var bid) && bid.ValueKind == JsonValueKind.String)
                            orderMap[bid.GetString()!] = i++;
                    }
                }
            } catch {
                // Best-effort.
            }
        }

        var assembledBubbles = new List<CursorBubble>(rawBubbles.Count);

        foreach (var (_, bubbleJson) in rawBubbles) {
            assembledBubbles.Add(CursorPayloadAssembler.AssembleBubble(bubbleJson, workspaceFolder));
        }

        var orderedBubbles = orderMap.Count > 0
            ? assembledBubbles
                .Where(b => orderMap.ContainsKey(b.BubbleId))
                .OrderBy(b => orderMap[b.BubbleId])
                .ToList()
            : assembledBubbles;

        var contentBlobKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bubble in orderedBubbles) {
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
                    // Best-effort.
                }
            }
        }

        var fetchedBlobs = await CursorStateReader.GetContentBlobsAsync(globalDbPath, contentBlobKeys, ct);
        var contentBlobs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, blob) in fetchedBlobs) {
            var (k, v) = CursorPayloadAssembler.MaybeTruncateBlob(key, blob);
            contentBlobs[k] = v;
        }

        var header = await CursorStateReader.GetComposerHeaderAsync(globalDbPath, composerId, ct);

        var trackedRepos = header!.TrackedGitRepos?
            .Select(r => new CursorTrackedRepo {
                RepoPath = r.RepoPath,
                Branches = r.BranchNames?.Select(b => new CursorTrackedBranch { BranchName = b }).ToList()
            })
            .ToList();

        var cursorHeader = new CursorHeader {
            Name              = header.Name,
            UnifiedMode       = header.UnifiedMode,
            CreatedAtMs       = header.CreatedAtMs,
            LastUpdatedAtMs   = header.LastUpdatedAtMs,
            TrackedGitRepos   = trackedRepos,
            TotalLinesAdded   = header.TotalLinesAdded,
            TotalLinesRemoved = header.TotalLinesRemoved,
            FilesChangedCount = header.FilesChangedCount,
            Subtitle          = header.Subtitle
        };

        return new CursorImportPayload {
            Vendor              = "cursor",
            ComposerId          = composerId,
            SchemaSourceVersion = new CursorSchemaVersion { ComposerData = 1, Bubble = 1 },
            Header              = cursorHeader,
            ComposerData        = composerData,
            Bubbles             = orderedBubbles,
            ContentBlobs        = contentBlobs
        };
    }
}
