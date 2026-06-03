using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Cursor;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Discover + classify + import historical Cursor agent sessions from
/// <c>~/.cursor/projects/&lt;sanitized&gt;/agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl</c>.
/// Each JSONL file is one session in Anthropic content-block format — the same
/// shape the live hook path ships per line via
/// <see cref="CursorTranscriptBackfill"/>, so historical and live import
/// converge on a single canonical-event stream on the server.
///
/// <para>
/// The <c>&lt;sanitized&gt;</c> path segment is Cursor's encoding of the
/// workspace folder (leading slash stripped, remaining separators rewritten
/// as <c>-</c>). The encoding is lossy — folder names that contain <c>-</c>
/// produce ambiguous reversals — so we don't try to invert it. Instead we
/// derive a sanitized-name → real-folder lookup by walking
/// <c>workspaceStorage/&lt;hash&gt;/workspace.json</c> and applying the same
/// forward encoding. Sessions whose <c>&lt;sanitized&gt;</c> doesn't match any
/// known workspace are still imported, just without <c>cwd</c> / git owner+repo
/// — the orchestrator's repo / path exclusion machinery is the only feature
/// that loses fidelity there.
/// </para>
/// </summary>
internal sealed class CursorImportSource : IImportSource {
    readonly string                                      _projectsDir;
    readonly string                                      _workspaceStorageDir;
    readonly Lazy<IReadOnlyDictionary<string, string?>>  _sanitizedToFolder;
    readonly Func<string, Task<RepositoryPayload?>>      _repoDetector;

    public CursorImportSource(
        string?                                  projectsDirOverride         = null,
        string?                                  workspaceStorageDirOverride = null,
        Func<string, Task<RepositoryPayload?>>?  repoDetector                = null
    ) {
        _projectsDir         = projectsDirOverride         ?? CursorPaths.ProjectsDir();
        _workspaceStorageDir = workspaceStorageDirOverride ?? CursorPaths.Resolve().WorkspaceStorageDir;
        _sanitizedToFolder   = new Lazy<IReadOnlyDictionary<string, string?>>(BuildSanitizedToFolderMap);
        _repoDetector        = repoDetector ?? RepositoryDetection.DetectRepositoryAsync;
    }

    /// <summary>
    /// On Windows and macOS the filesystem is case-insensitive; on Linux it
    /// isn't. The workspace.json scan and Cursor's sanitized path segment both
    /// preserve case, but two equally-valid folder paths can differ only in
    /// case on the case-insensitive platforms, so we compare cwd accordingly.
    /// </summary>
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

    public string Vendor => "cursor";

    public bool IsAvailable => Directory.Exists(_projectsDir);

    /// <summary>
    /// False — Cursor sessions ship a transcript-derived title via the live
    /// hook path. The historical importer feeds the same transcript route, so
    /// the server's title pipeline handles naming without help from the CLI.
    /// </summary>
    public bool SupportsTitleGeneration => false;

    /// <summary>
    /// Strip dashes from a Cursor session id. The server stores Cursor sessions
    /// under <c>AgentSession-{dashless}</c> streams, so the <c>--session</c>
    /// filter must compare dashless on both sides.
    /// </summary>
    public static string NormalizeCursorSessionId(string id) => id.Replace("-", "");

    /// <summary>
    /// Apply Cursor's workspace-path encoding: strip the leading separator and
    /// replace remaining ones with <c>-</c>. The reverse direction is ambiguous
    /// (folder names can contain <c>-</c>) so we go forward only.
    /// </summary>
    internal static string EncodeWorkspacePath(string folder) {
        var trimmed = folder.TrimStart('/', '\\');
        return trimmed.Replace('/', '-').Replace('\\', '-');
    }

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        if (!Directory.Exists(_projectsDir))
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>([]);

