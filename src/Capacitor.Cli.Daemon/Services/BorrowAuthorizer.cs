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
            // realpath resolution failed (permission on an ancestor, a transient FS error, a symlink
            // chain too long to follow). Fail CLOSED: this boundary never authorizes an unresolved path.
            return Task.FromResult(new BorrowAuthResult(false, null, null, "not_allowed"));
        }

        var canonicalGitRoot = GitRepository.FindRoot(canonicalCwd);

        var allowed = canonicalGitRoot is not null
            ? config.IsRepoAllowed(canonicalCwd) || config.IsRepoAllowed(canonicalGitRoot)
            : config.AllowedRepoPaths.Length > 0 && config.IsRepoAllowed(canonicalCwd);

        return Task.FromResult(new BorrowAuthResult(allowed, canonicalCwd, canonicalGitRoot, allowed ? null : "not_allowed"));
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to its real, symlink-free, host-normalized form — a true
    /// <c>realpath</c> that resolves symlinks in EVERY path component, not just the leaf. This is a
    /// security boundary: an <i>ancestor</i> symlink must not let a directory that physically lives
    /// outside the operator's allowlisted tree textually match <see cref="DaemonConfig.IsRepoAllowed"/>
    /// (e.g. an allowlisted <c>/repos/*</c> containing a symlink <c>proj/linkdir</c> → <c>~/.ssh</c>).
    /// A path the walk could not finish throws rather than answering with its unresolved remainder,
    /// which the allowlist would match as though it were a location.
    /// </summary>
    public static string Canonicalize(string path) =>
        CanonicalPath.TryResolve(path, out var resolved)
            ? resolved
            : throw new IOException($"{path} could not be fully resolved.");
}
