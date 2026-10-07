namespace Capacitor.Cli.Core.Plans;

/// One read of a session's plan artifacts, totalized the way the plans read is; Body rides only
/// with Ready.
public sealed record PlanArtifactsRead(SessionPlansReadKind Kind, PlanArtifactsResponseDto? Body) {
    public static PlanArtifactsRead Of(SessionPlansReadKind kind) => new(kind, null);
}
