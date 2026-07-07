using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Outcome of <see cref="BorrowAuthorizer.AuthorizeBorrowAsync"/>.</summary>
public readonly record struct BorrowAuthResult(bool Allowed, string? CanonicalCwd, string? CanonicalGitRoot, string? Reason);

/// <summary>
/// Decides whether a given cwd may be <i>borrowed</i> — a read-only reviewer run in it on
/// this daemon. Deliberately a standalone type with no same-origin check, since cross-repo
/// borrowing is expected (unlike the retired mirror-sync guard, which required an origin match).
///
/// Depends only on <see cref="DaemonConfig.IsRepoAllowed"/> and <see cref="GitRepository.FindRoot"/>,
/// both cheap/local, so this is unit-testable without a running daemon.
/// </summary>
public class BorrowAuthorizer(DaemonConfig config) {
    /// <summary>
    /// 1. Rejects a missing/non-existent path outright.
    /// 2. Canonicalizes the path (resolves symlinks + normalizes).
    /// 3. Finds the git root at or above the canonical path, if any.
    /// 4. Authorizes git-rooted cwds under the normal repo allowlist (empty allowlist = allow
    ///    all local repos); a non-repo cwd is authorized ONLY against a non-empty allowlist —
    ///    an empty allowlist never authorizes a non-repo directory, since "allow all local
    ///    repos" means repos, not arbitrary directories.
    /// </summary>
    public Task<BorrowAuthResult> AuthorizeBorrowAsync(string path) {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) {
            return Task.FromResult(new BorrowAuthResult(false, null, null, "path_absent"));
        }

        string canonicalCwd;
        try {
            canonicalCwd = Canonicalize(path);
        } catch {
            // realpath resolution failed (permission on an ancestor, transient FS error, …). Fail
            // CLOSED — never authorize against an unresolved path on this security boundary (Qodo #290 #3).
            return Task.FromResult(new BorrowAuthResult(false, null, null, "not_allowed"));
        }

        var canonicalGitRoot = GitRepository.FindRoot(canonicalCwd);

        var allowed = canonicalGitRoot is not null
            ? config.IsRepoAllowed(canonicalCwd) || config.IsRepoAllowed(canonicalGitRoot)
            : config.AllowedRepoPaths.Length > 0 && config.IsRepoAllowed(canonicalCwd);

        return Task.FromResult(new BorrowAuthResult(allowed, canonicalCwd, canonicalGitRoot, allowed ? null : "not_allowed"));
    }

    // A symlink cycle can't loop forever: cap total resolution steps and return the best-resolved
    // path on hitting the cap. 40 mirrors the common OS-level MAXSYMLINKS limit.
    const int MaxResolveSteps = 40;

    /// <summary>
    /// Resolves <paramref name="path"/> to its real, symlink-free, host-normalized form — a true
    /// <c>realpath</c> that resolves symlinks in EVERY path component, not just the leaf. This is a
    /// security boundary: an <i>ancestor</i> symlink must not let a directory that physically lives
    /// outside the operator's allowlisted tree textually match <see cref="DaemonConfig.IsRepoAllowed"/>
    /// (e.g. an allowlisted <c>/repos/*</c> containing a symlink <c>proj/linkdir</c> → <c>~/.ssh</c>).
    /// Also called by <c>WorktreeInfo.Borrowed</c> (Task A5) so both sides of a borrow compare
    /// canonical paths.
    /// </summary>
    public static string Canonicalize(string path) => RealPath(Path.GetFullPath(path));

    /// <summary>
    /// Walks the path root-first, resolving each accumulated prefix a single hop at a time: when a
    /// prefix is a symlink, its target replaces the prefix (an absolute target restarts from its own
    /// root; a relative one resolves against the already-canonical parent) and resolution continues
    /// against it, so symlink chains and ancestor symlinks are all followed. Bounded by
    /// <see cref="MaxResolveSteps"/> so a symlink cycle terminates at the best-resolved path.
    /// </summary>
    static string RealPath(string fullPath) {
        var root      = Path.GetPathRoot(fullPath) ?? "";
        var segments  = SplitSegments(fullPath[root.Length..]);
        var resolved  = root;
        var steps     = 0;

        while (segments.Count > 0) {
            if (steps++ >= MaxResolveSteps) {
                // Cycle guard: stitch the unresolved remainder back on and stop.
                return Path.GetFullPath(Path.Combine([resolved, ..segments]));
            }

            var next   = Path.Combine(resolved, segments.Dequeue());
            var target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: false);

            if (target is null) {
                resolved = next; // real directory component
                continue;
            }

            // One symlink hop. Resolve a relative target against the link's (canonical) parent.
            var linkPath = target.FullName;

            if (!Path.IsPathRooted(linkPath)) {
                linkPath = Path.GetFullPath(Path.Combine(resolved, linkPath));
            }

            var linkRoot = Path.GetPathRoot(linkPath) ?? "";

            // Re-queue the target's own segments (so ancestor symlinks inside it get resolved too)
            // ahead of the segments we hadn't reached yet.
            segments = new Queue<string>(SplitSegments(linkPath[linkRoot.Length..]).Concat(segments));
            resolved = linkRoot;
        }

        return resolved;
    }

    static Queue<string> SplitSegments(string pathRemainder) => new(
        pathRemainder
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0)
    );
}
