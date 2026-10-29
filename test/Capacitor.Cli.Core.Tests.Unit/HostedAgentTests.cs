namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// What the daemon exports into an agent it hosts, as this process reads it back. Both variables
/// are inherited by every spawned child, so the cohort is the whole assembly.
/// </summary>
[NotInParallel]
public class HostedAgentTests {
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task A_blank_id_names_no_agent(string? raw) {
        using var _ = EnvScope.Exclusive("KCAP_AGENT_ID", raw);

        await Assert.That(HostedAgent.FromEnvironment().AgentId).IsNull();
    }

    [Test]
    public async Task An_id_is_read_verbatim() {
        using var _ = EnvScope.Exclusive("KCAP_AGENT_ID", "6ba7b8109dad11d180b400c04fd430c8");

        await Assert.That(HostedAgent.FromEnvironment().AgentId).IsEqualTo("6ba7b8109dad11d180b400c04fd430c8");
    }

    /// <summary>
    /// The daemon exports one exact value here; the hook decides whether the bridge owns this
    /// session's prompts on it, so anything else has to read as not rendered rather than as
    /// truthy. Written with the literals, so a rename on either side has to be deliberate.
    /// </summary>
    [Test]
    [Arguments("1", true)]
    [Arguments("0", false)]
    [Arguments("true", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Only_the_exported_marker_marks_a_rendered_session(string? raw, bool rendered) {
        using var _ = EnvScope.Exclusive("KCAP_RENDERED_AGENT", raw);

        await Assert.That(HostedAgent.FromEnvironment().IsRendered).IsEqualTo(rendered);
    }

    /// The daemon exports the id unconditionally and the bridge only when it has one, so an id
    /// without a bridge is the ordinary shape rather than a broken environment.
    [Test]
    public async Task An_id_without_a_bridge_is_hosted() {
        using var id     = EnvScope.Exclusive("KCAP_AGENT_ID", "agent-1");
        using var bridge = EnvScope.Exclusive("KCAP_DAEMON_URL", null);

        var hosted = HostedAgent.FromEnvironment();

        await Assert.That(hosted.AgentId).IsEqualTo("agent-1");
        await Assert.That(hosted.Bridge).IsEqualTo(DaemonBridge.None);
    }

    [Test]
    public async Task A_bridge_is_parsed_alongside_the_id() {
        using var id     = EnvScope.Exclusive("KCAP_AGENT_ID", "agent-1");
        using var bridge = EnvScope.Exclusive("KCAP_DAEMON_URL", "http://127.0.0.1:9/b/");

        var hosted = HostedAgent.FromEnvironment();

        await Assert.That(hosted.AgentId).IsEqualTo("agent-1");
        await Assert.That(hosted.Bridge).IsEqualTo(new DaemonBridge.Loopback("http://127.0.0.1:9/b"));
    }

    /// <summary>Written with the literals: the daemon and the server's ack both send these.</summary>
    [Test]
    [Arguments("flow_participant", HostedSessionKind.FlowParticipant)]
    [Arguments("hosted_agent", HostedSessionKind.HostedAgent)]
    [Arguments("reviewer", HostedSessionKind.None)]
    [Arguments("", HostedSessionKind.None)]
    [Arguments(null, HostedSessionKind.None)]
    public async Task The_session_kind_is_read_from_its_marker(string? raw, HostedSessionKind kind) {
        using var _ = EnvScope.Exclusive("KCAP_SESSION_KIND", raw);

        await Assert.That(HostedAgent.FromEnvironment().Kind).IsEqualTo(kind);
    }

    [Test]
    public async Task A_participant_launch_is_marked_with_its_kind_and_role() {
        var markers = HostedAgent.LaunchMarkers(isFlowParticipant: true, flowRole: "reviewer");

        await Assert.That(markers.ToDictionary()).IsEquivalentTo(new Dictionary<string, string> {
            ["KCAP_SESSION_KIND"] = "flow_participant",
            ["KCAP_FLOW_ROLE"]    = "reviewer"
        });
    }

    [Test]
    [Arguments(false, "reviewer")]
    [Arguments(false, null)]
    public async Task Any_other_launch_is_a_hosted_agent_with_no_role(bool participant, string? role) {
        var markers = HostedAgent.LaunchMarkers(participant, role);

        await Assert.That(markers.ToDictionary()).IsEquivalentTo(new Dictionary<string, string> { ["KCAP_SESSION_KIND"] = "hosted_agent" });
    }

    [Test]
    public async Task A_participant_whose_launch_named_no_role_gets_none() {
        var markers = HostedAgent.LaunchMarkers(isFlowParticipant: true, flowRole: null);

        await Assert.That(markers.ToDictionary()).IsEquivalentTo(new Dictionary<string, string> { ["KCAP_SESSION_KIND"] = "flow_participant" });
    }
}
