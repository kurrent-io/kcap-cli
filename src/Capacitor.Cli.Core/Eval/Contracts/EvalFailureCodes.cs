namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Coded failure reasons the CLI can attach to a judge invocation that produced no
/// verdict — mirrors the codes the server's <c>EvalFailureCodes</c> accepts.</summary>
static class EvalFailureCodes {
    public const string JudgeTimeout       = "judge_timeout";
    public const string ChatError          = "chat_error";
    public const string VerdictParseFailed = "verdict_parse_failed";
}
