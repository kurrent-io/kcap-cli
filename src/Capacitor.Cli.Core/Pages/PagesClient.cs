using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Pages;

/// The HTTP channel for a session's pages. <paramref name="http"/> must already carry the
/// caller's bearer. Degrades rather than throws, except for the caller's own cancellation, which
/// propagates.
public sealed class PagesClient(HttpClient http, string serverUrl) {
    readonly string _base = serverUrl.TrimEnd('/');

    public async Task<SessionPagesRead> ListForSessionAsync(string sessionId, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } id) return SessionPagesRead.Of(SessionPlansReadKind.Unavailable);

        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/sessions/{Uri.EscapeDataString(id)}/artefacts");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            return (int)resp.StatusCode switch {
                401        => SessionPagesRead.Of(SessionPlansReadKind.SignedOut),
                403 or 404 => SessionPagesRead.Of(SessionPlansReadKind.Unavailable),
                >= 200 and < 300 when await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactListDto, ct).ConfigureAwait(false) is { } body
                    => new(SessionPlansReadKind.Ready, body.Artefacts),
                _ => SessionPagesRead.Of(SessionPlansReadKind.Unreachable),
            };
        } catch (Exception e) when (IsTransient(e, ct)) {
            return SessionPagesRead.Of(SessionPlansReadKind.Unreachable);
        }
    }

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

    public async Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct) {
        if (page.IsOwner) return PageResultsAccess.Visible;
        var reply = await ResultsAsync(artefactId, page.LatestVersion, ct).ConfigureAwait(false);
        return reply.Status switch {
            200 when reply.Body is { ValueKind: JsonValueKind.Object } body
                     && body.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "owner"
                => PageResultsAccess.Hidden,
            200        => PageResultsAccess.Visible,
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
