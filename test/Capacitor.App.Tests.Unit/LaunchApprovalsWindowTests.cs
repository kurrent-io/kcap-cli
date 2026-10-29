using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LaunchApprovalsWindowTests {
    [Test]
    public Task Polls_only_while_its_own_window_is_visible_and_restored() => AvaloniaSession.RunOnUiAsync(async () => {
        var reader = new ScriptedReader();
        var ticker = new FakeTicker();
        var key = 0;
        using var activity = new ActivityViewModel(reader.Read, () => key.ToString(System.Globalization.CultureInfo.InvariantCulture), ticker);
        var window = new LaunchApprovalsWindow { DataContext = activity };
        try {
            await Assert.That(reader.ReadCalls).IsEqualTo(0);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            await Assert.That(reader.ReadCalls).IsEqualTo(1);
            window.WindowState = WindowState.Minimized;
            key++;
            ticker.Tick();
            ticker.Tick();
            await Assert.That(reader.ReadCalls).IsEqualTo(1);
            window.WindowState = WindowState.Normal;
            await activity.PendingRefreshForTesting!;
            await Assert.That(reader.ReadCalls).IsEqualTo(2);
            window.Hide();
            key++;
            ticker.Tick();
            ticker.Tick();
            await Assert.That(reader.ReadCalls).IsEqualTo(2);
            window.Show();
            await activity.PendingRefreshForTesting!;
            await Assert.That(reader.ReadCalls).IsEqualTo(3);
        } finally { window.Close(); }
        key++;
        ticker.Tick();
        ticker.Tick();
        await Assert.That(reader.ReadCalls).IsEqualTo(3);
    });

    [Test]
    public Task Coordinator_reuses_restores_and_recreates_the_window() => AvaloniaSession.RunOnUiAsync(async () => {
        using var activity = new ActivityViewModel(() => new ConsentLogReadResult([], true), () => "key", new FakeTicker());
        using var coordinator = new LaunchApprovalsCoordinator(activity);
        coordinator.Show();
        var first = coordinator.Window!;
        coordinator.Show();
        await Assert.That(coordinator.Window!).IsSameReferenceAs(first);
        first.WindowState = WindowState.Minimized;
        coordinator.Show();
        await Assert.That(first.WindowState).IsEqualTo(WindowState.Normal);
        first.Close();
        coordinator.Show();
        var reopened = coordinator.Window!;
        await Assert.That(reopened).IsNotSameReferenceAs(first);
        reopened.Close();
        coordinator.Dispose();
        coordinator.Show();
        await Assert.That(coordinator.Window).IsNull();
        if (activity.PendingRefreshForTesting is { } refresh) await refresh;
    });
}
