using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

/// One read of a session's plan artifacts, bodies included, however the app reaches the server.
public interface IPlanArtifactSource {
    Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct);
}
