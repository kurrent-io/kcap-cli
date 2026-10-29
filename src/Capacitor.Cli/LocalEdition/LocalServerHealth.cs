using System.Collections.Frozen;
using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.LocalEdition;

static class LocalServerHealth {
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>One request, never a wait. Throws only <see cref="OperationCanceledException"/> from
    /// <paramref name="ct"/>.</summary>
    public static async Task<ServerHealth> ReadAsync(HttpClient http, LocalServerUnit unit, TimeProvider time, CancellationToken ct) {
        using var attempt = new CancellationTokenSource(RequestTimeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Token);

        string body;
        HttpStatusCode status;

        try {
            using var response = await http.GetAsync(new Uri($"{unit.BaseUrl}/healthz/ready"), linked.Token);
            status = response.StatusCode;
            body   = await response.Content.ReadAsStringAsync(linked.Token);
        } catch (HttpRequestException ex) {
            return new ServerHealth.Unreachable(ex.Message);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return new ServerHealth.Unreachable($"no answer within {RequestTimeout.TotalSeconds:0} seconds");
        }

        return Parse(status, body);
    }

    static ServerHealth.Answered Parse(HttpStatusCode status, string body) {
        var ready = status == HttpStatusCode.OK;
        try {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(ready, FrozenDictionary<string, StoreHealth>.Empty);
            var pending = doc.RootElement.TryGetProperty("updatePending", out var update) && update.ValueKind == JsonValueKind.True;
            if (doc.RootElement.Obj("results") is not { } results)
                return new(ready, FrozenDictionary<string, StoreHealth>.Empty, pending);

            var stores = new Dictionary<string, StoreHealth>(StringComparer.Ordinal);
            foreach (var entry in results.EnumerateObject()) {
                if (entry.Value.Str("status") is { } s) stores[entry.Name] = new(s, entry.Value.Str("description"));
            }

            return new(ready, stores, pending);
        } catch (JsonException) {
            return new(ready, FrozenDictionary<string, StoreHealth>.Empty);
        }
    }
}
