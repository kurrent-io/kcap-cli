using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryScopeResolver : ISessionStartMemoryScopeResolver {
    public async Task<SessionStartMemoryScope> ResolveAsync(string? cwd, CancellationToken ct) {
        string? repoHash = null;
        string? machine = null;
        try {
            var path = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
            var repo = await RepositoryDetection.DetectRepositoryAsync(path, detectPullRequest: false);
            if (repo?.Owner is not null && repo.RepoName is not null)
                repoHash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
        } catch { }
        ct.ThrowIfCancellationRequested();
        try { machine = await MachineIdProvider.GetOrCreateAsync(); } catch { }
        return new SessionStartMemoryScope(repoHash, machine);
    }
}
