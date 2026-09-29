using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Http;

/// <summary>Posts a title read from a harness's own store. The server answers a session it cannot see yet with a
/// coded 404; a bare 404 means an older server without the route, which still accepts <c>/hooks/set-title</c>.</summary>
public static class HarnessTitleClient {
    // The server normalises whitespace before clamping to 200, so a harness title is sent whole: cutting it here
    // would make one rename arrive as a different value from each sender. The larger bound is a safety cap only.
    // An older server stores whatever set-title sends, and 120 is what every other set-title caller sends.
    const int HarnessTitleMax = 4096;
    const int SetTitleMax     = 120;

    static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    /// <param name="timeout">The whole call's budget; 30 seconds when null.</param>
    /// <param name="retryStatuses">Retry a 408, 429 or server fault within the budget. For a caller with no later
    /// attempt of its own.</param>
    public static async Task<HarnessTitleOutcome> PostAsync(
            HttpClient client, TimeProvider time, string baseUrl, string sessionId, HarnessTitlePost post, CancellationToken ct,
            TimeSpan? timeout = null, bool retryStatuses = false
        ) {
        var hook = new HarnessTitleHook(sessionId, Clamp(post.Title, HarnessTitleMax), post.Kind == HarnessTitleKind.Rename ? "rename" : "auto",
            post.ChangedAt?.ToUniversalTime());

        try {
            using var content = new StringContent(JsonSerializer.Serialize(hook, CapacitorJsonContext.Default.HarnessTitleHook), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/harness-title", content, time, timeout ?? DefaultBudget, ct, retryStatuses);

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

    /// <param name="timeout">Shared by the harness-title attempt and the set-title fallback; 30 seconds when null.</param>
    /// <param name="retryStatuses">As for <see cref="PostAsync"/>, on both requests.</param>
    public static async Task<HarnessTitleOutcome> PostOrFallBackAsync(
            HttpClient client, TimeProvider time, string baseUrl, string sessionId, HarnessTitlePost post, CancellationToken ct,
            TimeSpan? timeout = null, bool retryStatuses = false
        ) {
        var budget  = timeout ?? DefaultBudget;
        var started = time.GetTimestamp();
        var outcome = await PostAsync(client, time, baseUrl, sessionId, post, ct, budget, retryStatuses);

        if (outcome != HarnessTitleOutcome.RouteMissing) return outcome;

        var remaining = budget - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return HarnessTitleOutcome.Failed;

        var payload = new JsonObject { ["session_id"] = sessionId, ["title"] = Clamp(post.Title, SetTitleMax) };

        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/set-title", content, time, remaining, ct, retryStatuses);

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
