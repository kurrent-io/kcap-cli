namespace Capacitor.Cli.Daemon.Services;

/// <summary>Reply of <c>POST /hooks/agent-title</c>: whether the rename took, and the title the
/// server shows afterwards.</summary>
internal sealed record AgentTitleReplyDto(bool Applied, string? Title);
