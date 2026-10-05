namespace Capacitor.Cli.Core.Policy;

/// <summary>The judge's 200 body. Every member but <c>outcome</c> is provenance the decision event
/// carries; nothing else is read.</summary>
public sealed record PolicyJudgeResponseV1(
    string? Outcome, string? ModelOutcome, string? Rationale, string? UserAuthorization, string? Clamped,
    string? Lineage, string? Cache, string? FailureClass, string? ConsultationId, string? ArtifactId);
