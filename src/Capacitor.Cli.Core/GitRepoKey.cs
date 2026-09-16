using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

/// <summary>
/// The <c>owner/repo</c> key for a checkout, read straight from <c>.git/config</c>. No git
/// process runs, so this is safe on a launch path where spawning one per decision would not be —
/// which is the whole reason the daemon can afford to scope a launch at all.
/// <para>Null whenever the key cannot be established: no origin remote, an unrecognized remote
/// URL, or a directory outside any repository. Callers treat that as unplaceable rather than as
/// a match.</para>
/// </summary>
public static class GitRepoKey {
    /// <summary>The key for a repository root, which must already be the MAIN root — pass
    /// <see cref="GitRepository.ResolveMainRepoRoot"/>'s output, since a linked worktree's
    /// gitfile has no <c>.git/config</c> of its own.</summary>
    public static string? ForMainRepoRoot(string? mainRepoRoot) {
        if (string.IsNullOrWhiteSpace(mainRepoRoot)) return null;
        if (GitRemoteReader.ReadOriginUrl(mainRepoRoot) is not { } url) return null;
        if (RemoteMatcher.NormalizeRemoteUrl(url) is not { } normalized) return null;

        return RemoteMatcher.PathAfterHost(normalized);
    }

    /// <summary>The key for any directory inside a checkout, resolving the repository root and
    /// then the main root behind it. A borrowed reviewer and a snapshot cut from the same repo
    /// therefore produce one key.</summary>
    public static string? ForCheckout(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var root = GitRepository.FindRoot(path);

        return root is null ? null : ForMainRepoRoot(GitRepository.ResolveMainRepoRoot(root));
    }
}
