using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class ExperimentalSettingsWatcherTests {
    // Starting an FSEvents stream has been measured at several seconds on macOS; only the refresh
    // after a write is held to the short bound.
    static readonly TimeSpan StartBound = TimeSpan.FromSeconds(30);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Watcher_triggers_refresh_on_store_write() {
        var root      = Config.Root;
        var refreshed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = ExperimentalSettingsWatcher.ForTest(
            new ExperimentalSettingsStore(root).Path, reason => refreshed.TrySetResult(reason), TimeProvider.System);
        await watcher.StartAsync(CancellationToken.None);
        await watcher.Watching.WaitAsync(StartBound);

        await new ExperimentalSettingsStore(root).SetAsync("default", ExperimentalFeatures.CodexAppServer.Id, true, CancellationToken.None);

        await Assert.That(await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2))).IsEqualTo("experimental settings changed");
        await watcher.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Watcher_refreshes_at_start_when_the_saved_switch_moved_before_it_watched() {
        var refreshed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = ExperimentalSettingsWatcher.ForTest(
            new ExperimentalSettingsStore(Config.Root).Path, reason => refreshed.TrySetResult(reason), TimeProvider.System,
            savedDiffersFromCommitted: () => true);

        await watcher.StartAsync(CancellationToken.None);
        await watcher.Watching.WaitAsync(StartBound);

        await Assert.That(await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2))).IsEqualTo("experimental settings changed");
        await watcher.StopAsync(CancellationToken.None);
    }
}
