namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>The three outcomes a judge can report for a question — mirrors the server's
/// <c>Capacitor.Events.EvalOutcomes</c>. Anything else is a parse failure.</summary>
static class EvalOutcomes {
    public const string Assessed             = "assessed";
    public const string InsufficientEvidence = "insufficient_evidence";
    public const string NotApplicable        = "not_applicable";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) {
        Assessed, InsufficientEvidence, NotApplicable
    };
}
