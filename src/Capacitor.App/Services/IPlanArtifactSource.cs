using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

/// The Artefacts tab's server reads, however the app reaches the server: a session's plan artifacts
/// with their bodies, and the published pages citing it.
public interface IPlanArtifactSource {
    Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct);
    /// The last read this viewer may still be shown, to fill a pane while it reads again.
    PlanArtifactsRead? Cached(string sessionId);
    Task<SessionPagesRead> ReadPagesAsync(string sessionId, CancellationToken ct);
    SessionPagesRead? CachedPages(string sessionId);
}
