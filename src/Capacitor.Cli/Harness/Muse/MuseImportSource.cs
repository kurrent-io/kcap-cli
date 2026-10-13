using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Harness.Muse;

/// <summary>
/// Discover + classify + import historical Muse Code sessions from
/// <c>&lt;sessions&gt;/yyyy/MM/dd/&lt;sessionId&gt;/session.jsonl</c>. The raw log is sent line for line, as the
/// live watcher sends it, and each nested <c>subagent/&lt;childId&gt;/session.jsonl</c> follows as a subagent of
/// the top-level session.
/// </summary>
internal sealed class MuseImportSource(MusePaths paths, TimeProvider time) : IImportSource {
    const string LogPathKey  = "LogPath";
    const string LastLineKey = "LastLine";
    const string EndedAtKey  = "EndedAt";

    // runtime.session.metadata is the log's second record; this bounds the head scan on a log that has none.
    const int HeadScanLines = 64;

    public HarnessId Vendor => HarnessId.Muse;
    public bool IsAvailable => Directory.Exists(paths.SessionsDir);
    public bool SupportsTitleGeneration => false; // the server titles from session.name.changed or the first prompt
    public bool AttachesChildContentOnReplay => true; // children are resent on every replay

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

        foreach (var (id, log) in EnumerateSessionLogs(ct)) {
            ct.ThrowIfCancellationRequested();

            try {
                var dashless = Guid.Parse(id).ToString("N");
                if (!seen.Add(dashless)) continue;
                if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal)) continue;

                var (workspaceRoot, firstTs) = ReadHead(log);
                if (workspaceRoot is null) continue;

                if (normalizedCwd is not null && !NormalizeForComparison(workspaceRoot).Equals(normalizedCwd, PathComparison)) continue;

                firstTs ??= TryGetCreationUtc(log);
                if (sinceUtc is { } cutoff && firstTs is { } ts && ts < cutoff) continue;

