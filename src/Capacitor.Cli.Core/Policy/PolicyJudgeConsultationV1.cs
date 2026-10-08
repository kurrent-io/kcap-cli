namespace Capacitor.Cli.Core.Policy;

/// <summary>The judge consultation a decision used, as the server answered it. The server records a
/// coalesced or cached consultation once, so this is where per-seam accounting lives.</summary>
public sealed record PolicyJudgeConsultationV1(
    string? ConsultationId, string? ArtifactId, string? Outcome, string? ModelOutcome, string? UserAuthorization,
    string? Clamped, string? Lineage, string? Cache, string? FailureClass) {
    public static PolicyJudgeConsultationV1 Of(PolicyJudgeResponseV1 r) => new(
        r.ConsultationId, r.ArtifactId, r.Outcome, r.ModelOutcome, r.UserAuthorization,
        r.Clamped, r.Lineage, r.Cache, r.FailureClass);
}
