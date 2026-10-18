using System.Text.Json;

namespace Capacitor.Cli.Daemon.Services;

/// What a hosted vendor's hook reports as finished, and so which of its pending prompts are moot:
/// one tool by its id or by its name and input, a subagent's whole turn, or, with none of those,
/// the main agent's own turn. The session is the one the hook ran in; a prompt registered under
/// another is not its to retire. Claude's permission hook raises a prompt without a tool-use id,
/// which is why the name and input travel too.
internal readonly record struct ToolSettledNotice(
    string SessionId, string? ToolUseId, string? SubagentId, string? ToolName = null, JsonElement? ToolInput = null) {
    public bool NamesATool => ToolUseId is not null || ToolName is not null;
}
