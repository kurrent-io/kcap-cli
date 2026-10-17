using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>The repository a hosted session runs in: the origin of its worktree's main checkout, and the
/// branch the worktree has out.</summary>
public readonly record struct HostedSessionRepository(string Owner, string Name, string RemoteUrl, string? Branch) {
    /// <summary>Null outside a repository, or for one with no parseable origin, which the server could not
    /// name anyway.</summary>
    public static HostedSessionRepository? Read(string? cwd) {
        if (string.IsNullOrEmpty(cwd) || GitRepository.FindRoot(cwd) is null) return null;

        if (GitRemoteReader.ReadOriginUrl(GitRepository.ResolveMainRepoRoot(cwd)) is not { Length: > 0 } url) return null;

        var (owner, name) = GitUrlParser.ParseRemoteUrl(url);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name)) return null;

        return new HostedSessionRepository(owner, name, url, GitRepository.CurrentBranch(cwd));
    }
}
