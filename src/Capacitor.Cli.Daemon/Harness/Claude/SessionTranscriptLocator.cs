using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// Locates the transcript a hosted Claude agent writes — and with it the agent's session id — in the
/// Claude Code project directory, so the daemon can report the id over its own authenticated
/// connection rather than depend on the agent's session-start hook reaching the server.
///
/// That directory is shared with every other Claude session in the same folder: an owned worktree's
/// project dir is a symlink onto the source repo's (see <c>ClaudeLauncher.SymlinkClaudeProjectDir</c>),
/// and a borrowed cwd is the user's own checkout. A session id the daemon already holds is therefore
/// looked up by file name (<see cref="TryLocateSession"/>). Without one, a candidate's early
/// <c>"cwd"</c> lines must equal the agent's worktree path (<see cref="TryLocateWinner"/>), which
/// proves the file is the agent's only when that path is a worktree created for it.
/// </summary>
internal static class SessionTranscriptLocator {
    /// <summary>
    /// How many non-blank transcript lines to inspect for a <c>cwd</c> match before
    /// giving up on a candidate file. The cwd appears on the very first lines of a
    /// transcript, so 50 is generous while still bounding the read.
    /// </summary>
    public const int MaxLinesToInspect = 50;

    /// <summary>
    /// Slack applied to the spawn-time filter to absorb filesystem timestamp
    /// granularity (e.g. FAT's 2 s) and small clock quirks. Safe to be generous:
    /// the cwd check is the real disambiguator; the time filter only avoids re-reading old files.
    /// </summary>
    static readonly TimeSpan FileTimeSkewTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The normalized (dashless, lowercase) session id of the transcript
    /// <see cref="TryLocateWinner"/> picks, or null when no candidate matches yet.
    /// </summary>
    public static string? TryLocate(string projectDir, string worktreePath, DateTime spawnedAtUtc, ISet<string>? ruledOut = null) =>
        TryLocateWinner(projectDir, worktreePath, spawnedAtUtc, ruledOut)?.SessionId;

