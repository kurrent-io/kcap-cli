using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

static class LocalServerHealth {
    /// <summary>One request, never a wait, asked and classified as the desktop app asks it. Throws only
    /// <see cref="OperationCanceledException"/> from <paramref name="ct"/>.</summary>
    public static async Task<ServerHealth> ReadAsync(HttpClient http, LocalServerUnit unit, TimeProvider time, CancellationToken ct) =>
        await LocalPortProbe.ReadAsync(http, unit.Port, time, ct) switch {
            LocalPortAnswer.Answered(var status, var body) => Parse(status, body),
            LocalPortAnswer.Foreign(var what)              => new ServerHealth.Foreign(what),
            LocalPortAnswer.Silent(var reason)             => new ServerHealth.Unreachable(reason),
            _                                              => new ServerHealth.Unreachable("connection refused"),
        };

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
