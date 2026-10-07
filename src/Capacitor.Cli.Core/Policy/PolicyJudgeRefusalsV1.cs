namespace Capacitor.Cli.Core.Policy;

/// <param name="Complete">True only when the seam read the whole session's refusal history and every
/// entry fits. A false here forbids the judge to allow, so it must never be a lazy default.</param>
public sealed record PolicyJudgeRefusalsV1(bool Complete, string Source, PolicyJudgeDeclaredRefusalV1[] Entries) {
    public const string SourceTranscript = "transcript";
    public const string SourceBridge = "bridge";

    public static PolicyJudgeRefusalsV1 Unknown(string source) => new(false, source, []);
}
