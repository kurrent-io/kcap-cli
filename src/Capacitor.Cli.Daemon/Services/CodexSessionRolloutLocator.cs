using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Codex analog of <see cref="SessionTranscriptLocator"/>: discovers the session id of a
/// freshly spawned hosted Codex reviewer by scanning <c>~/.codex/sessions</c> for the rollout
/// file Codex writes.
///
/// Why this exists: a hosted Codex agent's session id normally reaches the server only via the
/// <c>kcap hook --codex</c> SessionStart POST (correlated by <c>KCAP_AGENT_ID</c>). When that
/// link doesn't land in time (unattended/borrowed correlation, or an auth hiccup) the agent has
/// no session id for correlation/display. Unlike Claude, Codex had no daemon-side fallback. The
/// server no longer <em>gates</em> incarnation completion on this id (it converges on daemon
/// liveness), so this is strictly best-effort — it resolves the id lazily when it can and never
/// blocks the launch.
///
/// Codex layout (verified against codex-cli 0.144.3): each session is
/// <c>~/.codex/sessions/YYYY/MM/DD/rollout-&lt;ISO-ts&gt;-&lt;uuid&gt;.jsonl</c>. The session id is
/// the trailing filename UUID (== the hook-reported <c>session_id</c> for a top-level CLI
/// session), parsed + normalized by <see cref="CodexPaths.ExtractSessionIdFromFileName"/>. The
/// cwd lives in the opening <c>session_meta</c> envelope's <c>payload.cwd</c> — NOT at the JSONL
/// root the way Claude records it.
///
/// Disambiguation: the sessions tree is shared across ALL of the user's Codex sessions, so a
/// new rollout there is not necessarily the reviewer's. Candidates are filtered by spawn time
/// and verified by <c>payload.cwd</c> equal to the agent's cwd. When more than one rollout still
/// matches (a rare borrowed-cwd race with the user's own concurrent session in the same repo),
/// the earliest one created at/after spawn is chosen — the reviewer's own rollout is created
/// immediately after the daemon spawns it.
///
/// The decision logic (cwd matching, spawn-time filtering) is pure and unit-tested without a
/// filesystem; only <see cref="TryLocate"/> touches disk.
/// </summary>
internal static class CodexSessionRolloutLocator {
    /// <summary>How many non-blank rollout lines to inspect for a <c>payload.cwd</c> before
    /// giving up on a candidate. The session_meta envelope is the very first line, so this is
    /// generous while still bounding the read.</summary>
    public const int MaxLinesToInspect = 50;

    /// <summary>Slack applied to the spawn-time filter to absorb filesystem timestamp
    /// granularity and small clock quirks. Safe to be generous: the cwd check is the real
    /// disambiguator; the time filter only avoids re-reading old sessions.</summary>
    static readonly TimeSpan FileTimeSkewTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Outcome of inspecting a rollout's early lines for a <c>payload.cwd</c>.</summary>
    internal enum CwdMatch {
        /// <summary>A <c>payload.cwd</c> equal to the agent cwd was found — this is the reviewer's rollout.</summary>
        Yes,
        /// <summary>A <c>payload.cwd</c> was found but belongs to a different session — a permanent non-match.</summary>
        No,
        /// <summary>No parseable <c>payload.cwd</c> in the inspected lines — may still be being written; re-check later.</summary>
        Unknown,
    }

    /// <summary>
    /// Scans <paramref name="sessionsRoot"/> (the <c>~/.codex/sessions</c> tree) for a rollout
    /// written at/after <paramref name="spawnedAtUtc"/> whose <c>session_meta</c> reports
    /// <c>payload.cwd</c> equal to <paramref name="cwd"/>. Returns the normalized (dashless,
    /// lowercase) session id, or null when no candidate matches yet. Best-effort: unreadable or
    /// malformed candidates are skipped, never thrown.
    /// </summary>
    /// <param name="ruledOut">
    /// Optional set of file paths already confirmed to belong to another session. The caller
    /// polls every few seconds over a shared tree, so caching definitive non-matches avoids
    /// re-opening the user's own concurrently-written sessions on every tick. Only
    /// <em>definitive</em> non-matches are added (a foreign cwd, or a name that isn't a rollout);
    /// a rollout with no cwd yet is left out so the reviewer's own file is always re-checked.
    /// </param>
    public static string? TryLocate(string sessionsRoot, string cwd, DateTime spawnedAtUtc, ISet<string>? ruledOut = null) {
        if (!Directory.Exists(sessionsRoot)) return null;

        string? earliestId       = null;
        var     earliestCreation = DateTime.MaxValue;

        foreach (var file in EnumerateRecentRolloutFiles(sessionsRoot, spawnedAtUtc)) {
            if (ruledOut?.Contains(file) == true) continue;

            try {
                if (CodexPaths.ExtractSessionIdFromFileName(file) is not { } sessionId) {
                    ruledOut?.Add(file); // not a rollout — its name won't change
                    continue;
                }

                var creation  = File.GetCreationTimeUtc(file);
                var lastWrite = File.GetLastWriteTimeUtc(file);

                if (!IsNewEnough(creation, lastWrite, spawnedAtUtc)) continue;

                switch (MatchRollout(ReadFirstLines(file), cwd, DefaultPathComparison)) {
                    case CwdMatch.Yes:
                        // Pick the earliest-created match: the reviewer's own rollout is created
                        // immediately after spawn, so a user's later session in the same cwd loses.
                        if (creation < earliestCreation) {
                            earliestCreation = creation;
                            earliestId       = sessionId;
                        }

                        break;
                    case CwdMatch.No:
                        ruledOut?.Add(file); // another session's rollout — cwd is fixed
                        break;
                    // CwdMatch.Unknown: no cwd yet — leave uncached, re-check next tick.
                }
            } catch {
                // Candidate vanished mid-scan, is locked, or is otherwise unreadable — skip it;
                // the caller polls, so a transiently unreadable match is retried next tick.
            }
        }

        return earliestId;
    }

