namespace Capacitor.Cli.Daemon.Acp;

/// <summary>A vendor's report that a subagent started or finished. The subagent runs as its own ACP
/// session on the parent's connection, so <see cref="ChildSessionId"/> is the <c>sessionId</c> its
/// updates carry.</summary>
internal sealed record AcpSubagentSignal(bool Started, string ChildSessionId, string? AgentType);
