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

    /// <summary>
    /// Resolves a LINKED-WORKTREE checkout to its main repository root, so user-facing repository
    /// lists show actual repositories (GH #655). A linked worktree's <c>.git</c> is a file whose
    /// <c>gitdir:</c> target points into <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c> — that
    /// main root is returned. A submodule's <c>.git</c> file points into <c>.git/modules</c>
    /// instead and is left alone: a submodule is a real repository of its own. A path whose
    /// directory is gone (a removed worktree) can't be read, so it falls back to stripping the
    /// agent-infra patterns (<c>/.claude/worktrees/&lt;leaf&gt;</c>,
    /// <c>/.capacitor/worktrees/&lt;leaf&gt;</c>). Anything unrecognized comes back unchanged;
    /// never throws — a heuristic, like <see cref="FindRoot"/>.
    /// </summary>
    public static string ResolveMainRepoRoot(string path) {
        if (string.IsNullOrEmpty(path)) return path;

        try {
            var dotGit = Path.Combine(path, ".git");
            if (File.Exists(dotGit)) {
                var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
                var target = line?["gitdir:".Length..].Trim();
                if (string.IsNullOrEmpty(target)) return path;

                if (!Path.IsPathRooted(target)) target = Path.Combine(path, target);
                target = Path.GetFullPath(target);

                // Replace only swaps separators, so an index into the normalized copy slices the
                // original string correctly.
                var marker = target.Replace('\\', '/').LastIndexOf("/.git/worktrees/", StringComparison.Ordinal);
                return marker > 0 ? Path.GetFullPath(target[..marker]) : path;
            }

            if (Directory.Exists(path)) return path;
        } catch {
            // I/O errors — same "heuristic, not authoritative" stance as FindRoot.
        }

        return StripAgentWorktreeTail(path);
    }

    /// <summary>The git directory belonging to THIS working tree: <c>.git</c> in a main checkout,
    /// and the <c>gitdir:</c> target in a linked worktree, which is where per-worktree state
    /// belongs and where Git itself removes it with the worktree.</summary>
    public static string? ResolveGitDir(string startDir) {
        var root = FindRoot(startDir);
        if (root is null) return null;

        var dotGit = Path.Combine(root, ".git");

        try {
            // Resolving walks the path a component at a time, so it reads the filesystem and can
            // fail the same ways the pointer read below does — an unresolvable git directory is
            // this method's null, never an exception at the call site.
            if (Directory.Exists(dotGit)) return CanonicalPath.Resolve(dotGit);

            var line = File.ReadAllText(dotGit).Trim();
            const string marker = "gitdir:";
            if (!line.StartsWith(marker, StringComparison.Ordinal)) return null;

            var target = line[marker.Length..].Trim();
            if (!Path.IsPathRooted(target)) target = Path.Combine(root, target);
            return CanonicalPath.Resolve(target);
        } catch {
            return null;
        }
    }

    /// <summary>The branch checked out in <paramref name="startDir"/>'s working tree, read from its
    /// <c>HEAD</c> with no git process; null when detached or unreadable. A checkout can switch
    /// branch after launch, so a branch recorded at creation is not this.</summary>
    public static string? CurrentBranch(string? startDir) {
        if (string.IsNullOrEmpty(startDir) || ResolveGitDir(startDir) is not { } gitDir) return null;

        try {
            const string prefix = "ref: refs/heads/";
            var head = File.ReadAllTextShared(Path.Combine(gitDir, "HEAD")).Trim();
            return head.StartsWith(prefix, StringComparison.Ordinal) && head.Length > prefix.Length ? head[prefix.Length..] : null;
        } catch {
            return null;
        }
    }

    static string StripAgentWorktreeTail(string path) {
        var normalized = path.Replace('\\', '/');
        foreach (var infra in (string[])["/.claude/worktrees/", "/.capacitor/worktrees/"]) {
            var i = normalized.LastIndexOf(infra, StringComparison.Ordinal);
            if (i > 0 && i + infra.Length < normalized.Length)
                return path[..i];
        }

        return path;
    }
}
