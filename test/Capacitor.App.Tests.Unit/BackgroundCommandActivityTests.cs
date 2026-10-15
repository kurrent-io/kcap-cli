using Capacitor.App.ViewModels;
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
        var activity = new BackgroundCommandActivity();
        IReadOnlyDictionary<string, int> latest = new Dictionary<string, int>();
        using var subscription = activity.Running.Subscribe(running => latest = running);

        var outgoing = activity.Track("local:a1", RunningCommand());
        using (activity.Track("local:a1", RunningCommand())) {
            outgoing.Dispose();
            await Assert.That(latest.GetValueOrDefault("local:a1")).IsEqualTo(1);
        }
        await Assert.That(latest.ContainsKey("local:a1")).IsFalse();
    }
}
