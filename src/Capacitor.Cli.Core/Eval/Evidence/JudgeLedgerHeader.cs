namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record JudgeLedgerHeader(string EvalRunId, string QuestionId, string ScopeVersion, EvidenceRunBudgets Budgets, DateTimeOffset? SoftDeadline, DateTimeOffset StartedAt) {
    public const int CurrentVersion = 1;
}
