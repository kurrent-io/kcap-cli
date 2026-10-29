namespace Capacitor.Cli.Core.Eval.Contracts;

public static class EvalStopReasons {
    public const string ByteBudget     = "byte_budget";
    public const string IterationCap   = "iteration_cap";
    public const string ToolCallBudget = "tool_call_budget";
    public const string TimeBudget     = "time_budget";
    public const string JudgeStopped   = "judge_stopped";
    public const string TokenBudget    = "token_budget";
    public const string NoToolCalling  = "no_tool_calling";

    public static readonly IReadOnlySet<string> V1 = new HashSet<string>(StringComparer.Ordinal) {
        ByteBudget, IterationCap, ToolCallBudget, TimeBudget, JudgeStopped
    };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(V1, StringComparer.Ordinal) { TokenBudget, NoToolCalling };
}
