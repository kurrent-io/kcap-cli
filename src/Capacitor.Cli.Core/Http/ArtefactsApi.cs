using System.Net;
using System.Net.Http.Json;

namespace Capacitor.Cli.Core.Http;

internal sealed class ArtefactsApi(ICapacitorHttpClient http, CapacitorServer server, TimeProvider time) : IArtefactsApi {
    public async Task<ArtefactWriteResult> PublishAsync(PublishArtefactBody body, CancellationToken ct = default) {
        using var content = JsonContent.Create(body, CapacitorJsonContext.Default.PublishArtefactBody);

        return await WriteAsync((c, token) => c.PostWithRetryAsync($"{server.Url}/api/artefacts", content, time, ct: token), ct);
    }

    public async Task<ArtefactWriteResult> PublishVersionAsync(string artefactId, string html, CancellationToken ct = default) {
        using var content = JsonContent.Create(new PublishArtefactVersionBody(html),
                                               CapacitorJsonContext.Default.PublishArtefactVersionBody);

        return await WriteAsync(
            (c, token) => c.PostWithRetryAsync($"{server.Url}/api/artefacts/{Escape(artefactId)}/versions", content, time, ct: token), ct);
    }

    public async Task<ArtefactWriteResult> SetVisibilityAsync(string artefactId, string visibility,
                                                              List<ArtefactGrantDto>? grants, CancellationToken ct = default) {
        using var content = JsonContent.Create(new SetArtefactVisibilityBody(visibility, grants),
                                               CapacitorJsonContext.Default.SetArtefactVisibilityBody);

        return await WriteAsync(
            (c, token) => c.PutWithRetryAsync($"{server.Url}/api/artefacts/{Escape(artefactId)}/visibility", content, time, ct: token), ct);
    }

    public async Task<ArtefactWriteResult> DeleteAsync(string artefactId, CancellationToken ct = default) =>
        await WriteAsync((c, token) => c.DeleteWithRetryAsync($"{server.Url}/api/artefacts/{Escape(artefactId)}", time, ct: token), ct);

    public async Task<List<ArtefactDto>> ListAsync(CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.GetWithRetryAsync($"{server.Url}/api/artefacts", time, ct: token), ct);

        if (response.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(response);

        var listed = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactListDto, ct);

        return listed?.Artefacts ?? [];
    }

    public async Task<ArtefactSearchResult> SearchAsync(string query, int limit, CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server,
            (c, token) => c.GetWithRetryAsync(
                $"{server.Url}/api/search?q={Uri.EscapeDataString(query)}&mode=pages&limit={limit}", time, ct: token), ct);

        if (response.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(response);

        var answer = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.PageSearchResponseDto, ct);

        return answer?.Pages is { } pages ? new ArtefactSearchResult.Found(pages) : new ArtefactSearchResult.ServerTooOld();
    }

    public async Task<ArtefactReadResult> ReadAsync(string artefactId, int? version, CancellationToken ct = default) {
        using var record = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.GetWithRetryAsync($"{server.Url}/api/artefacts/{Escape(artefactId)}", time, ct: token), ct);

        if (record.StatusCode == HttpStatusCode.NotFound) return new ArtefactReadResult.NotFound();
        if (record.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(record);

        var detail = await record.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactDetailDto, ct)
                  ?? throw new CapacitorApiException(200, "The server's answer could not be read.");

        // Pinned to the version the record names, so a version published between the two reads
        // cannot be served under the older number.
        var read = version ?? detail.Artefact.LatestVersion;

        using var content = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.GetWithRetryAsync($"{server.Url}/artefacts/{Escape(artefactId)}/content?v={read}", time, ct: token), ct);

        if (content.StatusCode == HttpStatusCode.NotFound) return new ArtefactReadResult.NotFound();
        if (content.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(content);

        return new ArtefactReadResult.Read(detail.Artefact, read, await content.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// The statuses every artefact write shares, read once.
    ///
    /// <para>404 and 403 are kept apart deliberately: the server answers 404 when the caller could
    /// not have seen the artefact at all and 403 when they can see it but do not own it, and
    /// collapsing them here would throw away the distinction a person needs to know which it
    /// was.</para>
    /// </summary>
    async Task<ArtefactWriteResult> WriteAsync(
            Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) {
        using var response = await CapacitorApiRequests.SendAsync(http, server, send, ct);

        switch (response.StatusCode) {
            case HttpStatusCode.NoContent:
                return new ArtefactWriteResult.Gone();

            case HttpStatusCode.OK or HttpStatusCode.Created: {
                var detail = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactDetailDto, ct);

                return detail is null
                    ? new ArtefactWriteResult.Refused(new("unreadable", "The server's answer could not be read.", null))
                    : new ArtefactWriteResult.Written(detail);
            }

            case HttpStatusCode.NotFound:  return new ArtefactWriteResult.NotFound();
            case HttpStatusCode.Forbidden: return new ArtefactWriteResult.NotYours();

            case HttpStatusCode.BadRequest or HttpStatusCode.Conflict: {
                var error = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactErrorDto, ct);

                return new ArtefactWriteResult.Refused(
                    error ?? new ArtefactErrorDto("refused", $"Server returned HTTP {(int)response.StatusCode}", null));
            }

            default:
                throw await CapacitorApiRequests.FailureAsync(response);
        }
    }

    static string Escape(string artefactId) => Uri.EscapeDataString(artefactId);
}
