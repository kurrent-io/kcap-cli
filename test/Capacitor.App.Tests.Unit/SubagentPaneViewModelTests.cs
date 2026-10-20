using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The pane follows its subagent's row through a reset of the parent's feed, which rebuilds the rows.
[NotInParallel("AvaloniaSession")]
public class SubagentPaneViewModelTests {
    static ChatProjectionResult Signals(params RunSignal[] signals) => new([], [], signals);

    [Test]
    public async Task The_pane_follows_its_row_by_call_after_the_parent_rebuilds_its_rows() => await RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        var runs = new SessionRuns(time);
        runs.Apply(Signals(new RunSignal.Started("c1", "Explore", "Map", time.GetUtcNow()), new RunSignal.Identified("c1", "s1")));
        var pane = new SubagentPaneViewModel(
            runs, runs.Rows.Single(), "Parent", AgentOrigin.Local, "a1", "claude", "key", _ => throw new InvalidOperationException(),
            new RecordingOpener(), time, new FakePermissionService(), () => { });
        await Assert.That(pane.IsRunning).IsTrue();

        runs.Clear();
        runs.Apply(Signals(new RunSignal.Started("c1", "Explore", "Map", time.GetUtcNow()), new RunSignal.Identified("c1", "s1"),
            new RunSignal.Finished("c1", null, RunOutcome.Done, time.GetUtcNow())));

        await Assert.That(pane.Row).IsSameReferenceAs(runs.Rows.Single());
        await Assert.That(pane.IsRunning).IsFalse();
        await pane.TeardownAsync();
    });
}
