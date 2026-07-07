using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Outcome of <see cref="BorrowAuthorizer.AuthorizeBorrowAsync"/>.</summary>
public readonly record struct BorrowAuthResult(bool Allowed, string? CanonicalCwd, string? CanonicalGitRoot, string? Reason);

/// <summary>
/// Decides whether a given cwd may be <i>borrowed</i> — a read-only reviewer run in it on
/// this daemon. This repurposes the intent of <c>AgentOrchestrator.IsAllowedSyncSourceAsync</c>
/// (the mirror-sync guard) but is deliberately a new, standalone type: unlike that guard, borrow
/// authorization has no same-origin check, since cross-repo borrowing is expected.
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

        var canonicalCwd    = Canonicalize(path);
        var canonicalGitRoot = GitRepository.FindRoot(canonicalCwd);

        var allowed = canonicalGitRoot is not null
            ? config.IsRepoAllowed(canonicalCwd) || config.IsRepoAllowed(canonicalGitRoot)
            : config.AllowedRepoPaths.Length > 0 && config.IsRepoAllowed(canonicalCwd);

        return Task.FromResult(new BorrowAuthResult(allowed, canonicalCwd, canonicalGitRoot, allowed ? null : "not_allowed"));
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to its real, symlink-free, host-normalized form.
    /// Also called by <c>WorktreeInfo.Borrowed</c> (Task A5) so both sides of a borrow compare
    /// canonical paths.
    /// </summary>
    public static string Canonicalize(string path) {
        var fullPath = Path.GetFullPath(path);

        try {
            var finalTarget = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);

            return finalTarget is null ? fullPath : Path.GetFullPath(finalTarget.FullName);
        } catch {
            // Not a link (or resolution failed for some other I/O reason) — use the normalized path as-is.
            return fullPath;
        }
    }
}
