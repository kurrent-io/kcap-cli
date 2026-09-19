using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The one busy predicate the chat pane and the rail share: running, and either mid-turn or with
/// subagents the daemon still counts. Null counts as zero on both facts.
public class SessionStatusDotsTests {
    [Test]
    [Arguments("Running", false, null, true)]
    [Arguments("Running", false, 0, true)]
    [Arguments("Running", true, null, false)]
    [Arguments("Running", true, 0, false)]
    [Arguments("Running", true, 1, true)]
    [Arguments("Running", null, null, false)]
    [Arguments("Running", null, 2, true)]
    [Arguments("Starting", false, 2, false)]
    [Arguments("Completed", false, 2, false)]
    public async Task Is_working_across_the_verdict_and_the_count(string status, bool? awaitingInput, int? liveSubagents, bool expected) {
        await Assert.That(SessionStatusDots.IsWorking(status, awaitingInput, liveSubagents)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_remote_row_carries_no_count_and_never_works_by_it() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = "Running", DaemonName = "d", OwnerUserId = "u", Vendor = "claude", RepoOwner = "o", RepoName = "r",
        });

        await Assert.That(row.LiveSubagents).IsNull();
        await Assert.That(SessionStatusDots.IsWorking(row.Status, row.AwaitingInput, row.LiveSubagents)).IsFalse();
    }
}
