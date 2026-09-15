using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// A launch the daemon is still starting, or one the app has only had accepted, renders as a row
/// of its own until a real agent row for the same id arrives.
public class PendingLaunchRowsTests {
    static (FakeDaemonClientService Local, AgentDirectory Dir) Build() {
        var local = new FakeDaemonClientService();
        var dir = new AgentDirectory(
            local, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null), p => p,
            "m1", "http://localhost:9999");
        return (local, dir);
    }

    static AgentStatusDto LocalAgent(string id) => new(
        Id: id, Kind: "agent", Vendor: "claude", RepoPath: "/r", Status: "Starting",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null);

    static PendingLaunchDto Pending(string id, string? stage = "spawned") =>
        new(id, "claude", "/r", "Fix the flaky test", DateTime.UtcNow, stage);

    [Test]
    public async Task A_daemon_pending_launch_is_a_starting_row_until_the_agent_is_published() {
        var (local, dir) = Build();
        using var _d = dir;

        local.Pending.AddOrUpdate(Pending("p1"));

        var row = dir.Rows.Lookup("pending:p1").Value;
        await Assert.That(row.Origin).IsEqualTo(AgentOrigin.Pending);
        await Assert.That(row.Id).IsEqualTo("p1");
        await Assert.That(row.Status).IsEqualTo("Starting");
        await Assert.That(row.Vendor).IsEqualTo("claude");
        await Assert.That(row.Title).IsEqualTo("Fix the flaky test");
        await Assert.That(row.LaunchStage).IsEqualTo("spawned");
        await Assert.That(row.RepoGroupKey).IsEqualTo(AgentRow.FromLocal(LocalAgent("x"), new RepoIdentity("path:/r", "r")).RepoGroupKey);

        local.Agents.AddOrUpdate(LocalAgent("p1"));

        await Assert.That(dir.Rows.Lookup("pending:p1").HasValue).IsFalse();
        await Assert.That(dir.Rows.Lookup("local:p1").HasValue).IsTrue();
    }

    [Test]
    public async Task A_placeholder_yields_to_the_daemons_own_pending_entry_and_then_to_the_agent() {
        var (local, dir) = Build();
        using var _d = dir;

        dir.AddPlaceholder("p2", "codex", "/r", "Fix the flaky test", "gpt-5-codex");
        var placeholder = dir.Rows.Lookup("pending:p2").Value;
        await Assert.That(placeholder.Origin).IsEqualTo(AgentOrigin.Pending);
        await Assert.That(placeholder.LaunchStage).IsNull();
        await Assert.That(placeholder.Model).IsEqualTo("gpt-5-codex");
        await Assert.That(placeholder.Vendor).IsEqualTo("codex");

        local.Pending.AddOrUpdate(Pending("p2", stage: "initialized"));
        await Assert.That(dir.Rows.Lookup("pending:p2").Value.LaunchStage).IsEqualTo("initialized");
        await Assert.That(dir.Rows.Count).IsEqualTo(1);

        local.Pending.Remove("p2");
        local.Agents.AddOrUpdate(LocalAgent("p2"));
        await Assert.That(dir.Rows.Lookup("pending:p2").HasValue).IsFalse();
        await Assert.That(dir.Rows.Lookup("local:p2").HasValue).IsTrue();
    }

    [Test]
    public async Task Removing_a_placeholder_drops_its_row() {
        var (_, dir) = Build();
        using var _d = dir;
        dir.AddPlaceholder("p3", "claude", "/r", null, null);
        await Assert.That(dir.Rows.Lookup("pending:p3").HasValue).IsTrue();

        dir.RemovePlaceholder("p3");

        await Assert.That(dir.Rows.Lookup("pending:p3").HasValue).IsFalse();
    }

    [Test]
    public async Task A_pending_row_never_claims_a_session() {
        var (local, dir) = Build();
        using var _d = dir;
        local.Pending.AddOrUpdate(Pending("p4"));
        await Assert.That(dir.VendorOfSession("s1")).IsNull();
    }
}
