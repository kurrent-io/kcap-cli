using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Harness.Grok;

/// <summary>
/// Discover, classify and import past Grok Build sessions from <c>~/.grok/sessions/&lt;cwd&gt;/&lt;id&gt;/</c>.
/// Each session's <c>updates.jsonl</c> is sent as it is, with <c>vendor: grok</c>, and the server
/// normalizes it. A subagent is its own session directory beside its parent's and is imported through
/// the parent, as a subagent of it, from the parent's <c>subagent_spawned</c> lines.
/// </summary>
/// <param name="detectRepository">The repository a session's cwd belongs to, without PR detection: an
/// imported session is historical, and today's open PR is not the one it worked on.</param>
internal sealed class GrokImportSource(GrokPaths paths, Func<string, Task<RepositoryPayload?>> detectRepository, TimeProvider time) : IImportSource {
    const string DirKey = "Dir";

    public HarnessId Vendor => HarnessId.Grok;
    public bool IsAvailable => Directory.Exists(paths.SessionsDir);
    public bool SupportsTitleGeneration => false;

    // The live watcher records only the parent, so an already-loaded parent still imports its subagents.
    public bool AttachesChildContentOnReplay => true;

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
        if (!Directory.Exists(paths.SessionsDir)) return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);

        foreach (var cwdDir in Directory.EnumerateDirectories(paths.SessionsDir)) {
            foreach (var dir in Directory.EnumerateDirectories(cwdDir)) {
                ct.ThrowIfCancellationRequested();
                try {
                    var id = Path.GetFileName(dir);
                    if (!Guid.TryParse(id, out _) || !File.Exists(Path.Combine(dir, "updates.jsonl"))) continue;

                    var summary = ReadSummary(dir);
                    if (summary.IsSubagent) continue;

                    var dashless = id.Replace("-", "");
                    if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal)) continue;

                    var cwd = summary.Cwd ?? CwdOf(cwdDir);
                    if (normalizedCwd is not null
                     && (cwd is null || !NormalizeForComparison(cwd).Equals(normalizedCwd, PathComparison))) continue;

                    if (sinceUtc is { } cutoff && summary.CreatedAt is { } ts && ts < cutoff) continue;

                    result.Add(new DiscoveredSession(dashless, Vendor, cwd, summary.CreatedAt,
                        new Dictionary<string, object?> { [DirKey] = dir }));
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    // An unreadable session directory must not abort the scan.
                }
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var dir  = (string)s.SourceMeta![DirKey]!;
            var meta = new SessionMetadata { SessionId = s.SessionId, Cwd = s.Cwd, FirstTimestamp = s.FirstTimestamp, LastTimestamp = ReadSummary(dir).UpdatedAt };

            IReadOnlyList<string> lines;
            try { lines = Lines(dir); }
            catch { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed")); continue; }

            var nonBlank = lines.Count(l => !string.IsNullOrWhiteSpace(l));
            if (nonBlank == 0) { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "empty transcript")); continue; }
            if (nonBlank < ctx.MinLines) { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.TooShort, nonBlank)); continue; }

            int? serverLastLine;
            try { serverLastLine = await LastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, agentId: null, ct); }
            catch (SessionDeletedException) { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.Deleted, nonBlank)); continue; }
            catch { results.Add(Classify(s, meta, ImportCommand.ClassificationStatus.ProbeError, nonBlank, "watermark probe failed")); continue; }

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

            if (serverLastLine is { } srv) {
                if (srv >= LastImportableIndex(lines)) status = ImportCommand.ClassificationStatus.AlreadyLoaded;
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
        var dir        = (string)classification.SourceMeta![DirKey]!;
        var transcript = Path.Combine(dir, "updates.jsonl");
        var sid        = classification.SessionId;

        // Lifecycle before content: a transcript past a failed start would strand the session without one.
        // Both are replayed for an already-loaded session, whose end may be the post a prior run lost.
        var start = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sid,
            ["cwd"]             = classification.Meta.Cwd ?? "",
            ["origin"]          = ImportOrigins.Historical,
        };
        if (classification.Meta.Cwd is { } cwd && GitRepository.FindRoot(cwd) is { } root) start["workspace_root"] = root;
        if (classification.Meta.FirstTimestamp is { } started) start["started_at"] = started.ToString("O");
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) start["default_visibility"] = visibility;
        if (!await PostAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/grok", start, ct)) return ImportOutcome.Failed;

        // Grok's session-start carries no repository, so it rides a transcript batch of its own. Best
        // effort: a session the server cannot attribute is still worth importing.
        if (classification.Meta.Cwd is { Length: > 0 } repoCwd && await detectRepository(repoCwd) is { } repo)
            await PostAsync(ctx.HttpClient, ctx.BaseUrl, "transcript", RepositoryBatch(sid, repo), ct);

        var alreadyLoaded = classification.Status == ImportCommand.ClassificationStatus.AlreadyLoaded;
        var startLine     = classification.Status == ImportCommand.ClassificationStatus.Partial ? classification.ResumeFromLine : 0;
        var sent          = 0;
        bool sentChildContent;
        try {
            // Strict, so a batch landing past a refused one cannot move the watermark beyond lines the server never took.
            if (!alreadyLoaded)
                sent = await SessionImporter.SendTranscriptBatches(
                    httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: sid,
                    filePath: transcript, agentId: null, startLine: startLine, time: time, vendor: Vendor,
                    failOnError: true, progress: ctx.Progress);

            sentChildContent = await ImportSubagentsAsync(ctx.HttpClient, ctx.BaseUrl, sid, transcript, ctx.Progress, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch {
            return ImportOutcome.Failed;
        }

        var end = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sid,
            ["reason"]          = "import",
            ["cwd"]             = classification.Meta.Cwd ?? "",
            ["origin"]          = ImportOrigins.Historical,
        };
        if (classification.Meta.LastTimestamp is { } ended) end["ended_at"] = ended.ToString("O");
        if (!await PostAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/grok", end, ct)) return ImportOutcome.Failed;

        var outcome = alreadyLoaded ? ImportOutcome.Skipped : startLine > 0 ? ImportOutcome.Resumed : sent == 0 ? ImportOutcome.Skipped : ImportOutcome.Loaded;

        return new ImportSessionResult(outcome, sentChildContent);
    }

    /// <summary>Each spawned subagent whose transcript is on disk, registered first (fail-closed) and
    /// resumed above what the server already holds for it. True when any of them sent content.</summary>
    async Task<bool> ImportSubagentsAsync(HttpClient client, string baseUrl, string sid, string parentTranscript, IProgress<ImportProgress>? progress, CancellationToken ct) {
        var spawned = GrokSubagents.Read(File.ReadLinesShared(parentTranscript))
            .Where(e => !e.Finished)
            .DistinctBy(e => e.ChildSessionId);

        var sentAny = false;

        foreach (var child in spawned) {
            ct.ThrowIfCancellationRequested();

            var transcript = GrokSubagents.ChildTranscript(parentTranscript, child.ChildSessionId);
            if (!File.Exists(transcript)) continue;

            var agentId   = GrokSubagents.AgentId(child.ChildSessionId);
            var agentType = child.SubagentType ?? "subagent";

            if (!await PostAsync(client, baseUrl, "subagent-start", GeminiSubagentDiscovery.BuildStartPayload(sid, agentId, agentType, transcript), ct))
                throw new HttpRequestException($"subagent-start failed for {child.ChildSessionId}");

            var watermark = await LastLineAsync(client, baseUrl, sid, agentId, ct);
            sentAny |= await SessionImporter.SendTranscriptBatches(
                httpClient: client, baseUrl: baseUrl, sessionId: sid,
                filePath: transcript, agentId: agentId, startLine: watermark is { } w ? w + 1 : 0,
                time: time, vendor: Vendor, failOnError: true, progress: progress) > 0;

            if (!await PostAsync(client, baseUrl, "subagent-stop", GeminiSubagentDiscovery.BuildStopPayload(sid, agentId, agentType, transcript), ct))
                throw new HttpRequestException($"subagent-stop failed for {child.ChildSessionId}");
        }

        return sentAny;
    }

    static IReadOnlyList<string> Lines(string dir) => File.ReadLinesShared(Path.Combine(dir, "updates.jsonl")).ToList();

    // Grok names a session's parent directory after its percent-encoded cwd, or a slug with the real
    // cwd in its .cwd file when the encoded form is too long.
    static string? CwdOf(string cwdDir) {
        var marker = Path.Combine(cwdDir, ".cwd");
        if (File.Exists(marker)) return File.ReadAllTextShared(marker).TrimEnd('\r', '\n');

        var name = Path.GetFileName(cwdDir);
        return name.Contains('%') ? Uri.UnescapeDataString(name) : null;
    }

    readonly record struct Summary(string? Cwd, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, bool IsSubagent);

    static Summary ReadSummary(string dir) {
        var path = Path.Combine(dir, "summary.json");
        if (!File.Exists(path)) return default;

        try {
            using var doc  = JsonDocument.Parse(File.ReadAllTextShared(path));
            var       root = doc.RootElement;

            return new(root.Obj("info")?.Str("cwd"), Time(root.Str("created_at")), Time(root.Str("updated_at")),
                       root.Str("session_kind") == "subagent");
        } catch (JsonException) {
            return default;
        }
    }

    static DateTimeOffset? Time(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) ? ts : null;

    // The server's watermark is the last line it mapped to an event, so this mirrors when the server's
    // Grok normalizer emits one; a trailing line it maps to nothing must not read as unsent.
    static int LastImportableIndex(IReadOnlyList<string> lines) {
        for (var i = lines.Count - 1; i >= 0; i--)
            if (IsImportable(lines[i])) return i;
        return 0;
    }

    static bool IsImportable(string line) {
        if (string.IsNullOrWhiteSpace(line)) return false;

        try {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.Obj("params")?.Obj("update") is not { } update) return false;

            return (doc.RootElement.Str("method"), update.Str("sessionUpdate")) switch {
                ("session/update", "user_message_chunk" or "agent_message_chunk" or "agent_thought_chunk")
                    => update.Obj("content")?.Str("text") is { Length: > 0 },
                ("session/update", "tool_call") => update.Str("toolCallId") is { Length: > 0 },
                ("session/update", "tool_call_update")
                    => update.Str("status") is "completed" or "failed" && update.Str("toolCallId") is { Length: > 0 },
                ("_x.ai/session/update", "turn_completed")
                    => update.Obj("usage")?.Obj("modelUsage")?.EnumerateObject().Any(HasUsage) == true,
                _ => false,
            };
        } catch (JsonException) {
            return false;
        }
    }

    static readonly string[] TokenBuckets = ["inputTokens", "cachedReadTokens", "outputTokens", "reasoningTokens"];

    // The server drops a model whose every token bucket is zero.
    static bool HasUsage(JsonProperty model) =>
        model.Name.Length > 0 && model.Value.IsObject && TokenBuckets.Any(k => model.Value.Num(k) > 0);

    static JsonObject RepositoryBatch(string sessionId, RepositoryPayload repo) {
        var node = new JsonObject();
        if (repo.Owner is not null)     node["owner"]      = repo.Owner;
        if (repo.RepoName is not null)  node["repo_name"]  = repo.RepoName;
        if (repo.Branch is not null)    node["branch"]     = repo.Branch;
        if (repo.RemoteUrl is not null) node["remote_url"] = repo.RemoteUrl;
        if (repo.UserName is not null)  node["user_name"]  = repo.UserName;
        if (repo.UserEmail is not null) node["user_email"] = repo.UserEmail;

        return new JsonObject {
            ["session_id"] = sessionId,
            ["lines"]      = new JsonArray(),
            ["vendor"]     = HarnessId.Grok.VendorId,
            ["repository"] = node,
        };
    }

    async Task<bool> PostAsync(HttpClient client, string baseUrl, string route, JsonObject payload, CancellationToken ct) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/{route}", content, time, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    async Task<int?> LastLineAsync(HttpClient http, string baseUrl, string sessionId, string? agentId, CancellationToken ct) {
        var url = $"{baseUrl}/api/sessions/{sessionId}/last-line" + (agentId is not null ? $"?agentId={agentId}" : "");
        using var resp = await http.GetWithRetryAsync(url, time, ct: ct);
        SessionDeletedException.ThrowIfDeleted(resp, sessionId);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt32() : null;
    }

    ImportCommand.SessionClassification Classify(
            DiscoveredSession s, SessionMetadata meta, ImportCommand.ClassificationStatus status, int totalLines, string? probeErrorReason = null) => new() {
        SessionId = s.SessionId, FilePath = "", EncodedCwd = "", Meta = meta, Status = status,
        Vendor = Vendor, ProbeErrorReason = probeErrorReason, TotalLines = totalLines, SourceMeta = s.SourceMeta,
    };
}
