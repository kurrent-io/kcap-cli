using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Which SessionStart fragments a session is composed for. A flow participant is not driving the
/// work, so it gets none of the driver guidance (work items, plans, memory index, next work, the
/// completion block). No hosted session is a person's first session after setup, so none gets the
/// onboarding fragments (harness nudge, first-run notice, update nudge). Repo guidelines and
/// coordination notices go to every session.
/// </summary>
sealed record SessionStartAudience(bool IsHosted, bool IsFlowParticipant) {
    public static readonly SessionStartAudience Interactive = new(false, false);

    public static SessionStartAudience From(HostedAgent hosted) =>
        new(hosted.IsHosted, hosted.Kind == HostedSessionKind.FlowParticipant);

    /// <summary>Narrows by the kind the server resolved at start, for a session whose launch carried
    /// no marker. Never widens: an ack cannot hand back what the launch marker withheld.</summary>
    public SessionStartAudience WithAck(JsonNode? ack) {
        var kind = ack is JsonObject obj && obj["session_kind"] is JsonValue value && value.TryGetValue<string>(out var s)
            ? HostedSessionKinds.Parse(s)
            : HostedSessionKind.None;

        return kind switch {
            HostedSessionKind.FlowParticipant => new(true, true),
            HostedSessionKind.HostedAgent     => this with { IsHosted = true },
            _                                 => this
        };
    }

    public bool GetsDriverGuidance => !IsFlowParticipant;

    public bool GetsOnboarding => !IsHosted;
}
