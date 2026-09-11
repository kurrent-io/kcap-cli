using System.Collections.Frozen;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// The per-vendor model catalog the server offers for launches, fetched once from
/// GET api/agents/model-options — the same source the web UI's model dropdown reads. A failed or
/// signed-out fetch leaves the last snapshot (empty until the first success), and the launcher
/// falls back to its curated per-vendor list, so a launch is never blocked on this.
public sealed class ServerVendorModelCatalog : IDisposable {
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> Empty =
        FrozenDictionary<string, IReadOnlyList<ModelChoice>>.Empty;

    readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>?>> _fetch;
    readonly BehaviorSubject<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>> _catalog = new(Empty);

    public ServerVendorModelCatalog(
            Func<CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>?>> fetch) =>
        _fetch = fetch;

    public IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>> Catalog => _catalog;

    /// Fetches once and publishes on success; a null result (offline / signed out / any error)
    /// leaves the current snapshot untouched. Never throws — the launcher's curated fallback covers
    /// a miss.
    public async Task LoadAsync(CancellationToken ct = default) {
        try {
            if (await _fetch(ct).ConfigureAwait(false) is { } catalog) _catalog.OnNext(catalog);
        } catch (Exception) { }
    }

    public void Dispose() => _catalog.Dispose();

    /// Builds the HTTP fetch over an authenticated client, mapping the server's {value,label} to
    /// ModelChoice. Returns null (no change) when signed out, offline, or on any non-success.
    public static Func<CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>?>>
            HttpFetch(ICapacitorHttpClient? http, ProfileContext? profiles) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return null;
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return null;
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.ModelOptions}";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                var raw = await response.Content.ReadFromJsonAsync(
                    RemoteModelsJsonContext.Default.DictionaryStringVendorModelOptionDtoArray, ct).ConfigureAwait(false);
                return raw is null ? null : Map(raw);
            }
        } catch (Exception) { return null; }
    };

    static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> Map(Dictionary<string, VendorModelOptionDto[]> raw) =>
        raw.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ModelChoice>)[.. kv.Value.Select(o => new ModelChoice(o.Value, o.Label))],
            StringComparer.OrdinalIgnoreCase);
}
