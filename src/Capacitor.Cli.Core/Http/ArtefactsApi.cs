using System.Net;
using System.Net.Http.Json;

namespace Capacitor.Cli.Core.Http;

internal sealed class ArtefactsApi(ICapacitorHttpClient http, CapacitorServer server) : IArtefactsApi {
    public async Task<ArtefactWriteResult> PublishAsync(PublishArtefactBody body, CancellationToken ct = default) {
        using var content = JsonContent.Create(body, CapacitorJsonContext.Default.PublishArtefactBody);

        return await WriteAsync((c, token) => c.PostWithRetryAsync($"{server.Url}/api/artefacts", content, ct: token), ct);
    }

    public async Task<ArtefactWriteResult> PublishVersionAsync(string artefactId, string html, CancellationToken ct = default) {
        using var content = JsonContent.Create(new PublishArtefactVersionBody(html),
                                               CapacitorJsonContext.Default.PublishArtefactVersionBody);

        return await WriteAsync(
            (c, token) => c.PostWithRetryAsync($"{server.Url}/api/artefacts/{Escape(artefactId)}/versions", content, ct: token), ct);
    }

    public async Task<ArtefactWriteResult> SetVisibilityAsync(string artefactId, string visibility,
                                                              List<ArtefactGrantDto>? grants, CancellationToken ct = default) {
        using var content = JsonContent.Create(new SetArtefactVisibilityBody(visibility, grants),
                                               CapacitorJsonContext.Default.SetArtefactVisibilityBody);

        return await WriteAsync(
            (c, token) => c.PutWithRetryAsync($"{server.Url}/api/artefacts/{Escape(artefactId)}/visibility", content, ct: token), ct);
    }

    public async Task<ArtefactWriteResult> DeleteAsync(string artefactId, CancellationToken ct = default) =>
        await WriteAsync((c, token) => c.DeleteWithRetryAsync($"{server.Url}/api/artefacts/{Escape(artefactId)}", ct: token), ct);

    public async Task<List<ArtefactDto>> ListAsync(CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.GetWithRetryAsync($"{server.Url}/api/artefacts", ct: token), ct);

        if (response.StatusCode != HttpStatusCode.OK) throw await CapacitorApiRequests.FailureAsync(response);

        var listed = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactListDto, ct);

        return listed?.Artefacts ?? [];
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