        var sessionFilter = filters.FilterSession is { } sf ? NormalizeCursorSessionId(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var sanitizedMap = _sanitizedToFolder.Value;
        var result       = new List<DiscoveredSession>();

        foreach (var sanitizedDir in Directory.EnumerateDirectories(_projectsDir)) {
            var sanitized      = Path.GetFileName(sanitizedDir);
            var transcriptsDir = Path.Combine(sanitizedDir, "agent-transcripts");

            if (!Directory.Exists(transcriptsDir)) continue;

            // sanitizedMap value is null when the sanitized key collided with
            // multiple distinct workspace folders (lossy encoding) — treat as
            // unknown rather than misattributing to one of them.
            sanitizedMap.TryGetValue(sanitized, out var workspaceFolder);

            if (normalizedCwd is not null
             && (workspaceFolder is null
              || !workspaceFolder.Equals(normalizedCwd, PathComparison))) {
                continue;
            }

            foreach (var sessionDir in Directory.EnumerateDirectories(transcriptsDir)) {
                var sessionDirName = Path.GetFileName(sessionDir);
                var jsonl          = Path.Combine(sessionDir, sessionDirName + ".jsonl");

                if (!File.Exists(jsonl)) continue;

                var dashless = NormalizeCursorSessionId(sessionDirName);

                if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                    continue;

                // Use the JSONL file's creation time as the session-start
                // proxy. Cursor's transcript JSONL lines carry no timestamp
                // field (Anthropic content-block format without metadata),
                // and the file is created when the session starts and
                // appended throughout — so birth-time on supported
                // filesystems (APFS, NTFS) is the closest proxy.
                //
                // `--since` MUST gate on session-start, not last-modified,
                // or any old session appended to after the cutoff would be
                // re-imported.
                //
                // Linux note: .NET's File.GetCreationTimeUtc on Linux ext4
                // falls back to mtime when btime isn't queryable. On those
                // hosts started_at == ended_at and `--since` effectively
                // gates on last-write. Acceptable degradation — most Cursor
                // users are on macOS/Windows and the production behavior is
                // still strictly better than not having `--since` at all.
                DateTimeOffset? firstTimestamp = null;
                try {
                    firstTimestamp = File.GetCreationTimeUtc(jsonl);
                } catch {
                    // Best effort.
                }

                if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) {
                    continue;
                }

                result.Add(new DiscoveredSession(
                    SessionId:      dashless,
                    Vendor:         Vendor,
                    Cwd:            workspaceFolder,
                    FirstTimestamp: firstTimestamp,
                    SourceMeta:     new Dictionary<string, object?> {
                        ["TranscriptPath"]  = jsonl,
                        ["WorkspaceFolder"] = workspaceFolder,
                        ["SanitizedDir"]    = sanitized,
                    }));
            }

            ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                   ctx,
            CancellationToken                 ct
        ) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        // Per-workspace repo cache so we only run RepositoryDetection once per
        // unique cwd in this Classify call — sessions cluster heavily inside
        // the same workspace folder.
        var repoCache    = new Dictionary<string, string?>(StringComparer.Ordinal); // cwd → "owner/repo" or null
        var hasExcludes  = ctx.ExcludedRepos is { Count: > 0 };

        foreach (var s in sessions) {
            var transcriptPath = (string)s.SourceMeta!["TranscriptPath"]!;

            var meta = new SessionMetadata {
                SessionId      = s.SessionId,
                Cwd            = s.Cwd,
                FirstTimestamp = s.FirstTimestamp,
            };

            int? lastNonBlankIndex;
            int  nonBlankCount;
            try {
                (lastNonBlankIndex, nonBlankCount) = await ReadTranscriptStatsAsync(transcriptPath, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "transcript read failed"));
                continue;
            }

