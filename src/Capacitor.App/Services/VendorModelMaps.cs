using Capacitor.Cli.Core;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A daemon's advertised model catalog, from either wire, as the launcher's picker shape. Null
/// stays null: it means an older daemon that advertises no catalog, not an empty one.
internal static class VendorModelMaps {
    public static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? FromStatus(Dictionary<string, VendorModelOption[]>? wire) =>
        wire?.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ModelChoice>) [.. kv.Value.Select(o => new ModelChoice(o.Value, o.Label))],
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? FromRegistry(Dictionary<string, VendorModelOptionDto[]>? wire) =>
        wire?.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ModelChoice>) [.. kv.Value.Select(o => new ModelChoice(o.Value, o.Label))],
            StringComparer.OrdinalIgnoreCase);
}