                result.Add(new DiscoveredSession(dashless, Vendor, workspaceRoot, firstTs,
                    new Dictionary<string, object?> { [LogPathKey] = log }));
            } catch {
                continue; // a hostile/unreadable session dir must not abort the scan
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    IEnumerable<(string Id, string Log)> EnumerateSessionLogs(CancellationToken ct) {
        if (!Directory.Exists(paths.SessionsDir)) yield break;

        foreach (var year in SafeDirectories(paths.SessionsDir).Where(d => IsDigits(Path.GetFileName(d))))
        foreach (var month in SafeDirectories(year))
        foreach (var day in SafeDirectories(month))
        foreach (var dir in SafeDirectories(day)) {
            ct.ThrowIfCancellationRequested();

            var id = Path.GetFileName(dir);
            if (!Guid.TryParse(id, out _)) continue;

            var log = Path.Combine(dir, "session.jsonl");
            if (File.Exists(log)) yield return (id, log);
        }
    }

    static IEnumerable<string> SafeDirectories(string dir) {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    static bool IsDigits(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);

    /// <summary>The workspace root of <c>runtime.session.metadata</c> (a stream without one is an
    /// approval-only stream, not a session) and the opening time, else the first record's.</summary>
    static (string? WorkspaceRoot, DateTimeOffset? FirstTimestamp) ReadHead(string log) {
        string? workspaceRoot = null;
        DateTimeOffset? opened = null, first = null;
        var scanned = 0;

        foreach (var line in File.ReadLinesShared(log)) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (++scanned > HeadScanLines) break;

            try {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.IsObject) continue;

                first ??= RecordedAt(root) ?? FrameRecordedAt(root);

                switch (root.Str("payload_type")) {
                    case "runtime.session.metadata" when workspaceRoot is null:
                        workspaceRoot = root.Obj("payload")?.Obj("record")?.Str("workspace_root");
                        break;
                    case "session.opened.observed" when opened is null:
                        opened = RecordedAt(root);
                        break;
                }
            } catch (JsonException) { }

            if (workspaceRoot is not null && opened is not null) break;
        }

        return (workspaceRoot, opened ?? first);
    }

    /// <summary>The last non-blank line index and the newest <c>session.end</c>, in one pass. A log whose
    /// last <c>session.opened.observed</c> follows its last end (a resume still running) or that has no
    /// end is unfinished: <c>EndedAt</c> is null, since a historical end must not close a live session.</summary>
    static (int NonBlank, int LastNonBlankIndex, DateTimeOffset? EndedAt) ReadTail(string log) {
        var nonBlank = 0;
        var lastIdx  = 0;
        DateTimeOffset? endedAt = null;
        var open  = false;
        var index = 0;

        foreach (var line in File.ReadLinesShared(log)) {
            if (!string.IsNullOrWhiteSpace(line)) {
                nonBlank++;
                lastIdx = index;

                var mentionsEnd    = line.Contains("\"session.end\"", StringComparison.Ordinal);
                var mentionsOpened = line.Contains("\"session.opened.observed\"", StringComparison.Ordinal);

                if (mentionsEnd || mentionsOpened) {
                    try {
                        using var doc = JsonDocument.Parse(line);

                        switch (doc.RootElement.Str("payload_type")) {
                            case "session.end":
                                endedAt = RecordedAt(doc.RootElement) ?? endedAt;
                                open    = false;
                                break;
                            case "session.opened.observed":
                                open = true;
                                break;
                        }
                    } catch (JsonException) { }
                }
            }

            index++;
        }

        return (nonBlank, lastIdx, open ? null : endedAt);
    }

    static DateTimeOffset? RecordedAt(JsonElement record) {
        if (record.Num("recorded_at") is not { } micros) return null;

        // 10 ticks per microsecond; bounded so a corrupt value cannot overflow DateTimeOffset.
        return micros is < 0 or > 253_402_300_799_000_000
            ? null
            : DateTimeOffset.UnixEpoch.AddTicks(micros * 10);
    }

    /// <summary>A retained permission frame has no <c>recorded_at</c> of its own, only its children's.</summary>
    static DateTimeOffset? FrameRecordedAt(JsonElement frame) {
        if (frame.Arr("children") is not { } children) return null;

        foreach (var child in children.EnumerateArray()) {
            if (child.Str("record_json") is not { } json) continue;

            try {
                using var doc = JsonDocument.Parse(json);
                if (RecordedAt(doc.RootElement) is { } at) return at;
            } catch (JsonException) { }
        }

        return null;
    }

    static DateTimeOffset? TryGetCreationUtc(string path) {
        try { return File.GetCreationTimeUtc(path); } catch { return null; }
    }

    static DateTimeOffset? TryGetLastWriteUtc(string path) {
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var log  = (string)s.SourceMeta![LogPathKey]!;
            var meta = new SessionMetadata { SessionId = s.SessionId, Cwd = s.Cwd, FirstTimestamp = s.FirstTimestamp };

            int nonBlank, lastIdx;
            DateTimeOffset? endedAt;
            try { (nonBlank, lastIdx, endedAt) = ReadTail(log); }
            catch { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, lastLine: 0, "transcript read failed")); continue; }

            if (nonBlank == 0) { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, lastLine: 0, "empty transcript")); continue; }
            if (nonBlank < ctx.MinLines) { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.TooShort, nonBlank, lastIdx)); continue; }

            int? serverLastLine;
            try { serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct); }
            catch { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, nonBlank, lastIdx, "watermark probe failed")); continue; }

            meta.LastTimestamp = endedAt ?? TryGetLastWriteUtc(log);

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

            if (serverLastLine is { } srv) {
                if (srv >= lastIdx) status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                else { status = ImportCommand.ClassificationStatus.Partial; resumeFromLn = srv + 1; }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId = s.SessionId, FilePath = "", EncodedCwd = "", Meta = meta,
                Status = status, Vendor = Vendor, ResumeFromLine = resumeFromLn, TotalLines = nonBlank,
                SourceMeta = SourceMetaFor(log, lastIdx, endedAt),
            });
        }

        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) {
        var log = (string)classification.SourceMeta![LogPathKey]!;
        if (!File.Exists(log)) return ImportOutcome.Failed;

        // A transcript advancing the watermark past a failed lifecycle POST would strand the session
        // lifecycle-less; re-runs are idempotent server-side.
        var startPayload = BuildSessionStartPayload(classification.SessionId, log, classification.Meta.Cwd, classification.Meta.FirstTimestamp);
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) startPayload["default_visibility"] = visibility;
        if (!await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/muse", startPayload, ct)) return ImportOutcome.Failed;

        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => (int)classification.SourceMeta![LastLineKey]! + 1,
            _                                                => 0,
        };

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: classification.SessionId,
                filePath: log, agentId: null, startLine: startLine, time: time, vendor: Vendor, progress: ctx.Progress);
        } catch {
            return ImportOutcome.Failed;
        }

        // Children land before session-end so their lifecycle sits inside the parent's. An unfinished
        // log posts no end; the server's stale-session closers own an abandoned one.
        var sentChildContent = await ImportChildrenAsync(ctx, classification.SessionId, log, ct);

        if (classification.SourceMeta![EndedAtKey] is DateTimeOffset endedAt
         && !await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/muse",
                BuildSessionEndPayload(classification.SessionId, classification.Meta.Cwd, endedAt), ct))
            return ImportOutcome.Failed;

        var outcome = startLine > 0 ? ImportOutcome.Resumed : sent == 0 ? ImportOutcome.Skipped : ImportOutcome.Loaded;

        return new ImportSessionResult(outcome, sentChildContent);
    }

    /// <summary>Every child resends from line 0 (its server ids are deterministic), so
    /// <c>SentChildContent</c> reads true on any replay of a session that has one. A refused
    /// subagent-start skips the child's content, and a failed batch leaves its subagent-stop unsent,
    /// so a re-import retries both; neither fails the already-imported parent.</summary>
    async Task<bool> ImportChildrenAsync(ImportContext ctx, string parentSessionId, string parentLog, CancellationToken ct) {
        var labels = MuseSubagentDiscovery.ResolveChildLabels(parentLog);
        var anySent = false;

        foreach (var childLog in MuseSubagentDiscovery.EnumerateChildLogs(parentLog)) {
            ct.ThrowIfCancellationRequested();

            var label = MuseSubagentDiscovery.LabelFor(Path.GetFileName(Path.GetDirectoryName(childLog))!, labels);

            if (!await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "subagent-start",
                    MuseSubagentDiscovery.BuildStartPayload(parentSessionId, label.AgentId, label.AgentType, childLog), ct))
                continue;

            int childSent;
            try {
                childSent = await SessionImporter.SendTranscriptBatches(
                    httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: parentSessionId, filePath: childLog,
                    agentId: label.AgentId, startLine: 0, time: time, vendor: Vendor, failOnError: true, progress: ctx.Progress);
            } catch {
                continue;
            }

            if (childSent > 0) anySent = true;

            await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "subagent-stop",
                MuseSubagentDiscovery.BuildStopPayload(parentSessionId, label.AgentId, label.AgentType, childLog), ct);
        }

        return anySent;
    }

    static JsonObject BuildSessionStartPayload(string sessionId, string log, string? cwd, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["transcript_path"] = log,
            ["cwd"]             = cwd ?? "",
            ["source"]          = "startup",
        };
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) payload["workspace_root"] = workspaceRoot;
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static JsonObject BuildSessionEndPayload(string sessionId, string? cwd, DateTimeOffset endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sessionId,
            ["cwd"]             = cwd ?? "",
            ["reason"]          = "Other",
        };
        payload["ended_at"] = endedAt.ToString("O");
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
        return doc.RootElement.Num("last_line_number") is { } ln ? (int)ln : null;
    }

    static Dictionary<string, object?> SourceMetaFor(string log, int lastLine, DateTimeOffset? endedAt = null) =>
        new() { [LogPathKey] = log, [LastLineKey] = lastLine, [EndedAtKey] = endedAt };

    ImportCommand.SessionClassification Classify(
            DiscoveredSession s, SessionMetadata meta, ImportCommand.ClassificationStatus status, int totalLines, int lastLine,
            string? probeErrorReason = null) => new() {
        SessionId = s.SessionId, FilePath = "", EncodedCwd = "", Meta = meta, Status = status,
        Vendor = Vendor, ProbeErrorReason = probeErrorReason, TotalLines = totalLines,
        SourceMeta = SourceMetaFor((string)s.SourceMeta![LogPathKey]!, lastLine),
    };
}
