using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;

namespace Capacitor.App.Services;

/// One read of the pages a session published, however the app reaches the server.
public interface IPageSource {
    Task<SessionPagesRead> ReadAsync(string sessionId, CancellationToken ct);
    /// The last read this viewer may still be shown, to fill a pane while it reads again.
    SessionPagesRead? Cached(string sessionId);

    /// One page's HTML at version, or its latest when null. Never cached.
    Task<PageContentRead> ReadContentAsync(string artefactId, int? version, CancellationToken ct);
    Task<PageBridgeReply> SubmitAsync(string artefactId, int version, JsonElement payload, CancellationToken ct);
    Task<PageBridgeReply> MineAsync(string artefactId, int version, CancellationToken ct);
    Task<PageBridgeReply> ResultsAsync(string artefactId, int version, CancellationToken ct);
    Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct);
    /// The signed-in viewer's display name, empty when there is none.
    Task<string> ViewerNameAsync(CancellationToken ct);
}
