using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>What certification left: the certified citations in request order, how many refs were dropped, and whether
/// the scope itself was lost (then nothing is certified).</summary>
public sealed record EvidenceCertificationOutcome(bool ScopeLost, IReadOnlyList<EvalEvidenceCitation> Certified, int Dropped);
