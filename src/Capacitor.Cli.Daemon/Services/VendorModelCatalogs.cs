using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

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

    /// The one vendor whose catalog depends on the account: a ChatGPT-plan and an API account can
    /// launch different models.
    internal static readonly string AccountVendor = HarnessId.Codex.VendorId;

    static readonly string AccountKeyPrefix = AccountKey("");

    /// The runtime factory that probes per account, when its vendor is advertised.
    internal static IHostedAgentRuntimeFactory? AccountFactory(
            IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> supported) =>
        supported.Contains(AccountVendor, StringComparer.Ordinal)
            ? factories.FirstOrDefault(f => string.Equals(f.Vendor, AccountVendor, StringComparison.Ordinal))
            : null;

    /// The registered accounts of <see cref="AccountVendor"/> in <paramref name="snapshot"/>.
    internal static IReadOnlyList<(string AccountId, string Directory)> AccountsIn(AccountsSnapshotDto? snapshot) =>
        snapshot is null
            ? []
            : [.. snapshot.Accounts.Where(a => string.Equals(a.Vendor, AccountVendor, StringComparison.Ordinal)).Select(a => (a.Id, a.Directory))];

    /// The key an account's catalog files are fingerprinted and watched under.
    internal static string AccountKey(string accountId) => $"{AccountVendor} account {accountId}";

    /// Probes each account concurrently under its own directory; an account whose probe answers null
    /// is not a key.
    internal static async Task<Dictionary<string, VendorModelOption[]>> ProbeByAccountAsync(
            IHostedAgentRuntimeFactory factory, IReadOnlyList<(string AccountId, string Directory)> accounts, CancellationToken ct) {
        var probes = accounts
            .Select(async a => (a.AccountId, Models: await factory.ProbeModelsAsync(a.Directory, ct).ConfigureAwait(false)))
            .ToArray();

        var result = new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal);
        foreach (var (id, models) in await Task.WhenAll(probes).ConfigureAwait(false))
            if (models is not null) result[id] = [.. models];
        return result;
    }

    /// Like <see cref="Merge"/>, except that an account not in <paramref name="live"/> is dropped
    /// from both sides: a probe that outlived its account publishes nothing. Null stays null while
    /// there is nothing to hold.
    internal static Dictionary<string, VendorModelOption[]>? MergeByAccount(
            Dictionary<string, VendorModelOption[]>? previous, Dictionary<string, VendorModelOption[]> fresh,
            IReadOnlySet<string> live) {
        var merged = new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal);
        foreach (var (id, models) in previous ?? []) if (live.Contains(id)) merged[id] = models;
        foreach (var (id, models) in fresh) if (live.Contains(id)) merged[id] = models;
        return previous is null && merged.Count == 0 ? null : merged;
    }

    /// Same keys and, per key, the same ordered (Value, Label) pairs.
    internal static bool Equal(Dictionary<string, VendorModelOption[]>? a, Dictionary<string, VendorModelOption[]>? b) {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var (vendor, models) in a)
            if (!b.TryGetValue(vendor, out var other) || !models.AsSpan().SequenceEqual(other)) return false;
        return true;
    }

    /// Keyed by vendor, plus <see cref="AccountKey"/> for each of <paramref name="accounts"/>.
    internal static IReadOnlyDictionary<string, CatalogPathStat[]> FingerprintCatalogPaths(
            IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> vendors,
            IHostedAgentRuntimeFactory? accountFactory = null, IReadOnlyList<(string AccountId, string Directory)>? accounts = null) {
        var byVendor = factories.ToDictionary(f => f.Vendor, StringComparer.Ordinal);
        var result = vendors.ToDictionary(
            v => v,
            v => byVendor.TryGetValue(v, out var f) ? f.CatalogFingerprintPaths.Select(CatalogPathStat.Of).ToArray() : [],
            StringComparer.Ordinal);
        return accountFactory is null ? result : WithAccountBaselines(result, accountFactory, accounts ?? []);
    }

    /// <paramref name="baselines"/> with its account entries replaced by fingerprints of
    /// <paramref name="accounts"/>' catalog files taken now. A new dictionary, since the watcher may be
    /// reading the old one.
    internal static IReadOnlyDictionary<string, CatalogPathStat[]> WithAccountBaselines(
            IReadOnlyDictionary<string, CatalogPathStat[]>? baselines, IHostedAgentRuntimeFactory accountFactory,
            IReadOnlyList<(string AccountId, string Directory)> accounts) {
        var result = new Dictionary<string, CatalogPathStat[]>(StringComparer.Ordinal);
        foreach (var (key, stats) in baselines ?? FrozenDictionary<string, CatalogPathStat[]>.Empty)
            if (!key.StartsWith(AccountKeyPrefix, StringComparison.Ordinal)) result[key] = stats;
        foreach (var (id, directory) in accounts)
            result[AccountKey(id)] = [.. accountFactory.CatalogFingerprintPathsFor(directory).Select(CatalogPathStat.Of)];
        return result;
    }
}