    /// <summary>
    /// Finds the named transcript in the current project first, then across sibling project directories.
    /// Distinct cross-project copies are ambiguous. Spawn time and cwd cannot identify a resumed session;
    /// its exact id names the file, with a dashed spelling as fallback for a dashless id.
    /// </summary>
    internal static (string SessionId, string Path)? TryLocateSession(string projectDir, string sessionId) {
        if (SessionIds.Canonical(sessionId) is not { } wanted || !Guid.TryParseExact(wanted, "N", out var guid)) return null;

        var names = new[] { sessionId.Trim(), guid.ToString("D") };
        if (FindIn(projectDir) is { } own) return (wanted, own);

        try {
            if (Directory.GetParent(projectDir) is not { Exists: true } projects) return null;

            string? winner = null;
            foreach (var sibling in projects.EnumerateDirectories()) {
                if (FindIn(sibling.FullName) is not { } candidate) continue;
                if (winner is not null && !string.Equals(winner, candidate, DefaultPathComparison)) return null;
                winner = candidate;
            }

            return winner is null ? null : (wanted, winner);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        string? FindIn(string directory) {
            var resolved = ResolveDirectory(directory);
            foreach (var name in names) {
                var path = Path.Combine(resolved, name + ".jsonl");
                if (File.Exists(path)) return path;
            }

            return null;
        }
    }

    /// <summary>
    /// Scans <paramref name="projectDir"/> for a transcript written at/after
    /// <paramref name="spawnedAtUtc"/> whose early lines report <c>cwd</c> equal to
    /// <paramref name="worktreePath"/>, and returns its normalized (dashless, lowercase) session
    /// id together with its path — link-resolved, because the per-worktree project dir is a
    /// symlink the launcher deletes at cleanup and a path through it would die with the process
    /// while the file lives on in the source repo's project dir. Null when no candidate matches
    /// yet. Best-effort: unreadable or malformed candidates are skipped, never thrown.
    /// </summary>
    /// <param name="ruledOut">
    /// Optional set of file paths already confirmed to belong to another session (or to not be a
    /// transcript at all). Since the caller polls every few seconds over a shared project dir,
    /// caching definitive non-matches here avoids re-opening the user's own concurrently-written
    /// sessions on every tick. Only <em>definitive</em> non-matches are added: a file whose name
    /// isn't a session id, or one whose <c>cwd</c> is present but foreign (a cwd never changes).
    /// Files with no <c>cwd</c> yet (still being written) are left out so the agent's own
    /// freshly-created transcript is always re-checked.
    /// </param>
    internal static (string SessionId, string Path)? TryLocateWinner(string projectDir, string worktreePath, DateTime spawnedAtUtc, ISet<string>? ruledOut = null) {
        if (!Directory.Exists(projectDir)) return null;

        foreach (var file in EnumerateTranscripts(projectDir)) {
            if (ruledOut?.Contains(file) == true) continue;

            try {
                if (SessionIdFromFileName(file) is not { } sessionId) {
                    ruledOut?.Add(file); // not a session transcript — its name won't change
                    continue;
                }

                if (!IsNewEnough(File.GetCreationTimeUtc(file), File.GetLastWriteTimeUtc(file), spawnedAtUtc)) {
                    continue;
                }

                switch (MatchTranscript(ReadFirstLines(file), worktreePath, DefaultPathComparison)) {
                    case CwdMatch.Yes: return (sessionId, file);
                    case CwdMatch.No:  ruledOut?.Add(file); break; // another session's transcript — cwd is fixed
                    // CwdMatch.Unknown: no cwd yet (file still being written) — leave uncached, re-check next tick.
                }
            } catch {
                // Candidate vanished mid-scan, is locked, or is otherwise unreadable — skip it;
                // the caller polls, so a transiently unreadable match is retried next tick.
            }
        }

        return null;
    }

    /// <summary>The project dir's <c>.jsonl</c> files under its link-resolved path: the path a caller keeps,
    /// and the one <c>ruledOut</c> is keyed by.</summary>
    static IEnumerable<string> EnumerateTranscripts(string projectDir) {
        var resolved = ResolveDirectory(projectDir);

        return Directory.EnumerateFiles(projectDir, "*.jsonl").Select(file => Path.Combine(resolved, Path.GetFileName(file)));
    }

    /// <summary>The directory a symlinked project dir points at (final target), or the directory itself.</summary>
    internal static string ResolveDirectory(string projectDir) {
        try {
            return new DirectoryInfo(projectDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? projectDir;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return projectDir;
        }
    }

    /// <summary>
    /// True when any of the first <see cref="MaxLinesToInspect"/> non-blank lines is a JSON
    /// object whose <c>cwd</c> equals <paramref name="worktreePath"/>. Tolerates partial or
    /// invalid JSON lines (skipped — the file is being written concurrently) and normalizes
    /// both paths (separator style, trailing separators) before comparing.
    /// </summary>
    /// <param name="comparison">
    /// Path comparison; defaults to case-insensitive on Windows, case-sensitive elsewhere.
    /// Injectable so tests can pin either behaviour regardless of the OS they run on.
    /// </param>
    public static bool TryMatchTranscript(IEnumerable<string> lines, string worktreePath, StringComparison? comparison = null)
        => MatchTranscript(lines, worktreePath, comparison ?? DefaultPathComparison) == CwdMatch.Yes;

    /// <summary>Outcome of inspecting a transcript's early lines for a <c>cwd</c>.</summary>
    internal enum CwdMatch {
        /// <summary>A <c>cwd</c> equal to the worktree path was found — this is the agent's transcript.</summary>
        Yes,
        /// <summary>A <c>cwd</c> was found but it belongs to a different session — a definitive, permanent non-match.</summary>
        No,
        /// <summary>No parseable <c>cwd</c> in the inspected lines — the file may still be being written; re-check later.</summary>
        Unknown,
    }

    /// <summary>
    /// Classifies a candidate transcript by scanning up to <see cref="MaxLinesToInspect"/> non-blank
    /// lines for a JSON <c>cwd</c>. Returns <see cref="CwdMatch.Yes"/> on the first line whose <c>cwd</c>
    /// equals <paramref name="worktreePath"/>, <see cref="CwdMatch.No"/> if a <c>cwd</c> was seen but none
    /// matched, or <see cref="CwdMatch.Unknown"/> if no <c>cwd</c> was seen at all. Partial or invalid
    /// JSON lines are tolerated (skipped — the file is being written concurrently) and both paths are
    /// normalized (separator style, trailing separators) before comparing.
    /// </summary>
    internal static CwdMatch MatchTranscript(IEnumerable<string> lines, string worktreePath, StringComparison comparison) {
        var target = NormalizePath(worktreePath);

        var inspected = 0;
        var sawCwd    = false;

        foreach (var line in lines) {
            if (inspected >= MaxLinesToInspect) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            inspected++;

            string? cwd;

            try {
                cwd = JsonNode.Parse(line)?["cwd"]?.GetValue<string>();
            } catch {
                continue; // partial/invalid JSONL line (or non-string cwd) — tolerate and move on
            }

            if (cwd is null) continue;

            sawCwd = true;

            if (string.Equals(NormalizePath(cwd), target, comparison)) return CwdMatch.Yes;
        }

        return sawCwd ? CwdMatch.No : CwdMatch.Unknown;
    }

    /// <summary>
    /// Derives the session id from a transcript file name: strips <c>.jsonl</c>, removes
    /// dashes and lowercases — the same canonical form the CLI's <c>NormalizeGuidField</c>
    /// produces for hook session ids. Returns null unless the stem is a GUID (32 hex chars
    /// once dashes are removed), so stray non-session <c>.jsonl</c> files never match.
    /// </summary>
    public static string? SessionIdFromFileName(string fileNameOrPath) {
        var name = Path.GetFileName(fileNameOrPath);

        if (!name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;

        var stem       = name[..^".jsonl".Length];
        var normalized = stem.Replace("-", "").ToLowerInvariant();

        if (normalized.Length != 32) return null;

        foreach (var c in normalized) {
            if (!Uri.IsHexDigit(c)) return null;
        }

        return normalized;
    }

    /// <summary>
    /// True when the file's newest timestamp (creation or last write) is at/after the
    /// agent's spawn time, minus <see cref="FileTimeSkewTolerance"/>. Filters out the
    /// user's own pre-existing sessions in the (shared, symlinked) project dir.
    /// </summary>
    public static bool IsNewEnough(DateTime creationTimeUtc, DateTime lastWriteTimeUtc, DateTime spawnedAtUtc) {
        var newest = creationTimeUtc > lastWriteTimeUtc ? creationTimeUtc : lastWriteTimeUtc;

        return newest >= spawnedAtUtc - FileTimeSkewTolerance;
    }

    static StringComparison DefaultPathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Unifies separator style and strips trailing separators so
    /// <c>C:\w\t\</c>, <c>C:/w/t</c> and <c>C:\w\t</c> all compare equal.</summary>
    static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Reads up to <see cref="MaxLinesToInspect"/> lines with <see cref="FileShare.ReadWrite"/>
    /// (Claude is appending to the file while we read) into memory, so the pure matcher never
    /// holds a file handle across JSON parsing.
    /// </summary>
    static List<string> ReadFirstLines(string path) {
        var lines = new List<string>(capacity: 16);

        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);

        while (lines.Count < MaxLinesToInspect && reader.ReadLine() is { } line) {
            lines.Add(line);
        }

        return lines;
    }
}
