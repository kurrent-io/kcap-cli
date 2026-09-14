using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryScopeResolver(
        GitProviderRouter router, ConfigRoot config, WorkingDirectory workdir, TimeProvider time)
        : ISessionStartMemoryScopeResolver {
    public async Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct) {
        var started = time.GetTimestamp();
        TimeSpan Remaining() {
            var value = budget - time.GetElapsedTime(started);
            return value > TimeSpan.Zero ? value : TimeSpan.Zero;
        }

        string? repoHash = null;
        string? machine = null;
        try {
            var path = string.IsNullOrWhiteSpace(cwd) ? workdir.Path : cwd;
            var repo = await RepositoryDetection.DetectRepositoryAsync(router, config, path, Remaining(), detectPullRequest: false);
            if (repo?.Owner is not null && repo.RepoName is not null)
                repoHash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
        } catch { }
        ct.ThrowIfCancellationRequested();
        if (Remaining() <= TimeSpan.Zero) throw new OperationCanceledException(ct);
        try { machine = await MachineIdProvider.GetOrCreateAsync(config, ct); } catch (OperationCanceledException) { throw; } catch { }
        return new SessionStartMemoryScope(repoHash, machine);
    }
}
