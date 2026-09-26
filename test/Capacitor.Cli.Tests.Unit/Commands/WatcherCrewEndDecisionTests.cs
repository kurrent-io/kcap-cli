using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>When a watcher watches for Kiro Crew finishing with its session, and the reason it posts.</summary>
public class WatcherCrewEndDecisionTests {
    [Test]
    [Arguments("kiro", null, true, true)]
    [Arguments("kiro", null, false, false)]
    [Arguments("kiro", "agent-1", true, false)]
    [Arguments("claude", null, true, false)]
    public async Task Only_a_kiro_session_watcher_with_crew_present_watches_crew(string vendor, string? agentId, bool crewPresent, bool expected) {
        await Assert.That(WatchCommand.WatchesCrewEnd(vendor, agentId, crewPresent)).IsEqualTo(expected);
    }

    [Test]
    public async Task Crew_finishing_ends_the_session_as_crew_finished() {
        await Assert.That(WatchCommand.DecideEndReason(parentExited: false, crewFinished: true, wedgedCeiling: false, idle: false))
            .IsEqualTo("crew_finished");
    }

    /// <summary>The process exiting is the more specific fact when both are seen.</summary>
    [Test]
    public async Task A_parent_exit_outranks_crew_finishing() {
        await Assert.That(WatchCommand.DecideEndReason(parentExited: true, crewFinished: true, wedgedCeiling: false, idle: false))
            .IsEqualTo("parent_exited");
    }

    [Test]
    public async Task No_signal_posts_no_end() {
        await Assert.That(WatchCommand.DecideEndReason(false, false, false, false)).IsNull();
    }
}
