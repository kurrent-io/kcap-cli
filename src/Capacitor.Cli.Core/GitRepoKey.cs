using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

/// <summary>
/// The <c>owner/repo</c> key for a checkout, read straight from its git config. No git process
/// runs, so this is safe on a launch path where spawning one per decision would not be — which is
/// the whole reason a launch can afford to be scoped at all.
/// <para>Null whenever the key cannot be established: no origin remote, an unrecognized remote
/// URL, or a directory outside any repository. Callers treat that as unplaceable rather than as a
/// match, so a key that fails to resolve where it should is not a quiet no-op — it silently drops
/// a repository out of every deny list.</para>
/// </summary>
public static class GitRepoKey {
    /// <summary>The key for any directory inside a checkout. A primary checkout, a linked worktree
    /// cut from it and a submodule each resolve to the repository that actually names them, so a
    /// list entry matches wherever the work is happening.</summary>
    public static string? ForCheckout(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (GitRepository.FindRoot(path) is not { } root) return null;
        if (ConfigPathFor(root) is not { } configPath) return null;
        if (GitRemoteReader.ReadOriginUrlFromConfig(configPath) is not { } url) return null;
        if (RemoteMatcher.NormalizeRemoteUrl(url) is not { } normalized) return null;

        // A remote written with a trailing slash normalizes with one, and `acme/widgets/` matches
        // no list entry anyone would write — leaving the repo out of its own deny list.
        return RemoteMatcher.PathAfterHost(normalized)?.Trim('/') is { Length: > 0 } key ? key : null;
    }

    /// <summary>
    /// The git config that names <paramref name="checkoutRoot"/>'s remotes.
    /// <para>Three layouts, and only the first has it where the name suggests. A primary checkout
    /// keeps <c>.git</c> as a directory. A linked worktree and a submodule both keep it as a FILE
    /// naming their real git dir: a worktree's is <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>,
    /// which holds no config of its own because it shares the repository's, while a submodule's is
    /// <c>&lt;super&gt;/.git/modules/&lt;name&gt;</c>, which holds the one that names IT rather than
    /// the superproject.</para>
    /// </summary>
    static string? ConfigPathFor(string checkoutRoot) {
        var dotGit = Path.Combine(checkoutRoot, ".git");

        try {
            if (Directory.Exists(dotGit)) return Path.Combine(dotGit, "config");
            if (!File.Exists(dotGit)) return null;

            var line   = File.ReadLinesShared(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            var target = line?["gitdir:".Length..].Trim();

            if (string.IsNullOrEmpty(target)) return null;
            if (!Path.IsPathRooted(target)) target = Path.Combine(checkoutRoot, target);

            target = Path.GetFullPath(target);

            var marker = target.Replace('\\', '/').LastIndexOf("/worktrees/", StringComparison.Ordinal);
            if (marker > 0) target = target[..marker];

            return Path.Combine(target, "config");
        } catch {
            // Same heuristic stance as GitRepository.FindRoot: an unreadable checkout is unplaceable.
            return null;
        }
    }
}
