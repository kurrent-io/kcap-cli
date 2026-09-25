namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceRunBudgets(int MaxToolCalls, long JudgeByteBudgetBytes, int PageBudgetBytes);
