using Capacitor.App.ViewModels;
using System.Reactive.Disposables;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class BackgroundCommandActivityTests {
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    static SessionRuns RunningCommand() {
        var runs = new SessionRuns(new FakeTimeProvider(T0));
        runs.Apply(new([], [], [
            new RunSignal.Started("c1", "make check", "", T0, RunKind.Shell, Provisional: true),
            new RunSignal.Detached("c1", "b1"),
        ]));
        return runs;
    }

    /// Reopening a session builds the incoming workspace before the outgoing one is torn down;
    /// that teardown must not erase the count the incoming one reported.
    [Test]
    public async Task An_outgoing_registration_leaves_the_incoming_count_standing() {
        using var activity = new BackgroundCommandActivity();
        IReadOnlyDictionary<string, int> latest = new Dictionary<string, int>();
        using var subscription = activity.Running.Subscribe(running => latest = running);

        var outgoing = activity.Track("local:a1", RunningCommand());
        using (activity.Track("local:a1", RunningCommand())) {
            outgoing.Dispose();
            await Assert.That(latest.GetValueOrDefault("local:a1")).IsEqualTo(1);
        }
        await Assert.That(latest.ContainsKey("local:a1")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_closed_workspace_keeps_tracking_commands_until_the_session_ends(bool alreadyRunning) {
        Action<int>? report = null;
        Action? ended = null;
        var stopped = false;
        using var activity = new BackgroundCommandActivity((_, changed, finished, _) => {
            report = changed;
            ended = finished;
            return Disposable.Create(() => stopped = true);
        });
        IReadOnlyDictionary<string, int> latest = new Dictionary<string, int>();
        using var subscription = activity.Running.Subscribe(running => latest = running);
        activity.Track("local:a1", alreadyRunning ? RunningCommand() : new SessionRuns(new FakeTimeProvider(T0))).Dispose();

        await Assert.That(latest.GetValueOrDefault("local:a1")).IsEqualTo(alreadyRunning ? 1 : 0);
        report!(2);
        await Assert.That(latest.GetValueOrDefault("local:a1")).IsEqualTo(2);
        report(0);
        await Assert.That(latest.ContainsKey("local:a1")).IsFalse();
        await Assert.That(stopped).IsFalse();
        report(1);
        await Assert.That(latest.GetValueOrDefault("local:a1")).IsEqualTo(1);
        ended!();
        await Assert.That(latest.ContainsKey("local:a1")).IsFalse();
        await Assert.That(stopped).IsTrue();
    }

    [Test]
    public async Task Reopening_stops_the_old_monitor_and_ignores_its_late_result() {
        Action<int>? report = null;
        var stopped = false;
        using var activity = new BackgroundCommandActivity((_, changed, _, _) => {
            report = changed;
            return Disposable.Create(() => stopped = true);
        });
        IReadOnlyDictionary<string, int> latest = new Dictionary<string, int>();
        using var subscription = activity.Running.Subscribe(running => latest = running);
        activity.Track("remote:a1", RunningCommand()).Dispose();
        using var reopened = activity.Track("remote:a1", RunningCommand());
        await Assert.That(stopped).IsTrue();
        report!(0);
        await Assert.That(latest.GetValueOrDefault("remote:a1")).IsEqualTo(1);
    }

    sealed class DisposalRecordingFeed : IChatTranscriptFeed {
        public bool Disposed { get; private set; }
        public FeedRead ReadAppended() => new(FeedStatus.Ok, []);
        public long? CurrentOffset => 0;
        public void Dispose() => Disposed = true;
    }

    /// A monitor that fails to start leaves no handed feed open and no stale count behind.
    [Test]
    public async Task A_monitor_that_fails_to_start_closes_the_handed_feed_and_drops_the_count() {
        var feed = new DisposalRecordingFeed();
        using var activity = new BackgroundCommandActivity((_, _, _, _) => throw new InvalidOperationException("boom"));
        IReadOnlyDictionary<string, int> latest = new Dictionary<string, int>();
        using var subscription = activity.Running.Subscribe(running => latest = running);
        var runs = RunningCommand();
        var registration = activity.Track("local:a1", runs, () => new TranscriptFeedHandoff(feed, "k", runs));

        await Assert.That(registration.Dispose).Throws<InvalidOperationException>();
        await Assert.That(feed.Disposed).IsTrue();
        await Assert.That(latest.ContainsKey("local:a1")).IsFalse();
    }

    [Test]
    public async Task Synchronous_completion_disposes_the_monitor() {
        var stopped = false;
        using var activity = new BackgroundCommandActivity((_, _, ended, _) => {
            ended();
            return Disposable.Create(() => stopped = true);
        });
        activity.Track("local:a1", RunningCommand()).Dispose();
        await Assert.That(stopped).IsTrue();
    }
}
