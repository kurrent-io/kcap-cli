namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record JudgeLedgerCall(int Seq, string Tool, string ArgsJson, string Outcome, string? StopReason, string? Error, int Bytes);
