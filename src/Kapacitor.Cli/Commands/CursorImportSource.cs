using System.Net;
using System.Text.Json;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Commands;

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
    readonly string                                     _projectsDir;
    readonly string                                     _workspaceStorageDir;
    readonly Lazy<IReadOnlyDictionary<string, string>>  _sanitizedToFolder;

    public CursorImportSource(string? projectsDirOverride = null, string? workspaceStorageDirOverride = null) {
        _projectsDir         = projectsDirOverride         ?? CursorPaths.ProjectsDir();
        _workspaceStorageDir = workspaceStorageDirOverride ?? CursorPaths.Resolve().WorkspaceStorageDir;
        _sanitizedToFolder   = new Lazy<IReadOnlyDictionary<string, string>>(BuildSanitizedToFolderMap);
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
        var normalizedCwd = filters.FilterCwd?.TrimEnd('/');
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var sanitizedMap = _sanitizedToFolder.Value;
        var result       = new List<DiscoveredSession>();

        foreach (var sanitizedDir in Directory.EnumerateDirectories(_projectsDir)) {
            var sanitized      = Path.GetFileName(sanitizedDir);
            var transcriptsDir = Path.Combine(sanitizedDir, "agent-transcripts");

            if (!Directory.Exists(transcriptsDir)) continue;

            sanitizedMap.TryGetValue(sanitized, out var workspaceFolder);

            if (normalizedCwd is not null
             && (workspaceFolder is null
              || !workspaceFolder.TrimEnd('/').Equals(normalizedCwd, StringComparison.Ordinal))) {
                continue;
            }

            foreach (var sessionDir in Directory.EnumerateDirectories(transcriptsDir)) {
                var sessionDirName = Path.GetFileName(sessionDir);
                var jsonl          = Path.Combine(sessionDir, sessionDirName + ".jsonl");

                if (!File.Exists(jsonl)) continue;

                var dashless = NormalizeCursorSessionId(sessionDirName);

                if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                    continue;

                DateTimeOffset? firstTimestamp = null;
                try {
                    firstTimestamp = File.GetCreationTimeUtc(jsonl);
                } catch {
                    // Best effort.
                }

                if (sinceUtc is { } cutoff) {
                    DateTimeOffset lastWrite;
                    try {
                        lastWrite = File.GetLastWriteTimeUtc(jsonl);
                    } catch {
                        lastWrite = DateTimeOffset.MinValue;
                    }
                    if (lastWrite < cutoff) continue;
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

            var (excludedRepoKey, excludedPathKey) = ResolveExclusions(s.Cwd, ctx);

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
                FilePath        = transcriptPath,
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

        var startLine = classification.Status == ImportCommand.ClassificationStatus.Partial
            ? classification.ResumeFromLine
            : 0;

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

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    static ImportCommand.SessionClassification MakeClassification(
        DiscoveredSession                  s,
        SessionMetadata                    meta,
        ImportCommand.ClassificationStatus status,
        int                                totalLines,
        string?                            probeErrorReason = null
    ) => new() {
        SessionId        = s.SessionId,
        FilePath         = (string)s.SourceMeta!["TranscriptPath"]!,
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

    static (string? ExcludedRepoKey, string? ExcludedPathKey) ResolveExclusions(string? cwd, ClassifyContext ctx) {
        // Repo exclusion is applied later by the orchestrator once it has
        // resolved owner/repo per session (ImportCommand.ResolveCursorRepos
        // walks each cwd into RepositoryDetection). Path exclusion needs only
        // the cwd, so we apply it inline here.
        string? excludedPathKey = null;
        if (cwd is not null && ctx.ExcludedPaths is { Count: > 0 } paths) {
            foreach (var entry in paths) {
                if (PathExclusion.IsExcluded(cwd, [entry])) {
                    excludedPathKey = PathExclusion.Normalize(entry);
                    break;
                }
            }
        }
        return (ExcludedRepoKey: null, ExcludedPathKey: excludedPathKey);
    }

    IReadOnlyDictionary<string, string> BuildSanitizedToFolderMap() {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

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
            map[sanitized] = normalized;
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
