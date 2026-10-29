using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Fingerprint of an installed vendor CLI: the file that actually runs once every symlink
/// is followed, plus its size and last-write time. The path is part of it because a vendor update
/// commonly retargets a symlink at a new version directory.</summary>
public readonly record struct CliBinaryStat(string ResolvedPath, long Size, long MtimeTicks);

/// <summary>
/// Polls the CLI binaries and model-catalog files of the vendors this daemon advertises and, when
/// one changes on disk, asks the orchestrator to re-probe and re-advertise. Without it the
/// advertised version and model list are startup snapshots, and the first reviewer launch after a
/// vendor auto-update is rejected for the mismatch.
/// </summary>
internal sealed partial class VendorCliWatcher : BackgroundService {
    readonly DaemonConfig?                                             _config;
    readonly IReadOnlyDictionary<string, IHostedAgentRuntimeFactory>? _factories;
    readonly Func<AccountsSnapshotDto?>                                _accounts = static () => null;
    readonly ILogger                                                   _logger;
    readonly TimeProvider                                              _time;

    int _rebuildRequested;
    IReadOnlyList<(string AccountId, string Directory)>? _actedAccounts;

    // Seams (assigned from DI in the production ctor; overridden directly in tests).
    internal Func<string, CliBinaryStat?>   StatBinary;
    internal Func<string, CatalogPathStat>  StatCatalog = CatalogPathStat.Of;
    internal Action<string>                 Refresh;
    // A vendor's CLI path is read per tick: a desktop app's bundled CLI moves to a new directory on update.
    internal Func<string, string?>          CurrentCliPath = _ => null;
    internal IReadOnlyList<(string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)> Watched;

    readonly Dictionary<string, CliBinaryStat?>   _baselines        = new(StringComparer.Ordinal);
    readonly Dictionary<string, CatalogPathStat[]> _catalogBaselines = new(StringComparer.Ordinal);
    // The poll loop and a caller forcing a tick can overlap, and a Dictionary corrupts under two writers.
    readonly Lock                                  _gate             = new();

    /// <summary>Fingerprints recorded when the advertisement was probed. The advertisement
    /// describes the files as they were then, so a vendor that updates before this service starts
    /// must read as a change on the first tick rather than become the baseline. The catalog record is
    /// read live: a refresh records an added account's files before probing it.</summary>
    IReadOnlyDictionary<string, CliBinaryStat?>?                   _recorded;
    readonly Func<IReadOnlyDictionary<string, CatalogPathStat[]>?> _recordedCatalogs;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public VendorCliWatcher(
            DaemonConfig config, AgentOrchestrator orchestrator,
            IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> factories, AccountsPublisher accounts,
            ILogger<VendorCliWatcher> logger, TimeProvider time) {
        _config    = config;
        _time      = time;
        _factories = factories;
        _accounts  = () => accounts.Current;
        _logger    = logger;
        _recordedCatalogs = () => config.VendorCatalogBaselines;
        Refresh    = reason => orchestrator.RefreshAdvertisedCapabilities(reason);
        StatBinary = path => StatCliBinary(config.Binaries, path);
        CurrentCliPath = vendor => factories.TryGetValue(vendor, out var factory) ? factory.CliPath : null;
        Watched    = [];
        accounts.Changed += OnAccountsChanged;
    }

    VendorCliWatcher(IReadOnlyList<(string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)> watched,
            Action<string> refresh, Func<string, CliBinaryStat?> stat, TimeProvider time,
            IReadOnlyDictionary<string, CliBinaryStat?>? baselines,
            Func<string, CatalogPathStat>? statCatalog,
            IReadOnlyDictionary<string, CatalogPathStat[]>? catalogBaselines) {
        _logger           = NullLogger.Instance;
        _time             = time;
        _recorded         = baselines;
        _recordedCatalogs = () => catalogBaselines;
        Watched           = watched;
        Refresh           = refresh;
        StatBinary        = stat;
        StatCatalog       = statCatalog ?? CatalogPathStat.Of;
    }

    internal static VendorCliWatcher ForTest(
            IReadOnlyList<(string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)> watched,
            Action<string> refresh, Func<string, CliBinaryStat?> stat, TimeProvider time,
            IReadOnlyDictionary<string, CliBinaryStat?>? baselines = null,
            Func<string, CatalogPathStat>? statCatalog = null,
            IReadOnlyDictionary<string, CatalogPathStat[]>? catalogBaselines = null) =>
        new(watched, refresh, stat, time, baselines, statCatalog, catalogBaselines);

