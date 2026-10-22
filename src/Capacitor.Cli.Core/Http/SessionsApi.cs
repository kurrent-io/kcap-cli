using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Http;

internal sealed class SessionsApi(ICapacitorHttpClient http, CapacitorServer server, TimeProvider time) : ISessionsApi {
    public async Task<DeleteSessionResponse> DeleteSessionAsync(string sessionId, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.DeleteWithRetryAsync($"{server.Url}/api/sessions/{sessionId}", time, ct: token), ct);

        if (response.IsSuccessStatusCode) return new DeleteSessionResponse.Deleted();
        if (response.StatusCode == HttpStatusCode.NotFound) return new DeleteSessionResponse.NotFound();

        throw await FailureAsync(response);
    }

    public async Task HideSessionAsync(string sessionId, CancellationToken ct = default) {
        using var body = Json(new JsonObject { ["visibility"] = "none" });
        using var response = await SendAsync(
                (c, token) => c.PutWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/visibility", body, time, ct: token), ct);

        if (!response.IsSuccessStatusCode) throw await FailureAsync(response);
    }

    public async Task<SetTitleResult> SetSessionTitleAsync(string sessionId, string title, bool rename, CancellationToken ct = default) {
        using var body = Json(new JsonObject { ["session_id"] = sessionId, ["title"] = title, ["rename"] = rename });

        for (var attempt = 1;; attempt++) {
            using var response = await SendAsync((c, token) => c.PostWithRetryAsync($"{server.Url}/hooks/agent-title", body, time, ct: token), ct);

            if (response.IsSuccessStatusCode) {
                var answer = await ReadObjectAsync(response, ct);

                return answer?.Bool("applied") switch {
                    true  => new SetTitleResult.Applied(),
                    false => new SetTitleResult.NotApplied(answer.Value.Str("title")),
                    null  => throw new CapacitorApiException((int)response.StatusCode, "Server answered the title request with an unreadable response")
                };
            }

            if (response.StatusCode != HttpStatusCode.NotFound) throw await FailureAsync(response);

            // Only an uncoded 404 means a server without the route; another coded 404 is a refusal.
            switch ((await ReadObjectAsync(response, ct))?.Str("error")) {
                case null:                    return await SetTitleOnOlderServerAsync(sessionId, title, ct);
                case not "session_not_found": throw await FailureAsync(response);
            }

            // A session the first prompt titles can still be on its way into the read model the ownership check reads.
            if (attempt == SessionNotFoundAttempts) return new SetTitleResult.SessionNotFound();

            await Task.Delay(SessionNotFoundRetryDelay, time, ct);
        }
    }

    const int SessionNotFoundAttempts = 3;

    static readonly TimeSpan SessionNotFoundRetryDelay = TimeSpan.FromSeconds(1);

    async Task<SetTitleResult> SetTitleOnOlderServerAsync(string sessionId, string title, CancellationToken ct) {
        using var body = Json(new JsonObject { ["session_id"] = sessionId, ["title"] = title });
        using var response = await SendAsync((c, token) => c.PostWithRetryAsync($"{server.Url}/hooks/set-title", body, time, ct: token), ct);

        if (!response.IsSuccessStatusCode) throw await FailureAsync(response);

        return new SetTitleResult.Unconfirmed();
    }

    static async Task<JsonElement?> ReadObjectAsync(HttpResponseMessage response, CancellationToken ct) {
        try {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            return doc.RootElement.IsObject ? doc.RootElement.Clone() : null;
        } catch (JsonException) {
            return null;
        }
    }

    public async Task<ErrorsResult> GetErrorsAsync(string sessionId, bool chain, CancellationToken ct = default) {
        var query = chain ? "?chain=true" : "";
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/errors{query}", time, ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var errors = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListErrorEntry, ct);
            return new ErrorsResult.Found(errors ?? []);
        }
        if (response.StatusCode == HttpStatusCode.NotFound) return new ErrorsResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<RecapResult> GetRecapAsync(string sessionId, bool chain, CancellationToken ct = default) {
        var query = chain ? "?chain=true" : "";
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/recap{query}", time, ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var entries = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListRecapEntry, ct);
            return new RecapResult.Found(entries ?? []);
        }
        if (response.StatusCode == HttpStatusCode.NotFound) return new RecapResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<TurnsResult> GetTurnsAsync(string sessionId, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/turns", time, ct: token), ct);

        if (response.IsSuccessStatusCode) return new TurnsResult.Found(await response.Content.ReadAsStringAsync(ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return new TurnsResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<TurnDetailResult> GetTurnAsync(string sessionId, int turnIndex, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/turns/{turnIndex}", time, ct: token), ct);

        if (response.IsSuccessStatusCode) return new TurnDetailResult.Found(await response.Content.ReadAsStringAsync(ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return new TurnDetailResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<PlanArtifactsResult> GetPlanArtifactsAsync(string sessionId, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/plan-artifacts?chain=true", time, ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var dto = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.PlanArtifactsResponseDto, ct);
            return new PlanArtifactsResult.Found(dto);
        }
        if (response.StatusCode == HttpStatusCode.NotFound) return new PlanArtifactsResult.NotFound();

        throw await FailureAsync(response);
    }

    static StringContent Json(JsonObject payload) => new(payload.ToJsonString(), Encoding.UTF8, "application/json");

    Task<HttpResponseMessage> SendAsync(Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) =>
        CapacitorApiRequests.SendAsync(http, server, send, ct);

    static Task<CapacitorApiException> FailureAsync(HttpResponseMessage response) =>
        CapacitorApiRequests.FailureAsync(response);
}
