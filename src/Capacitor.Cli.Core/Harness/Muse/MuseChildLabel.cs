namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>How a Muse child session is registered with the server: <see cref="AgentId"/> is the
/// dashless child session id, <see cref="AgentType"/> what the parent log called it.</summary>
public sealed record MuseChildLabel(string ChildSessionId, string AgentId, string AgentType);
