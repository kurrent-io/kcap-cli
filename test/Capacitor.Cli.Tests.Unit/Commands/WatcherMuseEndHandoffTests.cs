using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Which watcher posts the session-end a Muse SessionEnd hook hands over.</summary>
public class WatcherMuseEndHandoffTests {
    [Test]
    [Arguments("muse", null, true)]
    [Arguments("muse", "agent-1", false)]
    [Arguments("claude", null, false)]
    public async Task Only_a_muse_session_watcher_takes_a_handed_end(string vendor, string? agentId, bool expected) =>
        await Assert.That(WatchCommand.TakesHandedMuseEnd(vendor, agentId)).IsEqualTo(expected);

    /// <summary>A live process claimed for the session means its exit is not the session's; without one, or
    /// for any other vendor, the watcher's exit ends the session as before.</summary>
    [Test]
    [Arguments("muse", null, 4242, true)]
    [Arguments("muse", null, null, false)]
    [Arguments("muse", "agent-1", 4242, false)]
    [Arguments("claude", null, 4242, false)]
    public async Task A_muse_session_claimed_by_a_live_process_runs_on(string vendor, string? agentId, int? claimant, bool expected) =>
        await Assert.That(WatchCommand.SessionRunsOnElsewhere(vendor, agentId, claimant)).IsEqualTo(expected);
}
