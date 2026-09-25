namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>One obligation as the judge wrote it: <see cref="Anchor"/> and <see cref="Citations"/> are cite handles still to be
/// expanded and certified, and the entry has no id until it is reconciled.</summary>
public sealed record EvalReportedObligation(string Title, string Origin, string Status, string Anchor, IReadOnlyList<string> Citations, string? Note);
