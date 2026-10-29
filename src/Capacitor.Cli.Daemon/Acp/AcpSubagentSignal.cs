namespace Capacitor.Cli.Daemon.Acp;

/// <summary>A vendor's report that a subagent started or finished. The subagent runs as its own ACP
/// session on the parent's connection, so <see cref="ChildSessionId"/> is the <c>sessionId</c> its
/// updates carry; <see cref="AgentId"/> is the id its events are recorded under, the one the vendor's
/// watched sessions use.</summary>
internal sealed record AcpSubagentSignal(bool Started, string ChildSessionId, string AgentId, string? AgentType);
