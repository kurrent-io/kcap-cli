using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;

namespace Capacitor.App.Services;

/// What the in-app page viewer reads and sends for one page: its HTML and the response calls its
/// bridge makes. Nothing here is cached.
public interface IPageContentSource {
    /// One page's HTML at version, or its latest when null.
    Task<PageContentRead> ReadContentAsync(string artefactId, int? version, CancellationToken ct);
    Task<PageBridgeReply> SubmitAsync(string artefactId, int version, JsonElement payload, CancellationToken ct);
    Task<PageBridgeReply> MineAsync(string artefactId, int version, CancellationToken ct);
    Task<PageBridgeReply> ResultsAsync(string artefactId, int version, CancellationToken ct);
    Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct);
    /// The signed-in viewer's display name, empty when there is none.
    Task<string> ViewerNameAsync(CancellationToken ct);
}
