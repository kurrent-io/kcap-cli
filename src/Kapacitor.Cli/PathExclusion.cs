namespace Kapacitor.Cli;

/// <summary>
/// Path-based session exclusion. Matches a session's <c>cwd</c> against a
/// configured list of directories, treating descendants as excluded too.
/// Resolves symlinks at the leaf so worktree symlinks stored in config still
/// match cwds reported as the canonical path (or vice versa).
/// </summary>
static class PathExclusion {
    public static bool IsExcluded(string? cwd, IReadOnlyList<string>? excludedPaths) {
        if (cwd is null) return false;
        if (excludedPaths is null or { Count: 0 }) return false;

        string normalizedCwd;

        try {
            normalizedCwd = Normalize(cwd);
        } catch {
            return false;
        }

        foreach (var entry in excludedPaths) {
            string normalizedEntry;

            try {
                normalizedEntry = Normalize(entry);
            } catch {
                continue;
            }

            if (Contains(normalizedEntry, normalizedCwd)) return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a user-supplied path to an absolute, leaf-symlink-resolved form
    /// without a trailing separator. Expands <c>~</c> to the user's home dir.
    /// </summary>
    public static string Normalize(string path) {
        if (string.IsNullOrEmpty(path)) return path;

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
                        || path.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        var full = Path.GetFullPath(path);

        try {
            if (Directory.Exists(full)) {
                var info = new DirectoryInfo(full);
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);

                if (resolved is not null) full = resolved.FullName;
            }
        } catch {
            // best effort — non-existent paths stay as the abstract absolute form
        }

        if (full.Length > 1) {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full;
    }

    static bool Contains(string parent, string candidate) {
        // Path.GetRelativePath uses OS-appropriate case sensitivity (case-insensitive
        // on Windows/macOS, case-sensitive on Linux), so the containment check
        // matches platform path semantics without manual fiddling.
        var rel = Path.GetRelativePath(parent, candidate);

        if (rel == ".") return true;
        if (rel.StartsWith("..", StringComparison.Ordinal)) return false;
        if (Path.IsPathRooted(rel)) return false; // different volume/root

        return true;
    }
}
