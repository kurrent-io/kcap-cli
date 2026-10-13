namespace Capacitor.Cli.Core.Policy;

/// <param name="UserMessages">The last human user messages of the transcript, oldest first; the last
/// opened the turn the action belongs to.</param>
public sealed record PolicyJudgeTurnSetV1(PolicyJudgeDeclaredTurnV1[] UserMessages, string? ToolUseId);
