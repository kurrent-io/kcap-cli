using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using static Capacitor.Cli.Daemon.Tests.Unit.Services.AgentOrchestratorHarness;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorPermissionAttributionTests {
    const string S1 = "6ba7b8109dad11d180b400c04fd430c8";

    static AgentInstance Agent(string id, string worktree, string? sessionId = null, PolicySnapshot? policy = null, bool fresh = false) =>
        new(id, null, "", null, "/repo", "claude", new FakeHostedAgentRuntime("claude", true),
            new WorktreeInfo(worktree, "b", "/repo"), new CancellationTokenSource()) {
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            CreatedAt     = DateTime.UtcNow,
            LastOutputAt  = DateTime.UtcNow,
            SessionId = sessionId, PolicySnapshot = policy, BridgedSinceStart = fresh
        };

    static AgentOrchestrator Build() =>
        BuildOrchestrator(new FakeServerConnectionForAttribution(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task Agent_id_rung_matches_raw_then_canonical_and_needs_exactly_one() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", "/w1")); // a non-"N" key
        orch.RegisterAgentForTest(Agent("not-a-guid-key", "/w2"));

        await Assert.That(orch.HandleAttributePermission(new("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", S1, null))!.Value.AgentId)
            .IsEqualTo("6BA7B810-9DAD-11D1-80B4-00C04FD430C8");                                   // raw
        await Assert.That(orch.HandleAttributePermission(new("6ba7b8109dad11d180b400c04fd430c8", S1, null))!.Value.AgentId)
            .IsEqualTo("6BA7B810-9DAD-11D1-80B4-00C04FD430C8");                                   // canonical
        await Assert.That(orch.HandleAttributePermission(new("not-a-guid-key", S1, null))!.Value.AgentId)
            .IsEqualTo("not-a-guid-key");                                                          // raw, non-GUID
        await Assert.That(orch.HandleAttributePermission(new("unknown", "ffffffffffffffffffffffffffffffff", null))).IsNull();
    }

    [Test]
    public async Task Session_rung_matches_any_guid_shape_and_falls_through_on_two_matches() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: "6BA7B810-9DAD-11D1-80B4-00C04FD430C8"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, null))!.Value.AgentId).IsEqualTo("a1");

        orch.RegisterAgentForTest(Agent("a2", "/w2", sessionId: S1));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, null))).IsNull();
    }

    [Test]
    public async Task Cwd_rung_matches_one_worktree_and_falls_through_on_a_shared_checkout() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/repo/.capacitor/worktrees/agent-a1"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, "/repo/.capacitor/worktrees/agent-a1/"))!.Value.AgentId).IsEqualTo("a1");

        orch.RegisterAgentForTest(Agent("b1", "/shared"));
        orch.RegisterAgentForTest(Agent("b2", "/shared"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, "/shared"))).IsNull();
    }

    [Test]
    public async Task Malformed_session_id_is_unattributed() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: S1));
        await Assert.That(orch.HandleAttributePermission(new("a1", "nope", "/w1"))).IsNull();
    }

    /// The launch-bound snapshot and whether this daemon launched the agent fresh reach the permission seam on
    /// every rung. Both are defaulted record arguments, so dropping either from one return still compiles and
    /// still attributes the right agent; only an assertion on the value catches it.
    [Test]
    public async Task Every_attribution_rung_carries_the_agents_launch_facts() {
        var snap = new PolicySnapshot("snap-1", [], true, ["user policy unreadable"]);

        // No session id, so the session rung never matches and a cwd query falls through to it.
        await using var byId = Build();
        byId.RegisterAgentForTest(Agent("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", "/w1", policy: snap, fresh: true));
        await using var bySession = Build();
        bySession.RegisterAgentForTest(Agent("a1", "/w1", sessionId: S1, policy: snap, fresh: true));

        AttributedAgent?[] rungs = [
            byId.HandleAttributePermission(new("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", S1, null)), // raw agent id
            byId.HandleAttributePermission(new("6ba7b8109dad11d180b400c04fd430c8", S1, null)),     // canonical agent id
            byId.HandleAttributePermission(new(null, S1, "/w1")),                                  // cwd
            bySession.HandleAttributePermission(new(null, S1, null)),                              // session id
        ];

        foreach (var rung in rungs) await Assert.That(rung!.Value.PolicySnapshot).IsSameReferenceAs(snap);
        await Assert.That(rungs[^1]!.Value.BridgedSinceStart).IsTrue();
    }

    /// Only the session a fresh agent reported as its own started under this daemon; one resumed inside it, or a
    /// request naming another session, may carry refusals the daemon never saw.
    [Test]
    public async Task Only_a_fresh_agents_own_session_is_bridged_since_start() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: S1, fresh: true));
        orch.RegisterAgentForTest(Agent("a2", "/w2", sessionId: "6ba7b8119dad11d180b400c04fd430c8"));

        await Assert.That(orch.HandleAttributePermission(new("a1", S1, null))!.Value.BridgedSinceStart).IsTrue();
        await Assert.That(orch.HandleAttributePermission(new("a1", "ffffffffffffffffffffffffffffffff", null))!.Value.BridgedSinceStart).IsFalse();
        await Assert.That(orch.HandleAttributePermission(new("a2", "6ba7b8119dad11d180b400c04fd430c8", null))!.Value.BridgedSinceStart).IsFalse();
    }

    [Test]
    public async Task An_agent_launched_without_a_policy_snapshot_attributes_with_none() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: S1));

        var attributed = orch.HandleAttributePermission(new("a1", S1, null));

        await Assert.That(attributed!.Value.AgentId).IsEqualTo("a1");
        await Assert.That(attributed.Value.PolicySnapshot).IsNull();
    }

    [Test]
    public async Task Teardown_withdraws_the_agents_pending_permissions_before_unpublishing() {
        await using var orch = Build();
        var agent = Agent("a1", "/w1");
        orch.RegisterAgentForTest(agent);
        var settlement = orch.PermissionBrokerForTest.Register(
            new("r1", "a1", S1, "claude", "Bash", null, null, false, false, "t"));

        orch.UnpublishAgentForTest("a1");
        var s = await settlement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(s.Source).IsEqualTo("agent_gone");
    }

    sealed class FakeServerConnectionForAttribution() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        new FixedCredentialSource(),
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance, TimeProvider.System);
}
