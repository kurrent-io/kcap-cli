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
        var closed = 0;
        var pane = new SubagentPaneViewModel(
            runs, runs.Rows.Single(), "Parent", AgentOrigin.Local, "a1", "claude", "key", _ => throw new InvalidOperationException(),
            new RecordingOpener(), time, new FakePermissionService(), () => closed++);
        await Assert.That(pane.IsRunning).IsTrue();

        // A reset replays the start before the id, as the feed reads them.
        runs.Clear();
        runs.Apply(Signals(new RunSignal.Started("c1", "Explore", "Map", time.GetUtcNow())));
        runs.Apply(Signals(new RunSignal.Finished("c1", null, RunOutcome.Done, time.GetUtcNow())));
        // The row is the same call, so its end shows before its id is replayed.
        await Assert.That(pane.IsRunning).IsFalse();
        runs.Apply(Signals(new RunSignal.Identified("c1", "s1")));

        await Assert.That(closed).IsEqualTo(0);
        await Assert.That(pane.Row).IsSameReferenceAs(runs.Rows.Single());
        await Assert.That(pane.IsRunning).IsFalse();
        await pane.TeardownAsync();
    });

    /// A running subagent reads as working to the chat, so its own foreground runs stay in the transcript
    /// rather than in the banner for work that outlives a turn.
    [Test]
    public async Task A_running_subagent_reads_as_working_and_an_ended_one_does_not() => await RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        var runs = new SessionRuns(time);
        runs.Apply(Signals(new RunSignal.Started("c1", "Explore", "Map", time.GetUtcNow())));
        var pane = new SubagentPaneViewModel(
            runs, runs.Rows.Single(), "Parent", AgentOrigin.Local, "a1", "claude", "key", _ => new ChatPromptFeed(),
            new RecordingOpener(), time, new FakePermissionService(), () => { });

        await Assert.That(pane.Chat.ActivityNote).StartsWith("Working for");

        runs.Apply(Signals(new RunSignal.Finished("c1", null, RunOutcome.Done, time.GetUtcNow())));

        await Assert.That(pane.IsRunning).IsFalse();
        await Assert.That(pane.Chat.ActivityNote).IsEqualTo("");
        await pane.TeardownAsync();
    });

    /// The chat reads the agent the pane opened on, so a row that comes to name another agent closes it.
    [Test]
    public async Task A_row_renamed_to_another_agent_closes_the_pane() => await RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        var runs = new SessionRuns(time);
        runs.Apply(Signals(new RunSignal.Started("c1", "Explore", "Map", time.GetUtcNow()), new RunSignal.Identified("c1", "s1")));
        var closed = 0;
        var pane = new SubagentPaneViewModel(
            runs, runs.Rows.Single(), "Parent", AgentOrigin.Local, "a1", "claude", "key", _ => throw new InvalidOperationException(),
            new RecordingOpener(), time, new FakePermissionService(), () => closed++);

        runs.Apply(Signals(new RunSignal.Identified("c1", "s2")));

        await Assert.That(closed).IsEqualTo(1);
        await pane.TeardownAsync();
    });
}
