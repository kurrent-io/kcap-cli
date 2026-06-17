using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Kiro;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Discover + classify + import historical AWS Kiro CLI sessions from the SQLite
/// <c>data.sqlite3</c> DB (<c>conversations_v2</c>, legacy fallback
/// <c>conversations</c>). Unlike the file-tailing vendors there is no on-disk
/// JSONL: <see cref="KiroTranscriptReader"/> flattens each
/// <c>ConversationState</c> blob into the per-turn envelope the server
/// normalizer consumes, so historical and live import converge on the same
/// <c>KiroTranscriptNormalizer</c>. Every flattened line maps to a canonical
/// event (a <c>session</c> header + one <c>turn</c> per history entry), so the
/// "last import-relevant line" is simply the last line — no skip-list to keep in
/// sync (contrast Copilot's noise-line filtering).
/// </summary>
internal sealed class KiroImportSource : IImportSource {
    readonly string                                 _dbPath;
    readonly Func<string, Task<RepositoryPayload?>> _repoDetector;

    public KiroImportSource(
        string?                                 dbPathOverride = null,
        Func<string, Task<RepositoryPayload?>>? repoDetector   = null
    ) {
        _dbPath       = dbPathOverride ?? KiroPaths.DbPath();
        _repoDetector = repoDetector ?? RepositoryDetection.DetectRepositoryAsync;
    }

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    static string NormalizeForComparison(string path) {
        try {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        } catch {
            return path.TrimEnd('/', '\\');
        }
    }

    public string Vendor => "kiro";

    public bool IsAvailable => File.Exists(_dbPath);

    /// <summary>
    /// False — Kiro carries no session title in the conversation blob, so the
    /// server's session-end handler derives a fallback title from the first user
    /// message. (Scheduling LLM title generation would need the on-disk
    /// transcript the file-based vendors have; Kiro has none.)
    /// </summary>
    public bool SupportsTitleGeneration => false;

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();

