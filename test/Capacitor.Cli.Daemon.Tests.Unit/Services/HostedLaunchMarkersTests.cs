using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Pinned on a dictionary seeded with a known role, because a real launch environment is
/// seeded from the test host's own.</summary>
public class HostedLaunchMarkersTests {
    static RuntimeStartContext Ctx(bool isReviewFlow, string? flowRole) => new(
        AgentId: "agent-1", Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        FlowRole: flowRole);

    [Test]
    public async Task An_inherited_role_does_not_outlive_a_launch_that_names_none() {
        var env = new Dictionary<string, string?> { ["KCAP_FLOW_ROLE"] = "inherited" };

        HostedLaunchMarkers.Apply(env, Ctx(isReviewFlow: false, flowRole: null));

        await Assert.That(env.ContainsKey("KCAP_FLOW_ROLE")).IsFalse();
        await Assert.That(env["KCAP_SESSION_KIND"]).IsEqualTo("hosted_agent");
    }

    [Test]
    public async Task A_participant_replaces_an_inherited_role_with_its_own() {
        var env = new Dictionary<string, string?> { ["KCAP_FLOW_ROLE"] = "inherited" };

        HostedLaunchMarkers.Apply(env, Ctx(isReviewFlow: true, flowRole: "reviewer"));

        await Assert.That(env["KCAP_FLOW_ROLE"]).IsEqualTo("reviewer");
        await Assert.That(env["KCAP_SESSION_KIND"]).IsEqualTo("flow_participant");
    }

    /// <summary>Every runtime stamps these through this helper, so each hosted agent's kcap MCP servers
    /// can ask the daemon that hosts it, whatever that daemon is named.</summary>
    [Test]
    public async Task A_launch_names_its_agent_and_daemon_for_the_session_lookup() {
        var seeded  = new Dictionary<string, string?>();
        var overlay = new Dictionary<string, string>();
        var ctx     = Ctx(isReviewFlow: false, flowRole: null) with { DaemonName = "daemon-a" };

        HostedLaunchMarkers.Apply(seeded, ctx);
        HostedLaunchMarkers.ApplyOverlay(overlay, ctx);

        foreach (var env in new IReadOnlyDictionary<string, string?>[] { seeded, overlay.ToDictionary(kv => kv.Key, kv => (string?)kv.Value) }) {
            await Assert.That(env["KCAP_DAEMON_AGENT_ID"]).IsEqualTo("agent-1");
            await Assert.That(env["KCAP_DAEMON_NAME"]).IsEqualTo("daemon-a");
        }
    }

    /// <summary>Nothing may tie a private agent's session to the agent, an id inherited from the daemon's
    /// own environment included.</summary>
    [Test]
    public async Task A_private_launch_carries_no_agent_for_the_session_lookup() {
        var env = new Dictionary<string, string?> { ["KCAP_DAEMON_AGENT_ID"] = "outer-agent" };

        HostedLaunchMarkers.Apply(env, Ctx(isReviewFlow: false, flowRole: null) with { IsPrivate = true, DaemonName = "daemon-a" });

        await Assert.That(env.ContainsKey("KCAP_DAEMON_AGENT_ID")).IsFalse();
    }
}
