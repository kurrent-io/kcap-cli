using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Polls <c>repos.json</c> and re-sends the daemon's repo paths whenever the file no longer
/// matches the copy the server was last given. The CLI and the desktop app write that file from
/// other processes, and the server would otherwise learn of the change only at the next
/// registration.
/// </summary>
internal sealed partial class RepoStoreWatcher : BackgroundService {
    readonly ILogger _logger;

    // Seams (assigned from DI in the production ctor; overridden directly in tests).
    internal Func<RepoStoreFingerprint?> Stat;
    internal Func<RepoStoreFingerprint?> Advertised;
    internal Func<bool>                  IsReady;
    internal Func<Task>                  Publish;

    // One stat of one file per tick, so the interval can be short enough that a repo added from a
    // terminal is in the launch dialog by the time the user has switched to the browser.
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    readonly TimeProvider _time;

    public RepoStoreWatcher(
            DaemonConfig config, ServerConnection server, ILogger<RepoStoreWatcher> logger, TimeProvider time) {
        _logger    = logger;
        _time      = time;
        Stat       = () => new RepoPathStore(config.ConfigRoot, time).Fingerprint();
        Advertised = () => server.AdvertisedRepoStore;
        IsReady    = () => server.IsReady;
        Publish    = server.UpdateRepoPathsAsync;
    }

    RepoStoreWatcher(Func<RepoStoreFingerprint?> stat, Func<RepoStoreFingerprint?> advertised, Func<bool> isReady,
            Func<Task> publish, TimeProvider time) {
        _logger    = NullLogger.Instance;
        _time      = time;
        Stat       = stat;
        Advertised = advertised;
        IsReady    = isReady;
        Publish    = publish;
    }

    internal static RepoStoreWatcher ForTest(
            Func<RepoStoreFingerprint?> stat, Func<RepoStoreFingerprint?> advertised, Func<bool> isReady,
            Func<Task> publish, TimeProvider time) =>
        new(stat, advertised, isReady, publish, time);

    /// <summary>One poll iteration (timer-driven; also the unit-test entry point). Registration
    /// sends the file as it is then, so nothing is sent before it; a failed send leaves the
    /// advertised fingerprint where it was, so the next tick retries.</summary>
    internal async Task TickAsync() {
        if (!IsReady()) return;
        if (Stat() == Advertised()) return;

        LogChanged(_logger);
        try {
            await Publish().ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogPublishFailed(_logger, ex);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        using var timer = new PeriodicTimer(PollInterval, _time);
        try {
            while (await timer.WaitForNextTickAsync(ct)) await TickAsync();
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "repos.json changed on disk; re-sending repo paths to the server")]
    static partial void LogChanged(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Re-sending repo paths failed; the next tick retries")]
    static partial void LogPublishFailed(ILogger logger, Exception ex);
}
