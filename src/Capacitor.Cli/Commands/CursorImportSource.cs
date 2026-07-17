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

    // AI-1358 (item 5): per-cwd cache of the detected repo payload, populated during
    // ImportSessionAsync. Historical import can walk many sessions that share one workspace
    // folder — without this, each session would re-run repo detection for the same cwd. Keyed
    // by the resolved workspace folder (Ordinal — the same string ImportSessionAsync already
    // compares against SourceMeta["WorkspaceFolder"], no case-folding needed for a cache key).
    // Caches the RepositoryPayload rather than the built JsonObject: a JsonNode can only ever
    // have one parent, so reusing the same JsonObject instance across two sessions' payloads
    // throws InvalidOperationException on the second attach — BuildRepositoryNode must be
    // called fresh per session from the cached payload.
    readonly Dictionary<string, RepositoryPayload?> _repoCache = new(StringComparer.Ordinal);

    public CursorImportSource(
        string?                                  projectsDirOverride         = null,
        string?                                  workspaceStorageDirOverride = null,
        Func<string, Task<RepositoryPayload?>>?  repoDetector                = null
    ) {
        _projectsDir         = projectsDirOverride         ?? CursorPaths.ProjectsDir();
        _workspaceStorageDir = workspaceStorageDirOverride ?? CursorPaths.Resolve().WorkspaceStorageDir;
        _sanitizedToFolder   = new Lazy<IReadOnlyDictionary<string, string?>>(BuildSanitizedToFolderMap);
        // AI-1358 (item 5): historical import must never attach a live PR to an old session —
        // stamping today's open PR onto a transcript from weeks ago is an anachronism, and it's
        // exactly the wasted `gh pr view` / `glab api` round-trip per cwd that AI-1122 already
        // skips for the other import sources. detectPullRequest:false still resolves
        // owner/repo/branch (BuildRepositoryNode's non-PR fields), so imported sessions keep
        // grouping under their repo — they just never carry pr_number/pr_title/pr_url/pr_head_ref.
        // The LIVE Cursor hook path (CursorHookCommand → EnrichWithRepositoryInfoFromCwd) is a
        // separate call site untouched by this default and keeps live PR detection.
        _repoDetector        = repoDetector ?? (cwd => RepositoryDetection.DetectRepositoryAsync(cwd, detectPullRequest: false));
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

    // Normalize separators to '/' and drop any trailing slash WITHOUT calling
    // Path.GetFullPath: on Windows GetFullPath rebases a driveless rooted path
    // (e.g. "/Users/me/dev/foo") onto the current drive ("C:\Users\me\dev\foo"),
    // which neither Cursor's sanitized project-dir naming nor a caller-supplied
    // --cwd ever carries — breaking the sanitized-key match (AI-820). Must stay
    // byte-identical to BuildSanitizedToFolderMap's normalization.
    static string NormalizeForComparison(string path) {
        var p = path.Replace('\\', '/').TrimEnd('/');

        return p.Length == 0 ? "/" : p;
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
            try {
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
                    try {
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
                    } catch {
                        // A hostile/inaccessible session subtree must not abort the whole scan.
                        continue;
                    }
                }
            } catch {
                // A hostile/inaccessible workspace subtree must not abort the whole scan.
                continue;
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

        // AI-1153: correlate subagent (child) sessions to their parent by prompt-hash across
        // all discovered transcripts. A child is ingested under the parent's AgentSubsession
        // stream *by the parent's own import* (subagent-start → transcript-with-agent_id →
        // subagent-stop, before the parent's session-end) rather than as a separate top-level
        // session. Doing it inside the parent import guarantees the parent's SessionEnded stays
        // the last event on its stream — a subagent-start posted after a parallel parent's
        // session-end would reactivate the ended parent (SubagentStarted is a reactivation event).
        //
        // AI-1156 (D5): the correlator's INPUT is widened to every session under the SAME
        // workspace's agent-transcripts/ dir — ignoring --session/--cwd/--since/scope for this
        // internal step (cheap local file reads only). Without this, a `--session <child>`
        // (or --cwd/--since-narrowed) import only ever sees the child in `sessions`, so it can
        // never discover the parent's Task prompt and the child would wrongly import top-level.
        // Repo detection below still runs ONLY on the filtered `sessions` slice — this widening
        // is purely a same-workspace file scan, no git/network work.
        var pathBySession = new Dictionary<string, string>(StringComparer.Ordinal);

        var sanitizedDirs = sessions
            .Select(s => s.SourceMeta!.TryGetValue("SanitizedDir", out var sd) ? sd as string : null)
            .Where(sd => sd is not null)
            .Select(sd => sd!)
            .Distinct(StringComparer.Ordinal);

        foreach (var sanitizedDir in sanitizedDirs)
            foreach (var (sid, path) in DiscoverSameWorkspaceSessionPaths(sanitizedDir))
                pathBySession[sid] = path;

        // Ensure every session actually being classified in this run is present even if its own
        // workspace directory couldn't be (re-)walked above (e.g. injected SourceMeta without a
        // SanitizedDir in a test double) — this run's own slice must never be lost.
        foreach (var s in sessions) {
            if (s.SourceMeta!.TryGetValue("TranscriptPath", out var tp) && tp is string p && p.Length > 0)
                pathBySession[s.SessionId] = p;
        }

        var subagentLinks    = CursorSubagentCorrelator.Correlate(pathBySession.Select(kv => (kv.Key, kv.Value)));
        var childrenByParent = new Dictionary<string, List<CursorSubagentChild>>(StringComparer.Ordinal);
        foreach (var (childId, lnk) in subagentLinks) {
            if (!childrenByParent.TryGetValue(lnk.ParentSessionId, out var kids)) {
                kids = [];
                childrenByParent[lnk.ParentSessionId] = kids;
            }
            kids.Add(new CursorSubagentChild(childId, pathBySession.GetValueOrDefault(childId, ""), lnk.SubagentType));
        }

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

            // Ended-at contract (AI-1358 A3): unlike Copilot (session.shutdown record) or
            // Gemini/Pi (per-record "timestamp" field), Cursor's Anthropic content-block
            // transcript carries no authoritative session-end record to tail-scan. The
            // fallback is: the last parsed user-wrapper timestamp when the normalizer
            // surfaced one (live-hook path), else this file's last-write time. Do NOT
            // treat a tail-scan of this transcript as authoritative — there is no
            // per-line timestamp field here the way there is for Gemini/Pi.
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
                SourceMeta      = StampSubagentMeta(s.SourceMeta!, s.SessionId, subagentLinks, childrenByParent),
            });
        }

        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) {
        // AI-1153: a correlated subagent child is imported by its parent (below), under the
        // parent's AgentSubsession stream — never as a standalone top-level session.
        //
        // AI-1156 (D5): this flag is no longer unconditional. ImportCommand reconciles `routed`
        // right after building it — an ORPHANED child (its correlated parent isn't itself part
        // of this run's plan) has IsSubagentChild/ParentSessionId cleared before reaching here,
        // so this check only still fires for a child whose parent IS about to run its own
        // ImportSessionAsync (and will import this child inline, below in that parent's call).
        // An orphan instead falls through to the ordinary standalone start→transcript→end path.
        if (classification.SourceMeta!.TryGetValue("IsSubagentChild", out var scObj) && scObj is true) {
            return ImportOutcome.Skipped;
        }

        var transcriptPath = (string)classification.SourceMeta!["TranscriptPath"]!;

        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        var workspaceFolder = classification.SourceMeta!.TryGetValue("WorkspaceFolder", out var wfObj)
            ? wfObj as string
            : null;

        var (createdUtc, modifiedUtc) = TryGetTranscriptTimes(transcriptPath);

        // AI-1152: detect the repo from the workspace folder so the synthetic
        // sessionStart carries a `repository` node → server emits RepositoryDetected
        // and the (historical/backfilled) session groups under its repo. Fail-open:
        // a non-git workspace or detection error leaves it null and unattributed.
        //
        // AI-1358 (item 5): cached per cwd — a historical import walks every session under a
        // workspace, and many share one cwd, so this only runs detection once per distinct
        // folder for the whole import rather than once per session.
        JsonObject? repositoryNode = null;
        if (workspaceFolder is not null) {
            if (!_repoCache.TryGetValue(workspaceFolder, out var repo)) {
                try {
                    repo = await _repoDetector(workspaceFolder);
                } catch {
                    repo = null;
                }
                _repoCache[workspaceFolder] = repo;
            }
            if (repo is not null) repositoryNode = RepositoryDetection.BuildRepositoryNode(repo);
        }

        // sessionStart MUST succeed before transcript advances the server
        // watermark — otherwise a transient lifecycle failure plus a successful
        // transcript would leave the session permanently lifecycle-less
        // (next run sees AlreadyLoaded and never re-emits). Treat lifecycle
        // POST failure as a hard import failure; the orchestrator surfaces
        // Errored and the user re-runs, which is idempotent on the server
        // (canonical event ids are deterministic — AI-731).
        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/cursor",
            BuildSessionStartPayload(classification.SessionId, workspaceFolder, transcriptPath, createdUtc, repositoryNode),
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

        // AI-1153: import this parent's subagent children BEFORE its session-end, so the
        // parent's SessionEnded remains the last event on its stream. A subagent-start
        // (reactivation event) appended after the parent's SessionEnded would flip the
        // ended parent back to Active. Hard-fail on child failure (same contract as the
        // parent lifecycle) so a re-run — idempotent via deterministic ids — repairs it.
        //
        // AI-1154 review fix (P1): track whether any child ACTUALLY sent new transcript bytes,
        // independent of the parent's own `sent`/`startLine`. An AlreadyLoaded parent (nothing
        // past its own watermark) can still attach a previously-unloaded child here — that's
        // real new work, and ImportCommand's IsLifecycleOnlyRoutedReplay must not suppress it.
        var sentChildContent = false;
        if (classification.SourceMeta!.TryGetValue("SubagentChildren", out var kidsObj)
         && kidsObj is List<CursorSubagentChild> children) {
            foreach (var child in children) {
                var (childOk, childSent) = await SendSubagentLifecycleAsync(classification.SessionId, child, ctx, ct);
                if (!childOk) return ImportOutcome.Failed;
                sentChildContent |= childSent;
            }
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

        if (sent == 0) {
            var noOwnContentOutcome = startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;
            return new ImportSessionResult(noOwnContentOutcome, SentChildContent: sentChildContent);
        }

        var outcome = startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
        return new ImportSessionResult(outcome, SentChildContent: sentChildContent);
    }

    /// <summary>A Cursor subagent child correlated to a parent: its own session id (used as the
    /// AgentSubsession agent id), its transcript path, and the parent's declared subagent_type.</summary>
    internal sealed record CursorSubagentChild(string SessionId, string TranscriptPath, string? SubagentType);

    /// <summary>
    /// Stamps subagent correlation onto a session's SourceMeta (SourceMeta is read-only, so a
    /// child/parent gets a fresh copy). Children carry <c>IsSubagentChild</c> + <c>ParentSessionId</c>
    /// (AI-1156 D5 — the parent id lets <see cref="ImportCommand"/> reconcile an orphaned child
    /// to standalone when the parent isn't itself part of this run's plan) so their own import
    /// no-ops (unless reconciled); parents carry <c>SubagentChildren</c> so they import them inline.
    /// </summary>
    static IReadOnlyDictionary<string, object?> StampSubagentMeta(
        IReadOnlyDictionary<string, object?>                       src,
        string                                                     sessionId,
        Dictionary<string, CursorSubagentCorrelator.SubagentLink>  links,
        Dictionary<string, List<CursorSubagentChild>>              childrenByParent
    ) {
        var isChild = links.TryGetValue(sessionId, out var link);
        var hasKids = childrenByParent.TryGetValue(sessionId, out var kids);
        if (!isChild && !hasKids) return src;

        var d = new Dictionary<string, object?>(src);
        if (isChild) {
            d["IsSubagentChild"] = true;
            d["ParentSessionId"] = link.ParentSessionId;
        }
        if (hasKids) d["SubagentChildren"] = kids;
        return d;
    }

    /// <summary>
    /// AI-1156 (D5): every session id → transcript path found directly under ONE workspace's
    /// <c>agent-transcripts/</c> directory (identified by its Cursor-sanitized folder name),
    /// with NO <c>--session</c>/<c>--cwd</c>/<c>--since</c>/scope filtering applied — cheap,
    /// local file reads only. Used exclusively to widen the subagent correlator's input so a
    /// filtered/scoped import (e.g. <c>--session &lt;child&gt;</c>) still sees the child's parent
    /// (and siblings) for correlation, even though the parent itself may fall outside this run's
    /// classify slice and never get imported in this run. Mirrors the per-workspace walk in
    /// <see cref="DiscoverAsync"/>, minus the session/cwd/since filters.
    /// </summary>
    IReadOnlyDictionary<string, string> DiscoverSameWorkspaceSessionPaths(string sanitizedDir) {
        var map            = new Dictionary<string, string>(StringComparer.Ordinal);
        var transcriptsDir = Path.Combine(_projectsDir, sanitizedDir, "agent-transcripts");

        if (!Directory.Exists(transcriptsDir)) return map;

        try {
            foreach (var sessionDir in Directory.EnumerateDirectories(transcriptsDir)) {
                try {
                    var sessionDirName = Path.GetFileName(sessionDir);
                    var jsonl          = Path.Combine(sessionDir, sessionDirName + ".jsonl");

                    if (!File.Exists(jsonl)) continue;

                    map[NormalizeCursorSessionId(sessionDirName)] = jsonl;
                } catch {
                    // A hostile/inaccessible session subtree must not abort the whole scan.
                    continue;
                }
            }
        } catch {
            // A hostile/inaccessible workspace subtree must not abort the whole scan.
        }

        return map;
    }

    /// <summary>
    /// AI-1153: ingest one Cursor subagent child under its parent's <c>AgentSubsession</c>
    /// stream. Mirrors <c>SessionImporter.SendAgentLifecycle</c>: <c>subagent-start</c>
    /// (session_id=parent, agent_id=child) → transcript batches routed with <c>agent_id</c>=child
    /// (resumed from the subsession watermark so re-imports don't repost the whole child) →
    /// <c>subagent-stop</c>. Called from the parent's <see cref="ImportSessionAsync"/> BEFORE the
    /// parent's session-end. Returns <c>(false, _)</c> on a hard failure (start/transcript/stop
    /// POST failed); a missing child transcript is skipped non-fatally (<c>(true, false)</c>). No
    /// standalone session lifecycle is emitted for the child, so it never becomes a top-level
    /// session card.
    ///
    /// <para>
    /// AI-1154 review fix (P1): also reports whether this call actually POSTed new transcript
    /// bytes for the child (<c>SentContent</c>) — the caller needs that to know real new work
    /// happened even when the PARENT's own <c>sent</c> count is zero (e.g. an AlreadyLoaded
    /// parent attaching a previously-unloaded child).
    /// </para>
    ///
    /// <para>
    /// AI-1154 review fix (P2, round-3): <c>SentContent</c> only reflects GENUINELY new content.
    /// A fail-open resend — the child subsession watermark probe itself threw, so
    /// <c>startLine</c> resets to 0 and the whole child is reposted — is server-side idempotent
    /// when the child was already complete, but <c>SendTranscriptBatches</c> still returns the
    /// count of lines POSTED (not "new"). That resend alone must NOT assert <c>SentContent</c>;
    /// only a resend where the watermark was genuinely known (the probe succeeded, whether it
    /// returned a value or nothing at all) counts as new content.
    /// </para>
    /// </summary>
    async Task<(bool Success, bool SentContent)> SendSubagentLifecycleAsync(
            string            parentSessionId,
            CursorSubagentChild child,
            ImportContext     ctx,
            CancellationToken ct
        ) {
        if (string.IsNullOrEmpty(child.TranscriptPath) || !File.Exists(child.TranscriptPath))
            return (true, false); // missing child transcript — skip, non-fatal

        var agentId      = child.SessionId; // the child session id doubles as the subagent id
        var subagentType = string.IsNullOrEmpty(child.SubagentType) ? "task" : child.SubagentType!;

        // subagent-start on the parent stream (creates AgentSubsession-{parent}-{child}).
        var startOk = await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "subagent-start", new JsonObject {
            ["hook_event_name"] = "subagent_start",
            ["session_id"]      = parentSessionId,
            ["agent_id"]        = agentId,
            ["agent_type"]      = subagentType,
            ["transcript_path"] = child.TranscriptPath, // required by HookBase
            ["cwd"]             = "",                    // required by HookBase
            ["strict"]          = true,                  // fail-closed: 500 if SubagentStarted isn't persisted
        }, ct);
        if (!startOk) return (false, false);

        // Resume from the subsession watermark (AgentSubsession-{parent}-{child}) so a re-import
        // doesn't repost the full child transcript every time. Fail-open to a full send.
        //
        // AI-1154 review fix (P2, round-3): a fail-open resend (the probe itself threw — e.g. a
        // transient 5xx) resets startLine to 0 and reposts the WHOLE child. Those events are
        // server-side idempotent duplicates when the child was already complete, but
        // SendTranscriptBatches still returns the number of lines POSTED (not "new"), so a naive
        // `childSent > 0` would wrongly assert SentContent=true — recreating the double-count /
        // re-privatization bug this signal exists to prevent. Track whether the watermark probe
        // itself succeeded (probeFailed) so a fail-open full repost can never assert "new content"
        // on its own; only a resend where the watermark WAS known (probe succeeded, whichever
        // value it returned) counts.
        var startLine   = 0;
        var probeFailed = false;
        try {
            if (await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, parentSessionId, ct, agentId) is { } last)
                startLine = last + 1;
        } catch {
            startLine   = 0;
            probeFailed = true;
        }

        int childSent;
        try {
            // failOnError: fail-closed like the parent lifecycle — a rejected/failed child
            // transcript POST must abort so the parent import fails and a re-run repairs it,
            // rather than leaving an empty completed subagent while reporting success.
            childSent = await SessionImporter.SendTranscriptBatches(
                httpClient:  ctx.HttpClient,
                baseUrl:     ctx.BaseUrl,
                sessionId:   parentSessionId,
                filePath:    child.TranscriptPath,
                agentId:     agentId,
                startLine:   startLine,
                vendor:      Vendor,
                failOnError: true);
        } catch {
            return (false, false);
        }

        // Full SubagentStopHook shape (mirrors the Gemini/OpenCode builders) — the server
        // binds all fields, so an incomplete body can be rejected before HandleSubagentStop.
        var stopOk = await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "subagent-stop", new JsonObject {
            ["hook_event_name"]        = "subagent_stop",
            ["session_id"]             = parentSessionId,
            ["agent_id"]               = agentId,
            ["agent_type"]             = subagentType,
            ["transcript_path"]        = child.TranscriptPath, // required by HookBase
            ["cwd"]                    = "",                   // required by HookBase
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = child.TranscriptPath,
            ["last_assistant_message"] = "",
            ["strict"]                 = true,                 // fail-closed: 500 if SubagentCompleted isn't persisted
        }, ct);

        return (stopOk, stopOk && childSent > 0 && !probeFailed);
    }

    internal static JsonObject BuildSessionStartPayload(
        string sessionId, string? workspaceFolder, string transcriptPath, DateTimeOffset? startedAt,
        JsonObject? repository = null
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
        // AI-1152: git-detected repo (owner/repo/branch/...) so the server emits
        // RepositoryDetected for historical/backfilled Cursor sessions.
        if (repository is not null) {
            payload["repository"] = repository;
        }
        // AI-739: server prefers started_at over UtcNow when present, so
        // historical sessions surface with their real start time. Use an
        // ISO-8601 round-trip ("O") string — DateTimeOffset? on the server
        // record deserialises that shape directly.
        if (startedAt is { } ts) {
            payload["started_at"] = ts.ToString("O");
        }
        payload["origin"] = ImportOrigins.Historical;
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
        payload["origin"] = ImportOrigins.Historical;
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

    static async Task<int?> FetchServerLastLineAsync(HttpClient http, string baseUrl, string sessionId, CancellationToken ct, string? agentId = null) {
        // agentId set → probe the AgentSubsession-{sessionId}-{agentId} watermark (AI-1153).
        var url = string.IsNullOrEmpty(agentId)
            ? $"{baseUrl}/api/sessions/{sessionId}/last-line"
            : $"{baseUrl}/api/sessions/{sessionId}/last-line?agentId={Uri.EscapeDataString(agentId)}";
        using var resp = await http.GetWithRetryAsync(url, ct: ct);

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

            // Separator-normalize only (see NormalizeForComparison): NOT
            // Path.GetFullPath, which injects the current drive for driveless
            // rooted paths on Windows and breaks the sanitized-key match.
            var normalized = NormalizeForComparison(StripFileUri(folder));

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
