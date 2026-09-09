using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The watcher reads the daemon's restart-pending marker file, the same source `kcap daemon
/// status` reads: nothing about a queued restart travels over the status socket.
public class DaemonRestartPendingWatcherTests {
    const string Name = "daemon-a";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static DaemonRestartMarker Marker() =>
        new("1.2.3", "self-detected", new DateTimeOffset(2026, 9, 9, 11, 5, 58, TimeSpan.Zero));

    sealed class Harness : IDisposable {
        public readonly Subject<AttachStatus> Status = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        public readonly TimerCountingTimeProvider Time;
        public readonly DaemonRestartPendingWatcher Watcher;
        public readonly List<bool> Seen = [];
        readonly CancellationTokenSource _lifetime = new();
        IDisposable? _subscription;

        public Harness(DaemonStore store) {
            Time    = new TimerCountingTimeProvider(Clock);
            Watcher = new DaemonRestartPendingWatcher(store, Name, Status, Time, _lifetime.Token);
        }

        public void Start() {
            Watcher.Start();
            _subscription = Watcher.Pending.Subscribe(Seen.Add);
        }

        public void Dispose() {
            _subscription?.Dispose();
            _lifetime.Cancel();
            Watcher.Dispose();
            _lifetime.Dispose();
        }
    }

    [Test]
    public async Task Reports_a_marker_already_present_when_started() {
        DaemonRestartMarker.Write(Daemons.Store, Name, Marker());
        using var h = new Harness(Daemons.Store);

        h.Start();

        await Assert.That(h.Seen).IsEquivalentTo([true]);
    }

    [Test]
    public async Task Sees_a_marker_written_later_on_the_next_poll() {
        using var h = new Harness(Daemons.Store);
        h.Start();
        await Assert.That(h.Seen).IsEquivalentTo([false]);

        DaemonRestartMarker.Write(Daemons.Store, Name, Marker());
        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the poll to be armed");
        h.Clock.Advance(DaemonRestartPendingWatcher.PollInterval);

        await WaitUntilAsync(() => h.Seen[^1], what: "the marker to be seen on the poll");
    }

    [Test]
    public async Task Clears_on_an_attach_transition_once_the_marker_is_gone() {
        DaemonRestartMarker.Write(Daemons.Store, Name, Marker());
        using var h = new Harness(Daemons.Store);
        h.Start();

        DaemonRestartMarker.Delete(Daemons.Store, Name);
        h.Status.OnNext(new AttachStatus(AttachState.Connected, null, []));

        await Assert.That(h.Seen).IsEquivalentTo([true, false]);
    }

    [Test]
    public async Task Rereads_that_find_the_same_state_emit_nothing() {
        using var h = new Harness(Daemons.Store);
        h.Start();

        h.Status.OnNext(new AttachStatus(AttachState.Connecting, null, null));
        h.Status.OnNext(new AttachStatus(AttachState.Connected, null, []));

        await Assert.That(h.Seen).IsEquivalentTo([false]);
    }
}
