using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class SessionStartAudienceTests {
    static HostedAgent Agent(string? id, HostedSessionKind kind) => new(id, false, DaemonBridge.None) { Kind = kind };

    [Test]
    public async Task A_terminal_session_gets_everything() {
        var audience = SessionStartAudience.From(HostedAgent.Terminal);

        await Assert.That(audience.GetsDriverGuidance).IsTrue();
        await Assert.That(audience.GetsOnboarding).IsTrue();
    }

    /// <summary>An ACP runtime stamps the kind without an agent id; the kind alone makes it hosted.</summary>
    [Test]
    [Arguments("agent-1", HostedSessionKind.None)]
    [Arguments(null, HostedSessionKind.HostedAgent)]
    public async Task A_hosted_session_keeps_driver_guidance_and_loses_onboarding(string? id, HostedSessionKind kind) {
        var audience = SessionStartAudience.From(Agent(id, kind));

        await Assert.That(audience.GetsDriverGuidance).IsTrue();
        await Assert.That(audience.GetsOnboarding).IsFalse();
    }

    [Test]
    public async Task A_flow_participant_loses_both() {
        var audience = SessionStartAudience.From(Agent(null, HostedSessionKind.FlowParticipant));

        await Assert.That(audience.GetsDriverGuidance).IsFalse();
        await Assert.That(audience.GetsOnboarding).IsFalse();
    }

    [Test]
    [Arguments("""{"session_kind":"flow_participant","flow_role":"reviewer"}""", false, false)]
    [Arguments("""{"session_kind":"hosted_agent"}""", true, false)]
    [Arguments("""{"session_kind":"something_new"}""", true, true)]
    [Arguments("""{"session_kind":7}""", true, true)]
    [Arguments("{}", true, true)]
    public async Task The_ack_narrows_an_unmarked_session(string ack, bool driver, bool onboarding) {
        var audience = SessionStartAudience.Interactive.WithAck(JsonNode.Parse(ack));

        await Assert.That(audience.GetsDriverGuidance).IsEqualTo(driver);
        await Assert.That(audience.GetsOnboarding).IsEqualTo(onboarding);
    }

    [Test]
    public async Task The_ack_never_widens_what_the_launch_marker_withheld() {
        var participant = SessionStartAudience.From(Agent("a", HostedSessionKind.FlowParticipant));

        await Assert.That(participant.WithAck(JsonNode.Parse("""{"session_kind":"hosted_agent"}"""))).IsEqualTo(participant);
        await Assert.That(participant.WithAck(null)).IsEqualTo(participant);
    }
}
