using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Services;

/// One read of a session's work context, however the app reaches the server.
public interface IWorkContextSource {
    Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct);
    /// The last read this viewer may still be shown, to fill a pane while it reads again.
    WorkContextRead? Cached(string sessionId);
}
