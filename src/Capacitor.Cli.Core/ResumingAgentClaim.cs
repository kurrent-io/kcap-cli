namespace Capacitor.Cli.Core;

/// <summary>An agent a booting daemon is about to resume, claimed on connect so the server spares it.</summary>
public sealed record ResumingAgentClaim(string AgentId, string PredecessorInstanceId, string? SessionId);
