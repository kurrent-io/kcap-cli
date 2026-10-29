namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex.agent_message` on a multi-agent handoff: the author and recipient agent paths
/// (`/root` is the top-level session, `/root/&lt;name&gt;` a subagent).
public sealed record CodexAgentMessageExtension {
    public string? Author    { get; init; }
    public string? Recipient { get; init; }
}
