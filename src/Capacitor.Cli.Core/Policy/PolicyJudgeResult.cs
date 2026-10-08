namespace Capacitor.Cli.Core.Policy;

/// <summary>What a judge consultation means to a seam. <see cref="PolicyOutcome.None"/> is
/// pass-through, and then <see cref="FailureClass"/> says why the judge did not decide.</summary>
public sealed record PolicyJudgeResult(
    PolicyOutcome Outcome, PolicyJudgeConsultationV1? Consultation, string? FailureClass, string? Rationale = null) {
    public const string Uncertain = "judge_uncertain";
    public const string Timeout = "judge_timeout";
    public const string TransportError = "judge_transport_error";
    public const string HttpError = "judge_http_error";
    public const string MalformedResponse = "judge_malformed_response";
    public const string Error = "judge_error";

    public static PolicyJudgeResult PassThrough(string failureClass, PolicyJudgeConsultationV1? consultation = null) =>
        new(PolicyOutcome.None, consultation, failureClass);
}
