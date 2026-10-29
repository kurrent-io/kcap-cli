using Avalonia.Threading;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// A row subscribes the shared ticker inside DynamicData's Transform, which runs on whatever
/// thread mutates the SourceCache (the daemon socket pump in production). Subscribing from there
/// must still bind the DispatcherTimer to the UI dispatcher, or the Interval never ticks and
/// Uptime freezes at its seed. This drives UiTicker's real 1s Interval from a background thread;
/// the row-level uptime tests inject a Subject and are blind to the hazard.
public class UiTickerTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Ticker_subscribed_off_the_ui_thread_still_ticks_on_the_dispatcher() {
        var (ticks, everyTickOnUiThread) = await AvaloniaSession.DispatchAsync(async () => {
            var ticker = new UiTicker(); // construct ON the UI thread, per the production contract

            var count = 0;
            var onUiThread = true;
            IDisposable? subscription = null;
            await Task.Run(() => subscription = ticker.Ticks.Subscribe(_ => {
                onUiThread &= Dispatcher.UIThread.CheckAccess();
                Interlocked.Increment(ref count);
            }));

            // Baseline excludes anything the ticker delivers SYNCHRONOUSLY on subscribe (a
            // StartWith-style seed), so the wait below can only be satisfied by a genuine periodic
            // tick.
            var seedOnSubscribe = Volatile.Read(ref count);

            // DispatchAsync's async overload pumps a DispatcherFrame around this body, which is
            // what lets the dispatcher's own timers run while we wait.
            await WorkspaceFixtures.WaitUntilAsync(() => Volatile.Read(ref count) > seedOnSubscribe, what: "a periodic ticker tick");
            subscription!.Dispose();

            return (Volatile.Read(ref count) - seedOnSubscribe, onUiThread);
        });

        await Assert.That(ticks).IsGreaterThan(0);
        await Assert.That(everyTickOnUiThread).IsTrue();
    }
}
