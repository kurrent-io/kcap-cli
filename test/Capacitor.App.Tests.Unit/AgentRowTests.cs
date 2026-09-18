using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class AgentRowTests {
    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    [Test]
    public async Task FromLocal_carries_the_dtos_session_id() {
        var dto = WorkspaceFixtures.Agent("a1", "claude", true, sessionId: "s1");
        var row = AgentRow.FromLocal(dto, Repo);
        await Assert.That(row.SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task FromRemote_carries_the_dtos_session_id() {
        var dto = new AgentInstanceDto {
            AgentId = "r1", SessionId = "s2", Status = "Running", DaemonName = "d",
            OwnerUserId = "u", RegisteredAt = DateTime.UtcNow,
        };
        var row = AgentRow.FromRemote(dto);
        await Assert.That(row.SessionId).IsEqualTo("s2");
    }
}
