using System.Reactive.Linq;
using Avalonia;
using Avalonia.Threading;

namespace Capacitor.App.Tests.Unit;

public class AvaloniaSessionTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispatch_runs_on_the_headless_session() {
        var answer = await AvaloniaSession.DispatchAsync(() => 42);
        await Assert.That(answer).IsEqualTo(42);
    }

    /// <summary>Per-test isolation rebuilds the application on every dispatch, and each rebuild
    /// releases Dispatcher.UIThread before reclaiming it — a window any concurrent thread reading
    /// that property takes for itself, after which building the application verifies access against
    /// a thread it does not own. One application across the assembly leaves nothing to reclaim.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_application_and_its_dispatcher_outlive_a_single_dispatch() {
        var first  = await AvaloniaSession.DispatchAsync(() => ((object?)Application.Current, (object)Dispatcher.UIThread));
        var second = await AvaloniaSession.DispatchAsync(() => ((object?)Application.Current, (object)Dispatcher.UIThread));

        await Assert.That(ReferenceEquals(first.Item1, second.Item1)).IsTrue();
        await Assert.That(ReferenceEquals(first.Item2, second.Item2)).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Scheduler_swap_observes_on_the_immediate_scheduler_and_restores() {
        // Force the (lazy, process-wide) session to start BEFORE snapshotting "prior" — session
        // start is what pins RxSchedulers.MainThreadScheduler to the real AvaloniaScheduler (see
        // AvaloniaSession's own comment). If this test runs FIRST in the NotInParallel group,
        // snapshotting "prior" before that pin would capture System.Reactive's unconfigured
        // default instead, and the restore assertion below would then fail.
        await AvaloniaSession.DispatchAsync(() => 0);
        var prior = ReactiveUI.Reactive.RxSchedulers.MainThreadScheduler;
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string? seen = null;
            using var _ = System.Reactive.Linq.Observable.Return("published-on-background")
                .ObserveOn(ReactiveUI.Reactive.RxSchedulers.MainThreadScheduler)
                .Subscribe(v => seen = v);
            await Task.Yield();
            await Assert.That(seen).IsEqualTo("published-on-background"); // immediate scheduler delivered synchronously
        });
        await Assert.That(ReferenceEquals(ReactiveUI.Reactive.RxSchedulers.MainThreadScheduler, prior)).IsTrue(); // restored
    }
}
