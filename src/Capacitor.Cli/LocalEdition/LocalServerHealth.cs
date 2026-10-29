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

        return new ServerHealth.Answered(status == HttpStatusCode.OK, Stores(body));
    }

    static IReadOnlyDictionary<string, StoreHealth> Stores(string body) {
        try {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.Obj("results") is not { } results) return FrozenDictionary<string, StoreHealth>.Empty;

            var stores = new Dictionary<string, StoreHealth>(StringComparer.Ordinal);
            foreach (var entry in results.EnumerateObject()) {
                if (entry.Value.Str("status") is { } s) stores[entry.Name] = new(s, entry.Value.Str("description"));
            }

            return stores;
        } catch (JsonException) {
            return FrozenDictionary<string, StoreHealth>.Empty;
        }
    }
}