    /// <summary>Unattended vendors, plus every vendor that publishes a model catalog: one with
    /// catalog files to fingerprint, or one that answered the startup probe. Each account a refresh
    /// probes on its own is an entry of its own, keyed by <see cref="VendorModelCatalogs.AccountKey"/>,
    /// with no binary.</summary>
    internal static IReadOnlyList<(string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)> WatchSet(
            DaemonConfig config, IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> factories,
            AccountsSnapshotDto? accounts = null) {
        var vendors = new HashSet<string>(config.UnattendedVendors ?? [], StringComparer.Ordinal);
        foreach (var f in factories.Values)
            if (f.CatalogFingerprintPaths.Count > 0 || (config.VendorModels?.ContainsKey(f.Vendor) ?? false))
                vendors.Add(f.Vendor);

        var perAccount = VendorModelCatalogs.AccountFactory(factories.Values, config.SupportedVendors ?? []) is { } accountFactory
            ? VendorModelCatalogs.AccountsIn(accounts).Select(a =>
                (Vendor: VendorModelCatalogs.AccountKey(a.AccountId), CliPath: "", CatalogPaths: accountFactory.CatalogFingerprintPathsFor(a.Directory)))
            : [];

        return vendors.Where(factories.ContainsKey)
            .Select(v => (Vendor: v, factories[v].CliPath, CatalogPaths: factories[v].CatalogFingerprintPaths))
            .Concat(perAccount)
            .Where(w => !string.IsNullOrEmpty(w.CliPath) || w.CatalogPaths.Count > 0)
            .OrderBy(w => w.Vendor, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Gives each watched entry without a baseline its first one, the recorded fingerprint when
    /// there is one, and forgets entries no longer watched. An entry that already has one keeps it, so
    /// a rebuild cannot absorb a change the next tick has yet to see; the recorded binary fingerprints
    /// serve only the first call.</summary>
    internal void PrimeBaselines() {
        lock (_gate) PrimeBaselinesUnlocked();
    }

    void PrimeBaselinesUnlocked() {
        var watched          = new HashSet<string>(StringComparer.Ordinal);
        var recordedCatalogs = _recordedCatalogs();
        foreach (var (vendor, cliPath, catalogPaths) in Watched) {
            watched.Add(vendor);
            if (!_baselines.ContainsKey(vendor))
                _baselines[vendor] = _recorded is { } recorded && recorded.TryGetValue(vendor, out var baseline)
                    ? baseline
                    : StatCli(cliPath);
            if (!_catalogBaselines.ContainsKey(vendor))
                _catalogBaselines[vendor] = recordedCatalogs is not null && recordedCatalogs.TryGetValue(vendor, out var catalog)
                    ? catalog
                    : StatCatalogs(catalogPaths);
        }

        foreach (var gone in _baselines.Keys.Where(k => !watched.Contains(k)).ToArray()) _baselines.Remove(gone);
        foreach (var gone in _catalogBaselines.Keys.Where(k => !watched.Contains(k)).ToArray()) _catalogBaselines.Remove(gone);
        _recorded = null;
    }

    /// <summary>The publisher's handler, which must not block: the watch set is rebuilt by the next tick,
    /// and the refresh is a trigger. A snapshot announces reading changes too, and those leave the watched
    /// accounts as they were, so only a change to the accounts themselves re-probes.</summary>
    internal void OnAccountsChanged(AccountsSnapshotDto snapshot) {
        var accounts = VendorModelCatalogs.AccountsIn(snapshot);
        if (Interlocked.Exchange(ref _actedAccounts, accounts) is { } acted && acted.SequenceEqual(accounts)) return;

        Interlocked.Exchange(ref _rebuildRequested, 1);
        Refresh("accounts changed");
    }

    /// <summary>One poll iteration (timer-driven; also the unit-test entry point). All vendors
    /// that changed since the last tick share one refresh, since a refresh re-probes them all.</summary>
    internal void Tick() {
        string? reason;
        lock (_gate) reason = TickUnlocked();
        if (reason is null) return;

        LogChanged(_logger, reason);
        Refresh(reason);
    }

    // The refresh is raised outside the gate: it is a trigger into the orchestrator, not state of this class.
    string? TickUnlocked() {
        if (Interlocked.Exchange(ref _rebuildRequested, 0) == 1 && _config is not null && _factories is not null) {
            Watched = WatchSet(_config, _factories, _accounts());
            PrimeBaselinesUnlocked();
        }

        List<string>? changed = null;
        foreach (var (vendor, cliPath, catalogPaths) in Watched) {
            var current        = StatCli(CurrentCliPath(vendor) ?? cliPath);
            var binaryChanged  = Changed(_baselines.GetValueOrDefault(vendor), current);
            var catalog        = StatCatalogs(catalogPaths);
            var catalogChanged = !catalog.AsSpan().SequenceEqual(_catalogBaselines.GetValueOrDefault(vendor) ?? []);
            if (!binaryChanged && !catalogChanged) continue;

            if (binaryChanged) _baselines[vendor] = current;
            _catalogBaselines[vendor] = catalog;
            (changed ??= []).Add(vendor);
        }

        return changed is null ? null : $"{string.Join(", ", changed)} CLI binary or catalog files changed on disk";
    }

    CliBinaryStat? StatCli(string cliPath) => string.IsNullOrEmpty(cliPath) ? null : StatBinary(cliPath);

    CatalogPathStat[] StatCatalogs(IReadOnlyList<string> paths) => [.. paths.Select(StatCatalog)];

    /// <summary>A null current fingerprint is a transient (the binary is mid-replacement), never a
    /// change: acting on it would re-probe a file that is not there. A missing catalog file is not
    /// transient, since deleting one does change the catalog.</summary>
    internal static bool Changed(CliBinaryStat? baseline, CliBinaryStat? current) =>
        current is { } c && baseline != c;

    /// <summary>Resolves a bare command on the probe's search path, follows the symlink chain to
    /// the file that runs and stats it. Null when the binary cannot be found right now.</summary>
    internal static CliBinaryStat? StatCliBinary(BinaryProbe binaries, string cliPath) {
        try {
            if (binaries.Resolve(cliPath) is not { } resolved) return null;
            var info   = new FileInfo(resolved);
            var target = info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info;
            return target.Exists ? new CliBinaryStat(target.FullName, target.Length, target.LastWriteTimeUtc.Ticks) : null;
        } catch {
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        // Runs off the caller's thread, so a tick may already have rebuilt the set: build and assign
        // under the gate, from the accounts as they are now, or a stale set lands over the rebuilt one.
        lock (_gate) {
            if (_config is not null && _factories is not null) {
                _recorded = _config.UnattendedVendorBaselines;
                Watched   = WatchSet(_config, _factories, _accounts());
            }
            PrimeBaselinesUnlocked();
        }

        using var timer = new PeriodicTimer(PollInterval, _time);
        try {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-probing advertised vendor CLIs: {Reason}")]
    static partial void LogChanged(ILogger logger, string reason);
}
