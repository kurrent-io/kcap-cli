using System.Text.Json;

namespace Capacitor.Cli.Daemon.Services;

/// What a hosted vendor's hook reports as finished, and so which of its pending prompts are moot:
/// one tool by its id, a subagent's whole turn, or, with neither, the main agent's own turn. The
/// session is the one the hook ran in; a prompt registered under another is not its to retire. A
/// finished tool also carries its name and input, which is all a prompt registered without the
/// tool's id (Claude's) can be matched by, and when the hook sent it.
internal readonly record struct ToolSettledNotice(
    string SessionId, string? ToolUseId, string? SubagentId, string? ToolName = null, JsonElement? ToolInput = null,
    DateTimeOffset? SentAt = null);
