using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Which watcher posts the session-end a Muse SessionEnd hook hands over.</summary>
public class WatcherMuseEndHandoffTests {
    [TempDir] public required TempDir Tmp { get; init; }

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

    /// <summary>A handed end a resume holds is not posted, and its claim goes so a later end is not blocked;
    /// without a resume the claim stays for the post.</summary>
    [Test]
    [Arguments("agent-1", false)]
    [Arguments(null, true)]
    public async Task A_handed_end_held_for_resume_releases_its_claim(string? resumingId, bool claimRemains) {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        MuseSessionEndRequest.Write(paths, "session-1", """{"reason":"exit"}""");
        var handed = MuseSessionEndRequest.TryClaim(paths, "session-1", "watcher")!;

        var held = WatchCommand.HoldHandedEndForResume(handed, resumingId);

        await Assert.That(held).IsEqualTo(resumingId);
        await Assert.That(File.Exists(handed.ClaimedPath)).IsEqualTo(claimRemains);
    }
}
