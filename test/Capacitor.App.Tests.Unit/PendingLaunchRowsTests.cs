using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// A launch the daemon is still starting, or one the app has only had accepted, renders as a row
/// of its own until a real agent row for the same id arrives.
public class PendingLaunchRowsTests {
    static (FakeDaemonClientService Local, AgentDirectory Dir) Build(TimeProvider? time = null) {
        var (local, _, dir) = BuildWithRemote(time);
        return (local, dir);
    }

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, AgentDirectory Dir) BuildWithRemote(TimeProvider? time = null) {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var dir = new AgentDirectory(
            local, remote, new FakeServerLane(), new RepoIdentityResolver(_ => null), p => p,
            "m1", "http://localhost:9999", time);
        return (local, remote, dir);
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

    /// Nothing else may ever change in an idle directory, so the expiry has to be the directory's
    /// own timer rather than a check folded into the next recompute.
    [Test]
    public async Task A_placeholder_expires_on_its_own_after_ten_minutes() {
        var time = new FakeTimeProvider();
        var (_, dir) = Build(time);
        using var _d = dir;
        dir.AddPlaceholder("p5", "claude", "/r", null, null);
        time.Advance(TimeSpan.FromMinutes(9));
        await Assert.That(dir.Rows.Lookup("pending:p5").HasValue).IsTrue();

        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        await Assert.That(dir.Rows.Lookup("pending:p5").HasValue).IsFalse();
    }

    /// The server accepts a launch as a dashed Guid and the daemon publishes the "N" form; the
    /// stand-ins match on the normalized id, or the placeholder would outlive the real row.
    [Test]
    public async Task A_placeholder_matches_the_daemons_rows_across_guid_spellings() {
        var (local, dir) = Build();
        using var _d = dir;
        const string dashed = "0123abcd-4567-89ef-0123-456789abcdef";
        var n = Guid.Parse(dashed).ToString("N");

        dir.AddPlaceholder(dashed, "claude", "/r", "Fix it", null);
        await Assert.That(dir.Rows.Lookup($"pending:{n}").HasValue).IsTrue();

        local.Pending.AddOrUpdate(new PendingLaunchDto(n, "claude", "/r", "Fix it", DateTime.UtcNow, "spawned"));
        await Assert.That(dir.Rows.Count).IsEqualTo(1);
        await Assert.That(dir.Rows.Items.Single().LaunchStage).IsEqualTo("spawned");

        local.Pending.Clear();
        local.Agents.AddOrUpdate(LocalAgent(n));
        await Assert.That(dir.Rows.Count).IsEqualTo(1);
        await Assert.That(dir.Rows.Lookup($"local:{n}").HasValue).IsTrue();
    }

    /// A same-id row on the remote lane is a different agent, so it neither retires a local
    /// launch's placeholder nor hides the daemon's own pending entry.
    [Test]
    public async Task A_remote_row_with_the_same_id_leaves_the_local_stand_ins_alone() {
        var (local, remote, dir) = BuildWithRemote();
        using var _d = dir;
        remote.Cache.AddOrUpdate(new Capacitor.Remote.Models.AgentInstanceDto {
            AgentId = "p6", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "claude", RepoOwner = "o", RepoName = "r",
        });

        dir.AddPlaceholder("p6", "claude", "/r", null, null);
        await Assert.That(dir.Rows.Lookup("pending:p6").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:p6").HasValue).IsTrue();

        local.Pending.AddOrUpdate(Pending("p6", stage: "initialized"));
        await Assert.That(dir.Rows.Lookup("pending:p6").Value.LaunchStage).IsEqualTo("initialized");
    }

    [Test]
    public async Task A_pending_row_never_claims_a_session() {
        var (local, dir) = Build();
        using var _d = dir;
        local.Pending.AddOrUpdate(Pending("p4"));
        await Assert.That(dir.VendorOfSession("s1")).IsNull();
    }
}
