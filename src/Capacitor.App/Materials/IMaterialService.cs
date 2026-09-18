namespace Capacitor.App.Materials;

public interface IMaterialService {
    MaterialState Current { get; }

    /// Replays Current to a new subscriber, then every change of any field.
    IObservable<MaterialState> States { get; }

    Task SetAsync(SurfaceMaterial material);

    /// Latches for the session; later reports are ignored.
    void ReportPipelineFailure(string reason);
}
