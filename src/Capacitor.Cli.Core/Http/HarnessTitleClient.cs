using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Http;

/// <summary>Posts a title read from a harness's own store. The server answers a session it cannot see yet with a
/// coded 404; a bare 404 means an older server without the route, which still accepts <c>/hooks/set-title</c>.</summary>
public static class HarnessTitleClient {
    public static async Task<HarnessTitleOutcome> PostAsync(HttpClient client, TimeProvider time, string baseUrl, string sessionId, HarnessTitlePost post, CancellationToken ct) {
        var hook = new HarnessTitleHook(sessionId, post.Title, post.Kind == HarnessTitleKind.Rename ? "rename" : "auto", post.ChangedAt?.ToUniversalTime());

        try {
            using var content = new StringContent(JsonSerializer.Serialize(hook, CapacitorJsonContext.Default.HarnessTitleHook), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/harness-title", content, time, ct: ct);

            if (resp.IsSuccessStatusCode) return HarnessTitleOutcome.Posted;

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return ErrorCode(await resp.Content.ReadAsStringAsync(ct)) == "session_not_found"
                    ? HarnessTitleOutcome.SessionNotFound
                    : HarnessTitleOutcome.RouteMissing;

            return (int)resp.StatusCode < 500 ? HarnessTitleOutcome.Refused : HarnessTitleOutcome.Failed;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return HarnessTitleOutcome.Failed;
        }
    }

    public static async Task<HarnessTitleOutcome> PostOrFallBackAsync(HttpClient client, TimeProvider time, string baseUrl, string sessionId, HarnessTitlePost post, CancellationToken ct) {
        var outcome = await PostAsync(client, time, baseUrl, sessionId, post, ct);

        if (outcome != HarnessTitleOutcome.RouteMissing) return outcome;

        var payload = new JsonObject { ["session_id"] = sessionId, ["title"] = post.Title };

        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/set-title", content, time, ct: ct);

            return resp.IsSuccessStatusCode ? HarnessTitleOutcome.Posted : HarnessTitleOutcome.Refused;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return HarnessTitleOutcome.Failed;
        }
    }

    static string? ErrorCode(string body) {
        try {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Str("error") : null;
        } catch (JsonException) {
            return null;
        }
    }
}
