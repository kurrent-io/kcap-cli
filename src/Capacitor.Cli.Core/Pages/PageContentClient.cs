using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Pages;

/// The HTTP channel for one page's content and its response calls, as the web viewer makes them.
/// <paramref name="http"/> must already carry the caller's bearer. Degrades rather than throws,
/// except for the caller's own cancellation.
public sealed class PageContentClient(HttpClient http, string serverUrl) {
    readonly string _base = serverUrl.TrimEnd('/');

    /// The detail names the version, then the content is read pinned to it, so a publish between
    /// the two cannot pair one version's metadata with another's HTML.
    public async Task<PageContentRead> ReadContentAsync(string artefactId, int? version, CancellationToken ct) {
        var id = Uri.EscapeDataString(artefactId);
        try {
            using var detailReq  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/artefacts/{id}");
            using var detailResp = await http.SendAsync(detailReq, ct).ConfigureAwait(false);
            if (Totalize(detailResp) is { } failed) return failed;
            var detail = await detailResp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactDetailDto, ct).ConfigureAwait(false);
            if (detail?.Artefact is not { } page) return new PageContentRead.Unreachable();

            var pinned = version ?? page.LatestVersion;
            using var contentReq  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/artefacts/{id}/content?v={pinned}&theme=dark");
            using var contentResp = await http.SendAsync(contentReq, ct).ConfigureAwait(false);
            if (Totalize(contentResp) is { } contentFailed) return contentFailed;
            return new PageContentRead.Found(page, pinned, await contentResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new PageContentRead.Unreachable();
        }
    }

    static PageContentRead? Totalize(HttpResponseMessage resp) => (int)resp.StatusCode switch {
        >= 200 and < 300 => null,
        401              => new PageContentRead.SignedOut(),
        403 or 404       => new PageContentRead.NotFound(),
        _                => new PageContentRead.Unreachable(),
    };

    public Task<PageBridgeReply> SubmitAsync(string artefactId, int version, JsonElement payload, CancellationToken ct) {
        var json = JsonSerializer.Serialize(new PageResponseBody(version, payload), CapacitorJsonContext.Default.PageResponseBody);
        return SendBridgeAsync(HttpMethod.Post, $"/api/artefacts/{Uri.EscapeDataString(artefactId)}/responses", new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json), ct);
    }

    public Task<PageBridgeReply> MineAsync(string artefactId, int version, CancellationToken ct) =>
        SendBridgeAsync(HttpMethod.Get, $"/api/artefacts/{Uri.EscapeDataString(artefactId)}/responses?version={version}&mine=true", null, ct);

    public Task<PageBridgeReply> ResultsAsync(string artefactId, int version, CancellationToken ct) =>
        SendBridgeAsync(HttpMethod.Get, $"/api/artefacts/{Uri.EscapeDataString(artefactId)}/results?version={version}", null, ct);

    /// The owner always sees results; anyone else sees them unless the server answers that only the
    /// owner may.
    public async Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct) {
        if (page.IsOwner) return PageResultsAccess.Visible;
        var reply = await ResultsAsync(artefactId, page.LatestVersion, ct).ConfigureAwait(false);
        return reply.Status switch {
            200 when reply.Body is { ValueKind: JsonValueKind.Object } body
                     && body.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "owner"
                => PageResultsAccess.Hidden,
            200        => PageResultsAccess.Visible,
            401        => PageResultsAccess.SignedOut,
            403 or 404 => PageResultsAccess.Hidden,
            _          => PageResultsAccess.Unknown,
        };
    }

    async Task<PageBridgeReply> SendBridgeAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct) {
        try {
            using var req  = new HttpRequestMessage(method, _base + path) { Content = content };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new((int)resp.StatusCode, ParseBody(text));
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0, null);
        }
    }

    static JsonElement? ParseBody(string text) {
        try {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        } catch (JsonException) {
            return null;
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}
