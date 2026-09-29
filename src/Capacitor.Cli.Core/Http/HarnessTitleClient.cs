using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Http;

/// <summary>Posts a title read from a harness's own store. The server answers a session it cannot see yet with a
/// coded 404; a bare 404 means an older server without the route, which still accepts <c>/hooks/set-title</c>.</summary>
public static class HarnessTitleClient {
    // The server clamps a harness title to 200; an older server stores whatever set-title sends, and 120 is the
    // length every other set-title caller sends.
    const int HarnessTitleMax = 200;
    const int SetTitleMax     = 120;

    /// <param name="timeout">The whole call's budget; the retry helper's default when null.</param>
    public static async Task<HarnessTitleOutcome> PostAsync(
            HttpClient client, TimeProvider time, string baseUrl, string sessionId, HarnessTitlePost post, CancellationToken ct,
            TimeSpan? timeout = null
        ) {
        var hook = new HarnessTitleHook(sessionId, Clamp(post.Title, HarnessTitleMax), post.Kind == HarnessTitleKind.Rename ? "rename" : "auto",
            post.ChangedAt?.ToUniversalTime());

        try {
            using var content = new StringContent(JsonSerializer.Serialize(hook, CapacitorJsonContext.Default.HarnessTitleHook), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/harness-title", content, time, timeout, ct);

            if (resp.IsSuccessStatusCode) return HarnessTitleOutcome.Posted;

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return ErrorCode(await resp.Content.ReadAsStringAsync(ct)) == "session_not_found"
                    ? HarnessTitleOutcome.SessionNotFound
                    : HarnessTitleOutcome.RouteMissing;

            return IsRefusal(resp.StatusCode) ? HarnessTitleOutcome.Refused : HarnessTitleOutcome.Failed;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return HarnessTitleOutcome.Failed;
        }
    }

    /// <param name="timeout">Shared by the harness-title attempt and the set-title fallback.</param>
    public static async Task<HarnessTitleOutcome> PostOrFallBackAsync(
            HttpClient client, TimeProvider time, string baseUrl, string sessionId, HarnessTitlePost post, CancellationToken ct,
            TimeSpan? timeout = null
        ) {
        var started = time.GetTimestamp();
        var outcome = await PostAsync(client, time, baseUrl, sessionId, post, ct, timeout);

        if (outcome != HarnessTitleOutcome.RouteMissing) return outcome;

        var remaining = timeout - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return HarnessTitleOutcome.Failed;

        var payload = new JsonObject { ["session_id"] = sessionId, ["title"] = Clamp(post.Title, SetTitleMax) };

        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/set-title", content, time, remaining, ct);

            if (resp.IsSuccessStatusCode) return HarnessTitleOutcome.Posted;

            return resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden ? HarnessTitleOutcome.Refused : HarnessTitleOutcome.Failed;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return HarnessTitleOutcome.Failed;
        }
    }

    // A verdict on the request itself. An auth lapse (401), a timeout (408), a rate limit (429) and any server
    // fault are transient, so they must stay retryable.
    static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity;

    static string Clamp(string title, int max) =>
        title.Length <= max ? title : title[..(char.IsHighSurrogate(title[max - 1]) ? max - 1 : max)];

    static string? ErrorCode(string body) {
        try {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.Str("error");
        } catch (JsonException) {
            return null;
        }
    }
}
