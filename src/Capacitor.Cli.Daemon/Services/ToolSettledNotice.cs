namespace Capacitor.Cli.Daemon.Services;

/// What a hosted vendor's hook reports as finished, and so which of its pending prompts are moot:
/// one tool by its id, a subagent's whole turn, or, with neither, the main agent's own turn.
internal readonly record struct ToolSettledNotice(string? ToolUseId, string? SubagentId);