    /// <summary>
    /// Classifies a candidate rollout by scanning up to <see cref="MaxLinesToInspect"/> non-blank
    /// lines for a JSON <c>payload.cwd</c> (the <c>session_meta</c> envelope). Returns
    /// <see cref="CwdMatch.Yes"/> on the first line whose <c>payload.cwd</c> equals
    /// <paramref name="cwd"/>, <see cref="CwdMatch.No"/> if a <c>payload.cwd</c> was seen but none
    /// matched, or <see cref="CwdMatch.Unknown"/> if none was seen. Partial/invalid JSON lines are
    /// tolerated (skipped) and both paths are normalized (separators, trailing separators) before
    /// comparing.
    /// </summary>
    internal static CwdMatch MatchRollout(IEnumerable<string> lines, string cwd, StringComparison comparison) {
        var target = NormalizePath(cwd);

        var inspected = 0;
        var sawCwd    = false;

        foreach (var line in lines) {
            if (inspected >= MaxLinesToInspect) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            inspected++;

            string? cwdValue;

            try {
                cwdValue = JsonNode.Parse(line)?["payload"]?["cwd"]?.GetValue<string>();
            } catch {
                continue; // partial/invalid JSONL line, non-object payload, or non-string cwd
            }

            if (cwdValue is null) continue;

            sawCwd = true;

            if (string.Equals(NormalizePath(cwdValue), target, comparison)) return CwdMatch.Yes;
        }

        return sawCwd ? CwdMatch.No : CwdMatch.Unknown;
    }

    /// <summary>
    /// True when the file's newest timestamp (creation or last write) is at/after the agent's
    /// spawn time, minus <see cref="FileTimeSkewTolerance"/>. Filters out the user's own
    /// pre-existing sessions in the shared tree.
    /// </summary>
    public static bool IsNewEnough(DateTime creationTimeUtc, DateTime lastWriteTimeUtc, DateTime spawnedAtUtc) {
        var newest = creationTimeUtc > lastWriteTimeUtc ? creationTimeUtc : lastWriteTimeUtc;

        return newest >= spawnedAtUtc - FileTimeSkewTolerance;
    }

    /// <summary>
    /// Enumerates <c>rollout-*.jsonl</c> files under <c>YYYY/MM/DD</c> day folders, pruning day
    /// folders older than the spawn's local date minus one day (Codex names folders by local
    /// date; the one-day slack covers a midnight boundary and timezone skew). Bounds the scan so
    /// a large history isn't walked on every poll tick.
    /// </summary>
    static IEnumerable<string> EnumerateRecentRolloutFiles(string sessionsRoot, DateTime spawnedAtUtc) {
        var cutoff = spawnedAtUtc.ToLocalTime().Date.AddDays(-1);

        foreach (var year in NumericDirs(sessionsRoot, cutoff.Year)) {
            foreach (var month in NumericDirs(year.Path, year.Value == cutoff.Year ? cutoff.Month : (int?)null)) {
                var dayMin = year.Value == cutoff.Year && month.Value == cutoff.Month ? cutoff.Day : (int?)null;

                foreach (var day in NumericDirs(month.Path, dayMin)) {
                    foreach (var file in Directory.EnumerateFiles(day.Path, "rollout-*.jsonl")) {
                        yield return file;
                    }
                }
            }
        }
    }

    static IEnumerable<(string Path, int Value)> NumericDirs(string parent, int? minInclusive) {
        if (!Directory.Exists(parent)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(parent)) {
            if (!int.TryParse(Path.GetFileName(dir), out var value)) continue;
            if (minInclusive is { } min && value < min) continue;

            yield return (dir, value);
        }
    }

    static StringComparison DefaultPathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Unifies separator style and strips trailing separators so
    /// <c>C:\r\p\</c>, <c>C:/r/p</c> and <c>C:\r\p</c> all compare equal.</summary>
    static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Reads up to <see cref="MaxLinesToInspect"/> lines with <see cref="FileShare.ReadWrite"/>
    /// (Codex is appending while we read) into memory, so the pure matcher never holds a file
    /// handle across JSON parsing.
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
