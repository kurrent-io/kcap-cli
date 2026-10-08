using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>
/// Discover + classify + import historical Mistral Vibe sessions from <c>~/.vibe/logs/session</c>.
/// Two on-disk shapes converge on one import: a legacy <c>session_&lt;ts&gt;/messages.jsonl</c> is sent
/// line-for-line, and a unified <c>unified/&lt;id&gt;/</c> directory is flattened by
/// <see cref="MistralVibeUnifiedStore"/> into the same ordered line stream before sending. Either way
/// the raw lines are POSTed with <c>vendor: mistral-vibe</c>, and the SERVER's Vibe normalizer turns them into
/// canonical events — the client-side projection
/// (<see cref="Capacitor.Models.Transcripts.Harness.MistralVibe.MistralVibeTranscriptEvents"/>) is for
/// local display, not ingestion.
///
/// <para><b>Certification note:</b> session-id/cwd field names in <c>meta.json</c> and the unified
/// line shape are reconstructed from documentation; confirm against a real <c>vibe</c> install.
/// Subagents (Vibe's <c>parent_session_id</c> child sessions) are not yet imported.</para>
/// </summary>
internal sealed class MistralVibeImportSource(string sessionLogsDir, TimeProvider time) : IImportSource {
    const string FormatKey = "Format";
    const string DirKey    = "Dir";

