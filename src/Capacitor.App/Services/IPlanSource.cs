using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

/// One read of the plans a session works from, however the app reaches the server.
public interface IPlanSource {
    Task<SessionPlansRead> ReadAsync(string sessionId, CancellationToken ct);
}
