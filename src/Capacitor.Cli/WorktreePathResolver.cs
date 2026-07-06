namespace Capacitor.Cli;

/// <summary>
/// Attributes ephemeral worktree cwds back to the parent project when the
/// worktree directory itself no longer exists on disk.
///
/// Sessions recorded inside a tool-managed worktree carry an absolute cwd of
/// the form <c>&lt;project&gt;/.&lt;dotseg&gt;/worktrees/&lt;slug&gt;[/&lt;tail&gt;]</c>
/// (e.g. <c>~/dev/kcap-cli/.claude/worktrees/adaptive-toasting-squid</c> or
/// <c>~/dev/kcap-cli/.capacitor/worktrees/agent-...</c>). When those worktrees
/// are cleaned up, the cwd no longer matches anything on disk and the session
/// drops out of any <c>--org</c> / <c>--repo</c> scope.
///
/// If the <c>&lt;project&gt;</c> prefix still exists, we transparently
/// attribute the session to it. This is intentionally pattern-based rather
/// than a hard-coded list of dot-segment names ("<c>.claude</c>",
/// "<c>.capacitor</c>", "<c>.git</c>", ...) because tools keep inventing new
/// worktree roots and we don't want to chase the list.
/// </summary>
static class WorktreePathResolver {
    public static (string Path, bool Stripped) Resolve(string cwd) =>
        Resolve(cwd, Directory.Exists);

    // Internal seam so tests can pin filesystem existence without touching disk.
    internal static (string Path, bool Stripped) Resolve(string cwd, Func<string, bool> exists) {
        if (string.IsNullOrEmpty(cwd)) return (cwd, false);

        // Run the cheap pure-string pattern scan FIRST. Import calls this
        // for every session cwd, and the vast majority don't match the
        // worktree pattern — short-circuiting before any filesystem probe
        // keeps the per-session cost to a substring scan.
        var stripped = StripWorktreeSuffix(cwd);

        if (stripped is null) return (cwd, false);
        if (exists(cwd)) return (cwd, false); // pattern matches but worktree wasn't cleaned up — leave it alone

        return exists(stripped) ? (stripped, true) : (cwd, false);
    }

    /// <summary>
    /// Scan <paramref name="path"/> for the earliest segment triple
    /// <c>.&lt;dotseg&gt;/worktrees/&lt;slug&gt;</c> (where <c>dotseg</c> starts
    /// with <c>.</c>, is at least 2 chars, and is not <c>..</c>) and return the
    /// path prefix immediately before the dot-segment. Accepts both <c>/</c>
    /// and <c>\</c> as separators so transcripts recorded on Windows
    /// resolve too. Returns null when no such segment exists, or when the
    /// pattern starts at the path root (no meaningful project prefix).
    /// </summary>
    internal static string? StripWorktreeSuffix(string path) {
        for (var i = 0; i < path.Length - 1; i++) {
            if (!CwdRemapper.IsSeparator(path[i]) || path[i + 1] != '.') continue;

            var dotStart = i + 1;
            var dotEnd   = NextSeparator(path, dotStart);

            if (dotEnd < 0) continue; // no following separator → no triple

            switch (dotEnd - dotStart) {
                case < 2:
                // ".."
                case 2 when path[dotStart + 1] == '.':
                    continue; // ".” alone is not a dot-segment
            }

            var wtStart = dotEnd + 1;
            var wtEnd   = NextSeparator(path, wtStart);

            if (wtEnd < 0) continue; // need a separator after "worktrees"
            if (path.AsSpan(wtStart, wtEnd - wtStart) is not "worktrees") continue;

            var slugStart = wtEnd + 1;

            if (slugStart >= path.Length || CwdRemapper.IsSeparator(path[slugStart])) continue;

            switch (i) {
                // Found the pattern. Refuse if it sits at the very root —
                // there's no meaningful project prefix to attribute to. "Root"
                // covers both Unix (`/.X/worktrees/...` → i == 0) and Windows
                // drive roots (`C:\.X\worktrees\...` → i == 2, prefix `C:`,
                // also `C:/.X/...`).
                case 0:
                case 2 when path[1] == ':' && char.IsLetter(path[0]):
                    return null;
                default:
                    return path[..i];
            }
        }

        return null;
    }

    static int NextSeparator(string s, int from) {
        for (var j = from; j < s.Length; j++) {
            if (CwdRemapper.IsSeparator(s[j])) return j;
        }

        return -1;
    }
}
