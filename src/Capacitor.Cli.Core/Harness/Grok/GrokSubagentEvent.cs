namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>A parent's record of one of its subagents starting or finishing.</summary>
public sealed record GrokSubagentEvent(string ChildSessionId, string? SubagentType, bool Finished);