    public HarnessId Vendor => HarnessId.MistralVibe;
    public bool IsAvailable => Directory.Exists(sessionLogsDir);
    public bool SupportsTitleGeneration => false; // the server computes a fallback title at session-end
    public bool AttachesChildContentOnReplay => false; // no subagent import yet

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static string NormalizeForComparison(string path) {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd('/', '\\'); }
    }

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } fc ? NormalizeForComparison(fc) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(sessionLogsDir)) return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);

        foreach (var (dir, format) in EnumerateSessionDirs(ct)) {
            ct.ThrowIfCancellationRequested();
            try {
                var (sessionId, cwd, firstTs) = ReadMeta(dir, format);
                if (sessionId is null || !Guid.TryParse(sessionId, out _)) continue;

                var dashless = sessionId.Replace("-", "");
                if (!seen.Add(dashless)) continue;
                if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal)) continue;

                if (normalizedCwd is not null
                 && (cwd is null || !NormalizeForComparison(cwd).Equals(normalizedCwd, PathComparison))) continue;

                if (sinceUtc is { } cutoff && firstTs is { } ts && ts < cutoff) continue;

                result.Add(new DiscoveredSession(dashless, Vendor, cwd, firstTs,
                    new Dictionary<string, object?> { [FormatKey] = format, [DirKey] = dir }));
            } catch {
                continue; // a hostile/unreadable session dir must not abort the scan
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    IEnumerable<(string Dir, string Format)> EnumerateSessionDirs(CancellationToken ct) {
        var unifiedRoot = Path.Combine(sessionLogsDir, "unified");
        if (Directory.Exists(unifiedRoot))
            foreach (var dir in Directory.EnumerateDirectories(unifiedRoot)) {
                ct.ThrowIfCancellationRequested();
                yield return (dir, "unified");
            }

        foreach (var dir in Directory.EnumerateDirectories(sessionLogsDir, "session_*")) {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(dir, "messages.jsonl"))) yield return (dir, "legacy");
        }
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var meta = new SessionMetadata { SessionId = s.SessionId, Cwd = s.Cwd, FirstTimestamp = s.FirstTimestamp };

            IReadOnlyList<string> lines;
            try { lines = MaterializeLines(s.SourceMeta!); }
            catch { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed")); continue; }

            var nonBlank = lines.Count(l => !string.IsNullOrWhiteSpace(l));
            if (nonBlank == 0) { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "empty transcript")); continue; }
            if (nonBlank < ctx.MinLines) { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.TooShort, nonBlank)); continue; }

            int? serverLastLine;
            try { serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct); }
            catch { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, nonBlank, "watermark probe failed")); continue; }

            var lastLineIndex = LastNonBlankIndex(lines);
            var status        = ImportCommand.ClassificationStatus.New;
            var resumeFromLn  = 0;

            if (serverLastLine is { } srv) {
                if (srv >= lastLineIndex) status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                else { status = ImportCommand.ClassificationStatus.Partial; resumeFromLn = srv + 1; }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId = s.SessionId, FilePath = "", EncodedCwd = "", Meta = meta,
                Status = status, Vendor = Vendor, ResumeFromLine = resumeFromLn, TotalLines = nonBlank, SourceMeta = s.SourceMeta,
            });
        }

        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) {
        IReadOnlyList<string> lines;
        try { lines = MaterializeLines(classification.SourceMeta!); }
        catch { return ImportOutcome.Failed; }
        if (lines.Count == 0) return ImportOutcome.Failed;

        // Lifecycle-before-transcript ordering: a transcript advancing the watermark past a failed
        // lifecycle POST would strand the session lifecycle-less. Re-runs are idempotent server-side.
        var startPayload = BuildSessionStartPayload(classification.SessionId, SessionDir(classification), classification.Meta.Cwd, classification.Meta.FirstTimestamp);
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) startPayload["default_visibility"] = visibility;
        if (!await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/mistral-vibe", startPayload, ct)) return ImportOutcome.Failed;

        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        // SendTranscriptBatches reads a file; a legacy session already is one, a unified session is
        // flattened into a temporary file that is deleted once sent.
        var (filePath, isTemp) = ResolveTranscriptFile(classification.SourceMeta!, lines);
        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: classification.SessionId,
                filePath: filePath, agentId: null, startLine: startLine, time: time, vendor: Vendor, progress: ctx.Progress);
        } catch {
            return ImportOutcome.Failed;
        } finally {
            if (isTemp) { try { File.Delete(filePath); } catch { /* best effort */ } }
        }

        if (!await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/mistral-vibe",
                BuildSessionEndPayload(classification.SessionId, SessionDir(classification), classification.Meta.Cwd, classification.Meta.LastTimestamp), ct)) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;
        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    // ── format resolution ────────────────────────────────────────────────────────────────────────

    static IReadOnlyList<string> MaterializeLines(IReadOnlyDictionary<string, object?> sourceMeta) {
        var dir    = (string)sourceMeta[DirKey]!;
        var format = (string)sourceMeta[FormatKey]!;
        return format == "unified"
            ? MistralVibeUnifiedStore.ReadLines(dir)
            : File.ReadLinesShared(Path.Combine(dir, "messages.jsonl")).ToList();
    }

    static (string Path, bool IsTemp) ResolveTranscriptFile(IReadOnlyDictionary<string, object?> sourceMeta, IReadOnlyList<string> lines) {
        var dir = (string)sourceMeta[DirKey]!;
        if ((string)sourceMeta[FormatKey]! == "legacy") return (Path.Combine(dir, "messages.jsonl"), false);

        var temp = Path.Combine(Path.GetTempPath(), $"kcap-vibe-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(temp, lines);
        return (temp, true);
    }

    static (string? SessionId, string? Cwd, DateTimeOffset? FirstTs) ReadMeta(string dir, string format) {
        var metaPath = Path.Combine(dir, "meta.json");
        string? sessionId = format == "unified" ? Path.GetFileName(dir) : null; // unified dir name IS the session id
        string? cwd = null;
        DateTimeOffset? firstTs = null;

        if (File.Exists(metaPath)) {
            try {
                using var doc = JsonDocument.Parse(File.ReadAllTextShared(metaPath));
                var root = doc.RootElement;
                sessionId ??= root.Str("session_id") ?? root.Str("sessionId") ?? root.Str("id");
                // Vibe nests the cwd under `environment.working_directory`, with `origin_directory` as
                // the top-level fallback; the session's own clock is `start_time`.
                cwd = root.Obj("environment")?.Str("working_directory") ?? root.Str("origin_directory");
                var ts = root.Str("start_time") ?? root.Str("created_at") ?? root.Str("startTime");
                if (ts is not null && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    firstTs = parsed;
            } catch { /* fall through to what we have */ }
        }

        firstTs ??= TryGetCreationUtc(dir);
        return (sessionId, cwd, firstTs);
    }

    static DateTimeOffset? TryGetCreationUtc(string dir) {
        try { return File.GetCreationTimeUtc(dir); } catch { return null; }
    }

    static int LastNonBlankIndex(IReadOnlyList<string> lines) {
        for (var i = lines.Count - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(lines[i])) return i;
        return 0;
    }

    // ── synthetic lifecycle POSTs (mirror of the other routed sources) ────────────────────────────

    static string SessionDir(ImportCommand.SessionClassification classification) =>
        (string)classification.SourceMeta![DirKey]!;

    // The generic session-start/end routes bind Claude-shaped records, which require transcript_path
    // and cwd and take an end reason only from their own set.
    static JsonObject BuildSessionStartPayload(string sessionId, string transcriptPath, string? cwd, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = cwd ?? "",
            ["source"]          = "startup",
        };
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) payload["workspace_root"] = workspaceRoot;
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static JsonObject BuildSessionEndPayload(string sessionId, string transcriptPath, string? cwd, DateTimeOffset? endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sessionId,
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = cwd ?? "",
            ["reason"]          = "Other",
        };
        if (endedAt is { } ts) payload["ended_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    async Task<bool> PostSyntheticHookAsync(HttpClient client, string baseUrl, string routeSegment, JsonObject payload, CancellationToken ct) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/{routeSegment}", content, time, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    async Task<int?> FetchServerLastLineAsync(HttpClient http, string baseUrl, string sessionId, CancellationToken ct) {
        using var resp = await http.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line", time, ct: ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt32() : null;
    }

    ImportCommand.SessionClassification Classify(
            DiscoveredSession s, SessionMetadata meta, ImportCommand.ClassificationStatus status, int totalLines, string? probeErrorReason = null) => new() {
        SessionId = s.SessionId, FilePath = "", EncodedCwd = "", Meta = meta, Status = status,
        Vendor = Vendor, ProbeErrorReason = probeErrorReason, TotalLines = totalLines, SourceMeta = s.SourceMeta,
    };
}
