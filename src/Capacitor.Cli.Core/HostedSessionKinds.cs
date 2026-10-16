namespace Capacitor.Cli.Core;

public static class HostedSessionKinds {
    public const string HostedAgentWire     = "hosted_agent";
    public const string FlowParticipantWire = "flow_participant";

    /// <summary>Shared by the launch marker and the server's session-start ack. An unknown value
    /// reads as <see cref="HostedSessionKind.None"/>: it withholds nothing.</summary>
    public static HostedSessionKind Parse(string? value) => value switch {
        HostedAgentWire     => HostedSessionKind.HostedAgent,
        FlowParticipantWire => HostedSessionKind.FlowParticipant,
        _                   => HostedSessionKind.None
    };

    public static string? ToWire(this HostedSessionKind kind) => kind switch {
        HostedSessionKind.HostedAgent     => HostedAgentWire,
        HostedSessionKind.FlowParticipant => FlowParticipantWire,
        _                                 => null
    };
}
