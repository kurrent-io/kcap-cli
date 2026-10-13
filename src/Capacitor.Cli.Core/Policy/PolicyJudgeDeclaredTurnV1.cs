namespace Capacitor.Cli.Core.Policy;

/// <param name="Id">The line's uuid — the event id the recording stores the message under.</param>
public sealed record PolicyJudgeDeclaredTurnV1(string Id, string? PromptId);
