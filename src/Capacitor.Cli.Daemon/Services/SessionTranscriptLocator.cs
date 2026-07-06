using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Discovers the session id of a freshly spawned hosted Claude agent by scanning the
/// Claude Code project directory for the transcript file the agent writes.
///
/// Why this exists: the web chat can only attach to a hosted agent once the server's
/// registry entry has a SessionId, and until now the ONLY source of that link was the
/// spawned Claude's session-start hook POSTing to <c>/hooks/session-start</c>. When that
/// hook fails (e.g. an expired kcap token → 401), the link is never made and the agent
/// page shows "Waiting for session to start..." forever even though the terminal works.
/// The daemon itself can make the link instead: Claude Code writes its transcript to
/// <c>{ClaudePaths.Projects}/{project-dir-hash}/{session-id}.jsonl</c>, so polling that
/// directory and reporting the discovered id over the daemon's own (authenticated)
/// SignalR connection is a hook-independent fallback.
///
/// Disambiguation: the daemon symlinks the worktree's project dir to the SOURCE repo's
/// project dir (see <c>ClaudeLauncher.SymlinkClaudeProjectDir</c>), which is shared with
/// the user's own sessions in that repo — so a new <c>.jsonl</c> there is not necessarily
/// this agent's. Candidates are verified by reading the first lines of the file and
/// requiring the JSON <c>"cwd"</c> field to equal the agent's worktree path (the worktree
/// is created per-agent, so a cwd match is definitive).
///
/// The decision logic (cwd matching, filename → session id parsing, timestamp filtering)
/// is pure and unit-tested without a filesystem; only <see cref="TryLocate"/> touches disk.
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
    /// the cwd check — against a worktree path created for this specific agent —
    /// is the real disambiguator; the time filter only avoids re-reading old files.
    /// </summary>
    static readonly TimeSpan FileTimeSkewTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Scans <paramref name="projectDir"/> for a transcript written at/after
    /// <paramref name="spawnedAtUtc"/> whose early lines report <c>cwd</c> equal to
    /// <paramref name="worktreePath"/>. Returns the normalized (dashless, lowercase)
    /// session id, or null when no candidate matches yet. Best-effort: unreadable or
    /// malformed candidates are skipped, never thrown.
    /// </summary>
    public static string? TryLocate(string projectDir, string worktreePath, DateTime spawnedAtUtc) {
        if (!Directory.Exists(projectDir)) return null;

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl")) {
            try {
                if (SessionIdFromFileName(file) is not { } sessionId) continue;

                if (!IsNewEnough(File.GetCreationTimeUtc(file), File.GetLastWriteTimeUtc(file), spawnedAtUtc)) continue;

                if (TryMatchTranscript(ReadFirstLines(file), worktreePath)) return sessionId;
            } catch {
                // Candidate vanished mid-scan, is locked, or is otherwise unreadable — skip it;
                // the caller polls, so a transiently unreadable match is retried next tick.
            }
        }

        return null;
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
    public static bool TryMatchTranscript(IEnumerable<string> lines, string worktreePath, StringComparison? comparison = null) {
        var cmp    = comparison ?? DefaultPathComparison;
        var target = NormalizePath(worktreePath);

        var inspected = 0;

        foreach (var line in lines) {
            if (inspected >= MaxLinesToInspect) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            inspected++;

            string? cwd;

            try {
                cwd = JsonNode.Parse(line)?["cwd"]?.GetValue<string>();
            } catch {
                continue; // partial/invalid JSONL line — tolerate and move on
            }

            if (cwd is not null && string.Equals(NormalizePath(cwd), target, cmp)) return true;
        }

        return false;
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
