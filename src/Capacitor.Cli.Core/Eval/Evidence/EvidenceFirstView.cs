namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>A strategy's first view as delivered: the stamps it puts on the assessment, its rendered block and the seeded pages
/// it adds to the ledger.</summary>
public sealed record EvidenceFirstView(string Strategy, string StrategyVersion, string Text, IReadOnlyList<JudgeLedgerPage> Pages);
