namespace Capacitor.Cli.Core;

/// <summary>What the daemon launched a session as. <see cref="None"/> is anything it did not launch.</summary>
public enum HostedSessionKind {
    None,
    HostedAgent,
    FlowParticipant
}
