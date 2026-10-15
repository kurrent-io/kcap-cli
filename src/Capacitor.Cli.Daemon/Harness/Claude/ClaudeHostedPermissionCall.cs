namespace Capacitor.Cli.Daemon.Harness.Claude;

using System.Text.Json;

/// <summary>One hosted permission request as the Claude hook posted it to the bridge.</summary>
internal sealed record ClaudeHostedPermissionCall(
    string SessionId, string AgentId, string? ToolName, JsonElement? ToolInput, string? Cwd,
    string? ToolUseId, string? TranscriptPath);