        foreach (var row in KiroTranscriptReader.DiscoverAll(_dbPath)) {
            ct.ThrowIfCancellationRequested();

            var dashless = row.ConversationId.Replace("-", "");

            if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                continue;

            if (normalizedCwd is not null
             && (row.Cwd is null || !NormalizeForComparison(row.Cwd).Equals(normalizedCwd, PathComparison)))
                continue;

            // started_at proxy: the DB created_at (or the first turn's timestamp
            // baked into the flattened header by FlattenRow).
            var firstTimestamp = row.CreatedAt;

            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            // Flatten now (post-filter) so classify/import reuse it without a
            // second SQLite read.
            var lines = KiroTranscriptReader.FlattenRow(row);
            if (lines.Count == 0) continue;

            result.Add(new DiscoveredSession(
                SessionId:      dashless,
                Vendor:         Vendor,
                Cwd:            row.Cwd,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["ConversationId"] = row.ConversationId,
                    ["Cwd"]            = row.Cwd,
                    ["Lines"]          = lines,
                    ["LastTimestamp"]  = row.UpdatedAt,
                }));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            CancellationToken                ct
        ) {
        var results     = new List<ImportCommand.SessionClassification>(sessions.Count);
        var repoCache   = new Dictionary<string, string?>(StringComparer.Ordinal);
        var hasExcludes = ctx.ExcludedRepos is { Count: > 0 };

        foreach (var s in sessions) {
            var lines = (List<string>)s.SourceMeta!["Lines"]!;

            var meta = new SessionMetadata {
                SessionId      = s.SessionId,
                Cwd            = s.Cwd,
                FirstTimestamp = s.FirstTimestamp,
                LastTimestamp  = s.SourceMeta!.TryGetValue("LastTimestamp", out var lt) ? lt as DateTimeOffset? : null,
            };

            var nonBlankCount = lines.Count(l => !string.IsNullOrWhiteSpace(l));

            if (nonBlankCount < ctx.MinLines) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.TooShort, totalLines: nonBlankCount));
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: nonBlankCount,
                                               probeErrorReason: "watermark probe failed"));
                continue;
            }

            string? repoKey = null;
            if (hasExcludes && s.Cwd is { } cwd) {
                if (!repoCache.TryGetValue(cwd, out repoKey)) {
                    try {
                        var repo = await _repoDetector(cwd);
                        repoKey = repo is { Owner: { } o, RepoName: { } n } ? $"{o}/{n}" : null;
                    } catch {
                        repoKey = null;
                    }
                    repoCache[cwd] = repoKey;
                }
            }

            var (excludedRepoKey, excludedPathKey) = ResolveExclusions(s.Cwd, repoKey, ctx);

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

            // Every flattened Kiro line emits a canonical event, so the last
            // import-relevant line is just the last line index.
            var lastImportable = lines.Count - 1;

            if (serverLastLine is { } srv) {
                if (srv >= lastImportable) {
                    status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                } else {
                    status       = ImportCommand.ClassificationStatus.Partial;
                    resumeFromLn = srv + 1;
                }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId       = s.SessionId,
                // Empty FilePath keeps Kiro on the routed phase (ImportSessionAsync
                // below) — same contract as Cursor/Copilot.
                FilePath        = "",
                EncodedCwd      = "",
                Meta            = meta,
                Status          = status,
                Vendor          = Vendor,
                ResumeFromLine  = resumeFromLn,
                ExcludedRepoKey = excludedRepoKey,
                ExcludedPathKey = excludedPathKey,
                TotalLines      = nonBlankCount,
                SourceMeta      = s.SourceMeta,
            });
        }

        return results;
    }

    public async Task<ImportOutcome> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) {
        var lines = (List<string>)classification.SourceMeta!["Lines"]!;
        if (lines.Count == 0) return ImportOutcome.Skipped;

        var cwd = classification.SourceMeta!.TryGetValue("Cwd", out var cwdObj) ? cwdObj as string : null;

        // Lifecycle-before-transcript ordering (see CursorImportSource): a
        // transcript that advances the watermark past a failed lifecycle POST
        // would leave the session permanently lifecycle-less. Re-runs are
        // idempotent server-side (deterministic lifecycle event ids).
        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/kiro",
            BuildSessionStartPayload(classification.SessionId, cwd, classification.Meta.FirstTimestamp),
            ct);
        if (!startOk) return ImportOutcome.Failed;

        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        // SendTranscriptBatches reads from a file; Kiro has none, so materialize
        // the flattened lines to a temp file for the send, then clean up.
        var tempPath = Path.Combine(Path.GetTempPath(), $"kcap-kiro-import-{classification.SessionId}-{Guid.NewGuid():N}.jsonl");

        int sent;
        try {
            await File.WriteAllLinesAsync(tempPath, lines, ct);

            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient,
                baseUrl:    ctx.BaseUrl,
                sessionId:  classification.SessionId,
                filePath:   tempPath,
                agentId:    null,
                startLine:  startLine,
                vendor:     Vendor);
        } catch {
            return ImportOutcome.Failed;
        } finally {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }

        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/kiro",
            BuildSessionEndPayload(classification.SessionId, cwd, classification.Meta.LastTimestamp),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    static JsonObject BuildSessionStartPayload(string sessionId, string? cwd, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "agentSpawn",
            ["session_id"]      = sessionId,
        };
        if (cwd is not null) payload["cwd"] = cwd;
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        return payload;
    }

    static JsonObject BuildSessionEndPayload(string sessionId, string? cwd, DateTimeOffset? endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = "historical-import",
        };
        if (cwd is not null) payload["cwd"] = cwd;
        if (endedAt is { } ts) payload["ended_at"] = ts.ToString("O");
        return payload;
    }

    static async Task<bool> PostSyntheticHookAsync(
        HttpClient client, string baseUrl, string routeSegment, JsonObject payload, CancellationToken ct
    ) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/{routeSegment}", content, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    static ImportCommand.SessionClassification MakeClassification(
        DiscoveredSession                  s,
        SessionMetadata                    meta,
        ImportCommand.ClassificationStatus status,
        int                                totalLines,
        string?                            probeErrorReason = null
    ) => new() {
        SessionId        = s.SessionId,
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = "kiro",
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    static async Task<int?> FetchServerLastLineAsync(HttpClient http, string baseUrl, string sessionId, CancellationToken ct) {
        using var resp = await http.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line", ct: ct);

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");

        var       body = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(body);

        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32()
            : null;
    }

    static (string? ExcludedRepoKey, string? ExcludedPathKey) ResolveExclusions(
        string? cwd, string? repoKey, ClassifyContext ctx
    ) {
        string? excludedRepoKey = null;
        if (repoKey is not null && ctx.ExcludedRepos is { Count: > 0 } repos
         && repos.Any(r => string.Equals(r, repoKey, StringComparison.OrdinalIgnoreCase))) {
            excludedRepoKey = repoKey;
        }

        string? excludedPathKey = null;
        if (cwd is not null && ctx.ExcludedPaths is { Count: > 0 } paths) {
            foreach (var entry in paths) {
                if (PathExclusion.IsExcluded(cwd, [entry])) {
                    excludedPathKey = PathExclusion.Normalize(entry);
                    break;
                }
            }
        }
        return (excludedRepoKey, excludedPathKey);
    }
}
