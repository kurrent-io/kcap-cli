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

    static AgentInstanceDto Remote(string? title, string? prompt = "fix the flaky test\nmore") => new() {
        AgentId = "r1", SessionId = "s2", Status = "Running", DaemonName = "d",
        OwnerUserId = "u7", RegisteredAt = DateTime.UtcNow, Title = title, Prompt = prompt,
    };

    [Test]
    public async Task FromRemote_prefers_the_server_title() {
        var row = AgentRow.FromRemote(Remote("Renamed on the server"));
        await Assert.That(row.Title).IsEqualTo("Renamed on the server");
    }

    [Test]
    public async Task FromRemote_falls_back_to_the_prompt_cut() {
        await Assert.That(AgentRow.FromRemote(Remote(null)).Title).IsEqualTo("fix the flaky test");
        await Assert.That(AgentRow.FromRemote(Remote("")).Title).IsEqualTo("fix the flaky test");
    }

    [Test]
    public async Task FromRemote_carries_the_owner() {
        await Assert.That(AgentRow.FromRemote(Remote(null)).OwnerUserId).IsEqualTo("u7");
    }
}