            if (lastNonBlankIndex is null) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "empty transcript"));
                continue;
            }

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

            try {
                meta.LastTimestamp = File.GetLastWriteTimeUtc(transcriptPath);
            } catch {
                // Best effort.
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

            if (serverLastLine is { } srv) {
                if (srv >= lastNonBlankIndex.Value) {
                    status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                } else {
                    status       = ImportCommand.ClassificationStatus.Partial;
                    resumeFromLn = srv + 1;
                }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId       = s.SessionId,
                // FilePath stays empty: ImportCommand routes sessions with an
                // empty FilePath through the routed phase (ImportSessionAsync),
                // and sessions with a populated FilePath through the chain
                // worker — which assumes Claude/Codex-shaped lifecycle hooks
                // (/hooks/session-start without a vendor suffix, defaulting to
                // vendor=claude on the server). Cursor needs the routed phase
                // so the transcript posts via /hooks/transcript with
                // vendor:"cursor". The transcript path lives in SourceMeta.
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
        var transcriptPath = (string)classification.SourceMeta!["TranscriptPath"]!;

        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        var workspaceFolder = classification.SourceMeta!.TryGetValue("WorkspaceFolder", out var wfObj)
            ? wfObj as string
            : null;

        var (createdUtc, modifiedUtc) = TryGetTranscriptTimes(transcriptPath);

        // sessionStart MUST succeed before transcript advances the server
        // watermark — otherwise a transient lifecycle failure plus a successful
        // transcript would leave the session permanently lifecycle-less
        // (next run sees AlreadyLoaded and never re-emits). Treat lifecycle
        // POST failure as a hard import failure; the orchestrator surfaces
        // Errored and the user re-runs, which is idempotent on the server
        // (canonical event ids are deterministic — AI-731).
        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/cursor",
            BuildSessionStartPayload(classification.SessionId, workspaceFolder, transcriptPath, createdUtc),
            ct);
        if (!startOk) return ImportOutcome.Failed;

        // Partial → resume from server watermark + 1. AlreadyLoaded → nothing
        // past the watermark, so SendTranscriptBatches returns 0 immediately.
        // The routed-phase filter at ImportCommand.cs:749 now includes
        // AlreadyLoaded for Cursor so legacy lifecycle-less sessions get
        // re-synced; the transcript leg becomes a no-op in that case.
        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient,
                baseUrl:    ctx.BaseUrl,
                sessionId:  classification.SessionId,
                filePath:   transcriptPath,
                agentId:    null,
                startLine:  startLine,
                vendor:     Vendor);
        } catch {
            return ImportOutcome.Failed;
        }

        // sessionEnd: same hard-fail contract — if it can't be appended, the
        // session would otherwise look perpetually "active" in the read-model.
        var durationMs = createdUtc is { } c && modifiedUtc is { } m && m >= c
            ? (long?)(m - c).TotalMilliseconds
            : null;
        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/cursor",
            BuildSessionEndPayload(classification.SessionId, transcriptPath, durationMs, modifiedUtc),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    static JsonObject BuildSessionStartPayload(
        string sessionId, string? workspaceFolder, string transcriptPath, DateTimeOffset? startedAt
    ) {
        var payload = new JsonObject {
            ["hook_event_name"]     = "sessionStart",
            ["session_id"]          = sessionId,
            ["transcript_path"]     = transcriptPath,
            ["is_background_agent"] = false,
        };
        if (workspaceFolder is not null) {
            payload["workspace_roots"] = new JsonArray(workspaceFolder);
        }
        // AI-739: server prefers started_at over UtcNow when present, so
        // historical sessions surface with their real start time. Use an
        // ISO-8601 round-trip ("O") string — DateTimeOffset? on the server
        // record deserialises that shape directly.
        if (startedAt is { } ts) {
            payload["started_at"] = ts.ToString("O");
        }
        return payload;
    }

    static JsonObject BuildSessionEndPayload(
        string sessionId, string transcriptPath, long? durationMs, DateTimeOffset? endedAt
    ) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = "historical-import",
            ["transcript_path"] = transcriptPath,
        };
        if (durationMs is { } d) {
            payload["duration_ms"] = d;
        }
        if (endedAt is { } ts) {
            payload["ended_at"] = ts.ToString("O");
        }
        return payload;
    }

    static (DateTimeOffset? Created, DateTimeOffset? Modified) TryGetTranscriptTimes(string transcriptPath) {
        try {
            return (File.GetCreationTimeUtc(transcriptPath), File.GetLastWriteTimeUtc(transcriptPath));
        } catch {
            return (null, null);
        }
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
        // See FilePath note in ClassifyAsync — empty keeps Cursor on the
        // routed phase.
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = "cursor",
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    /// <summary>
    /// Single-pass read returning the largest line index of a non-blank line
    /// (matches the server's <c>last_line_number</c> convention — only non-blank
    /// lines get accepted and counted) and the non-blank line count for
    /// <c>--min-lines</c> filtering.
    /// </summary>
    static async Task<(int? LastNonBlankIndex, int NonBlankCount)> ReadTranscriptStatsAsync(
        string transcriptPath, CancellationToken ct
    ) {
        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        int? lastIdx = null;
        var  count   = 0;
        var  lineIdx = 0;

        while (await reader.ReadLineAsync(ct) is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) {
                lastIdx = lineIdx;
                count++;
            }
            lineIdx++;
        }
        return (lastIdx, count);
    }

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

    IReadOnlyDictionary<string, string?> BuildSanitizedToFolderMap() {
        // EncodeWorkspacePath is lossy — "/foo/bar" and "/foo-bar" both encode
        // to "foo-bar". When two distinct workspaces collide on the same
        // sanitized key we can't tell which one a given JSONL session belongs
        // to, so we mark the key ambiguous (null) and let discovery treat the
        // affected sessions as having no resolvable cwd. Picking one
        // arbitrarily would misattribute git owner+repo and risk applying the
        // wrong excluded-repo gating.
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (!Directory.Exists(_workspaceStorageDir)) return map;

        foreach (var subdir in Directory.EnumerateDirectories(_workspaceStorageDir)) {
            var wsJson = Path.Combine(subdir, "workspace.json");
            if (!File.Exists(wsJson)) continue;

            string? folder;
            try {
                using var doc = JsonDocument.Parse(File.ReadAllText(wsJson));
                folder = doc.RootElement.TryGetProperty("folder", out var f) ? f.GetString() : null;
            } catch {
                continue;
            }

            if (folder is null) continue;

            var    stripped = StripFileUri(folder);
            string normalized;
            try {
                normalized = Path.GetFullPath(stripped).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            } catch {
                continue;
            }

            var sanitized = EncodeWorkspacePath(normalized);

            if (map.TryGetValue(sanitized, out var existing)) {
                // Already ambiguous (null) — leave it. Otherwise: if the new
                // folder matches the existing one (case-insensitive on
                // macOS/Windows), keep the existing entry; if it differs,
                // collapse to ambiguous.
                if (existing is not null && !existing.Equals(normalized, PathComparison)) {
                    map[sanitized] = null;
                }
            } else {
                map[sanitized] = normalized;
            }
        }

        return map;
    }

    static string StripFileUri(string uri) {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return uri;

        var path = uri["file://".Length..];

        // Windows: file:///C:/foo → /C:/foo → C:/foo
        if (path.StartsWith('/') && path.Length > 2 && path[2] == ':') {
            path = path.TrimStart('/');
        }

        return Uri.UnescapeDataString(path);
    }
}
