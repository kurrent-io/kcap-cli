namespace Capacitor.Cli.Core;

public static class GitRepository {
    /// <summary>
    /// Walks up the directory tree from <paramref name="startDir"/> looking for a
    /// <c>.git</c> entry (either a directory, as in a normal working tree, or a file,
    /// as in submodules and linked worktrees). Returns the path that contains the
    /// <c>.git</c> entry, or <c>null</c> if no working tree is found before reaching
    /// the filesystem root. Filesystem errors are treated as "not found" rather than
    /// thrown — callers use this as a heuristic, not an authoritative check.
    /// </summary>
    public static string? FindRoot(string startDir) {
        if (string.IsNullOrEmpty(startDir)) return null;

        try {
            var dir = new DirectoryInfo(startDir);
            while (dir is not null) {
                var dotGit = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit)) return dir.FullName;
                dir = dir.Parent;
            }
        } catch {
            // I/O errors (permission denied on parent traversal, etc.) — treat as no repo.
        }

        return null;
    }

    public static bool IsInsideRepo(string startDir) => FindRoot(startDir) is not null;
}
