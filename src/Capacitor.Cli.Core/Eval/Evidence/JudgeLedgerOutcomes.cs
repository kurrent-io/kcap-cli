namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>What happened to one judge tool call: answered, refused, charged but not executed, or failed.</summary>
public static class JudgeLedgerOutcomes {
    public const string Executed    = "executed";
    public const string Refused     = "refused";
    public const string NotExecuted = "not_executed";
    public const string Error       = "error";
}
