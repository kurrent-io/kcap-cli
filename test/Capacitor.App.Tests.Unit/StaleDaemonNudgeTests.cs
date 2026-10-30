using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.Update;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class StaleDaemonNudgeTests {
    static readonly AttachStatus Connected = new(AttachState.Connected, null, []);

    sealed class Harness : IDisposable {
        public readonly FakeTimeProvider Clock = new();
        public readonly BehaviorSubject<AttachStatus> Status = new(Connected);
        public readonly BehaviorSubject<string?> Version = new("1.4.0+abc");
        public readonly BehaviorSubject<bool> Pending = new(false);
        public readonly List<string> Outcomes = [];
        public int Requests;
        public readonly StaleDaemonNudge Nudge;

        public Harness() => Nudge = new StaleDaemonNudge("1.4.1", Status, Version, Pending,
            _ => { Requests++; return Task.FromResult("queued"); }, Outcomes.Add, Clock, CancellationToken.None);

        public void Dispose() => Nudge.Dispose();
    }

    [Test]
    public async Task An_older_daemon_with_no_restart_queued_is_asked_once_after_the_grace() {
        using var h = new Harness();

        h.Clock.Advance(StaleDaemonNudge.Grace - TimeSpan.FromSeconds(1));
        await Assert.That(h.Requests).IsEqualTo(0);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(h.Requests).IsEqualTo(1);
        await Assert.That(h.Outcomes).IsEquivalentTo(["queued"]);

        h.Status.OnNext(new AttachStatus(AttachState.Unreachable, ExpectedDaemonRestart.Reason, null));
        h.Status.OnNext(Connected);
        h.Clock.Advance(StaleDaemonNudge.Grace * 2);
        await Assert.That(h.Requests).IsEqualTo(1);
    }

    /// The daemon's own poll queuing the restart, or the daemon catching up, ends the wait without a request.
    [Test]
    public async Task A_daemon_that_queues_its_own_restart_or_catches_up_is_left_alone() {
        using var queued = new Harness();
        queued.Clock.Advance(TimeSpan.FromSeconds(10));
        queued.Pending.OnNext(true);
        queued.Clock.Advance(StaleDaemonNudge.Grace);
        await Assert.That(queued.Requests).IsEqualTo(0);

        using var current = new Harness();
        current.Version.OnNext("1.4.1+def");
        current.Clock.Advance(StaleDaemonNudge.Grace);
        await Assert.That(current.Requests).IsEqualTo(0);
    }

    [Test]
    [Arguments("1.4.0", true)]
    [Arguments("1.4.1+abc", false)]
    [Arguments("1.5.0", false)]
    [Arguments(null, false)]
    public async Task Only_an_older_daemon_is_behind(string? daemon, bool behind) {
        await Assert.That(StaleDaemonNudge.Behind(daemon, "1.4.1")).IsEqualTo(behind);
    }
}
