namespace Capacitor.App.Materials;

/// FailureReason is set exactly when Availability is PipelineFailed.
public sealed record MaterialState(
    SurfaceMaterial Effective,
    SurfaceMaterial? Requested,
    MaterialAvailability Availability,
    string? FailureReason,
    bool ReduceTransparency) {
    public static MaterialState Opaque { get; } =
        new(SurfaceMaterial.Opaque, null, MaterialAvailability.Available, null, false);
}
