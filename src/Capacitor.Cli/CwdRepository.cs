using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli;

/// <summary>
/// The repository a long-lived server's working directory belongs to, resolved on first use and
/// held for the life of the process. Pull-request detection is skipped: callers need owner and
/// name only, and the provider round-trip would otherwise run in every agent session that spawns
/// the server, tool call or not. Concurrent first callers share one resolution.
/// </summary>
sealed class CwdRepository(
        ConfigRoot config, string cwd, GitProviderRouter router, TimeProvider time, CommandRunner? run = null) {
    readonly Lock             _lock = new();
    Task<RepositoryPayload?>? _detection;

    /// <summary>Owner, name, host and branch of the checkout, or null outside one. A failed resolution is retried.</summary>
    public async ValueTask<RepositoryPayload?> GetAsync() {
        Task<RepositoryPayload?> detection;
        lock (_lock) {
            if (_detection is null || _detection.IsFaulted || _detection.IsCanceled)
                _detection = RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time, detectPullRequest: false, run: run);
            detection = _detection;
        }

        return await detection;
    }

    /// <summary>The server-side repo hash of the checkout, or null when its origin names no owner.</summary>
    public async ValueTask<string?> GetHashAsync() {
        var repo = await GetAsync();

        return repo?.Owner is null || repo.RepoName is null
            ? null
            : RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
    }
}
