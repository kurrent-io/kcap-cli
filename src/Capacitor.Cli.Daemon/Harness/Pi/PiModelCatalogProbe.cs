using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// Asks an installed `pi` for the models it can launch here, over its RPC mode, and maps them to
/// `provider/id` values. The answer is Pi's own auth-filtered list; the daemon never reads Pi's
/// auth or models files.
internal static class PiModelCatalogProbe {
    internal const string Command = "get_available_models";

    internal static PiModelCatalogParse Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return PiModelCatalogParse.NotResponse;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return PiModelCatalogParse.NotResponse; }
        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Str("type") != "response" || root.Str("command") != Command)
                return PiModelCatalogParse.NotResponse;
            // An invalid envelope is null, never empty: an empty catalog would suppress every fallback.
            if (root.Bool("success") != true || root.Obj("data")?.Arr("models") is not { } models)
                return new(true, null);

            var list = new List<VendorModelOption>();
            foreach (var m in models.EnumerateArray()) {
                var id = m.Str("id");
                var provider = m.Str("provider");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(provider)) continue;
                var name = m.Str("name") is { Length: > 0 } n ? n : id;
                list.Add(new($"{provider}/{id}", $"{name} · {provider}"));
            }
            return new(true, list);
        }
    }
}
