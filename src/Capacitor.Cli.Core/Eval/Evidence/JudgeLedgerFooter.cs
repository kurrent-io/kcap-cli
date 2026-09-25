namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Appended after every call, so the last one on disk is authoritative even if the process is killed mid-question.</summary>
public sealed record JudgeLedgerFooter(int ToolCalls, long DeliveredBytes, string? StopReason, IReadOnlyList<string> SourcesRefused, DateTimeOffset EndedAt);
