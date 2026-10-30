using Capacitor.Cli.Core.Pages;

namespace Capacitor.App.Services;

/// One read of the pages a session published, however the app reaches the server.
public interface IPageSource {
    Task<SessionPagesRead> ReadAsync(string sessionId, CancellationToken ct);
    /// The last read this viewer may still be shown, to fill a pane while it reads again.
    SessionPagesRead? Cached(string sessionId);
}
