namespace Capacitor.Cli.Core.Harness.Claude;

using Capacitor.Cli.Core.Policy;

/// <summary>What a Claude seam declares to the judge from the session's transcript file. A null
/// turn set makes the consultation windowless.</summary>
public sealed record ClaudeJudgeDeclarations(PolicyJudgeTurnSetV1? Turns, PolicyJudgeRefusalsV1 Refusals) {
    public static readonly ClaudeJudgeDeclarations Unreadable =
        new(null, PolicyJudgeRefusalsV1.Unknown(PolicyJudgeRefusalsV1.SourceTranscript));
}
