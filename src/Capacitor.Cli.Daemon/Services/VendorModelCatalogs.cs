using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// The per-vendor model catalogs a daemon advertises. Every operation builds a new dictionary:
/// the advertised instance is published by reference swap and may be mid-serialization elsewhere.
internal static class VendorModelCatalogs {
    /// Probes the named vendors concurrently; a vendor whose probe answers null is not a key.
    internal static async Task<Dictionary<string, VendorModelOption[]>> ProbeAsync(
            IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> vendors, CancellationToken ct) {
        var wanted = new HashSet<string>(vendors, StringComparer.Ordinal);
        var probes = factories.Where(f => wanted.Contains(f.Vendor))
            .Select(async f => (f.Vendor, Models: await f.ProbeModelsAsync(ct).ConfigureAwait(false)))
            .ToArray();

        var result = new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal);
        foreach (var (vendor, models) in await Task.WhenAll(probes).ConfigureAwait(false))
            if (models is not null) result[vendor] = [.. models];
        return result;
    }

    /// Fresh answers win; a vendor absent from <paramref name="fresh"/> keeps its previous entry, so
    /// a transient probe failure never withdraws a working list.
    internal static Dictionary<string, VendorModelOption[]> Merge(
            Dictionary<string, VendorModelOption[]>? previous, Dictionary<string, VendorModelOption[]> fresh) {
        var merged = previous is null
            ? new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal)
            : new Dictionary<string, VendorModelOption[]>(previous, StringComparer.Ordinal);
        foreach (var (vendor, models) in fresh) merged[vendor] = models;
        return merged;
    }

    /// Same keys and, per key, the same ordered (Value, Label) pairs.
    internal static bool Equal(Dictionary<string, VendorModelOption[]>? a, Dictionary<string, VendorModelOption[]>? b) {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var (vendor, models) in a)
            if (!b.TryGetValue(vendor, out var other) || !models.AsSpan().SequenceEqual(other)) return false;
        return true;
    }

    internal static IReadOnlyDictionary<string, CatalogPathStat[]> FingerprintCatalogPaths(
            IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> vendors) {
        var byVendor = factories.ToDictionary(f => f.Vendor, StringComparer.Ordinal);
        return vendors.ToDictionary(
            v => v,
            v => byVendor.TryGetValue(v, out var f) ? f.CatalogFingerprintPaths.Select(CatalogPathStat.Of).ToArray() : [],
            StringComparer.Ordinal);
    }
}
