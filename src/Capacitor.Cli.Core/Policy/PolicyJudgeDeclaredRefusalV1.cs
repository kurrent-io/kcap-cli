namespace Capacitor.Cli.Core.Policy;

public sealed record PolicyJudgeDeclaredRefusalV1(string ToolUseId, string Tool, string Target, string? PromptId);
