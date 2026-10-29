using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>When the watcher stops, the session end it would post is held while the daemon is resuming
/// its hosted agent: the resumed process continues the same session.</summary>
public class SessionEndDecisionTests {
    static HostedAgent Agent(string? id) => new(id, false, DaemonBridge.None);

    static Func<HostedAgent, bool> Ledger(ResumeState? state) =>
        hosted => state == ResumeState.Resuming;

    [Test]
    public async Task A_parent_exit_end_is_held_while_the_entry_is_resuming() {
        var decision = WatchCommand.DecideSessionEnd(runsOnElsewhere: false, Agent("agent-1"), Ledger(ResumeState.Resuming));

        await Assert.That(decision).IsEqualTo(WatchCommand.SessionEndAction.HeldForResume);
    }

    [Test]
    public async Task A_parent_exit_end_posts_for_a_running_entry_or_no_entry() {
        await Assert.That(WatchCommand.DecideSessionEnd(false, Agent("agent-1"), Ledger(ResumeState.Running)))
            .IsEqualTo(WatchCommand.SessionEndAction.Post);
        await Assert.That(WatchCommand.DecideSessionEnd(false, Agent("agent-1"), Ledger(null)))
            .IsEqualTo(WatchCommand.SessionEndAction.Post);
        await Assert.That(WatchCommand.DecideSessionEnd(false, hosted: null, Ledger(ResumeState.Resuming)))
            .IsEqualTo(WatchCommand.SessionEndAction.Post);
    }

    [Test]
    public async Task A_session_that_runs_on_elsewhere_is_handed_to_its_new_process_first() {
        var decision = WatchCommand.DecideSessionEnd(runsOnElsewhere: true, Agent("agent-1"), Ledger(ResumeState.Resuming));

        await Assert.That(decision).IsEqualTo(WatchCommand.SessionEndAction.RunsOnElsewhere);
    }
}
