namespace Capacitor.Cli.Daemon.Services;

/// What a hosted vendor's hook reports as finished, and so which of its pending prompts are moot:
/// one tool by its id, a subagent's whole turn, or, with neither, the main agent's own turn. The
/// session is the one the hook ran in; a prompt registered under another is not its to retire.
internal readonly record struct ToolSettledNotice(string SessionId, string? ToolUseId, string? SubagentId);
