using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class ChatSessionInfoTests {
    [Test]
    public async Task FromLocal_carries_the_live_subagent_count() {
        var dto = WorkspaceFixtures.Agent("a1", "claude", true) with { LiveSubagents = 2 };
        await Assert.That(ChatSessionInfo.FromLocal(dto, ended: false).LiveSubagents).IsEqualTo(2);
        await Assert.That(ChatSessionInfo.FromLocal(dto with { LiveSubagents = null }, ended: false).LiveSubagents).IsNull();
    }

    [Test]
    public async Task FromRemote_carries_none() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = "Running", DaemonName = "d", OwnerUserId = "u", Vendor = "claude", RepoOwner = "o", RepoName = "r",
        });
        await Assert.That(ChatSessionInfo.FromRemote(row, ended: false).LiveSubagents).IsNull();
        await Assert.That(ChatSessionInfo.Gone.LiveSubagents).IsNull();
    }
}
