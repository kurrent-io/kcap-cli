namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Coded failure reasons the CLI can attach to a judge invocation that produced no verdict — a subset of the
/// codes the server's <c>EvalFailureCodes</c> accepts.</summary>
static class EvalFailureCodes {
    public const string IterationCap       = "iteration_cap";
    public const string JudgeTimeout       = "judge_timeout";
    public const string ChatError          = "chat_error";
    public const string VerdictParseFailed = "verdict_parse_failed";
    public const string SpendBudget        = "spend_budget";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) {
        IterationCap, JudgeTimeout, ChatError, VerdictParseFailed, SpendBudget
    };
}
