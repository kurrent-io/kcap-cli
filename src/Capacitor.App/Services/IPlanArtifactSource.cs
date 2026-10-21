using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

/// One read of a session's plan artifacts, bodies included, however the app reaches the server.
public interface IPlanArtifactSource {
    Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct);
    /// The last read this viewer may still be shown, to fill a pane while it reads again.
    PlanArtifactsRead? Cached(string sessionId);
}
