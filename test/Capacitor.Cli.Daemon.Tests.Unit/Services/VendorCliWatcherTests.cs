using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The watcher that notices an advertised vendor's CLI binary changing under a running daemon and
/// asks for the advertisement to be refreshed, so a vendor auto-update does not cost the next
/// reviewer launch.
/// </summary>
public class VendorCliWatcherTests {
    /// <summary>Every path here is rooted, which resolves without a search path.</summary>
    static BinaryProbe Binaries => TestBinaries.None;

    static readonly CliBinaryStat Old = new("/versions/2.1.259/claude", 100, 1);
    static readonly CliBinaryStat New = new("/versions/2.1.263/claude", 100, 2);

    const string Auth = "/home/u/.pi/agent/auth.json";

    sealed class Harness {
        public readonly List<string>                          Refreshes    = [];
        public readonly Dictionary<string, CliBinaryStat?>    Stats        = new(StringComparer.Ordinal);
        public readonly Dictionary<string, CatalogPathStat>   CatalogStats = new(StringComparer.Ordinal);
        public readonly VendorCliWatcher                      Watcher;

        public Harness(params (string Vendor, string CliPath)[] watched)
            : this(null, [.. watched.Select(w => (w.Vendor, w.CliPath, (IReadOnlyList<string>) []))]) { }

        public Harness(IReadOnlyDictionary<string, CatalogPathStat[]>? catalogBaselines,
                params (string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)[] watched) {
            Watcher = VendorCliWatcher.ForTest(
                watched,
                refresh: Refreshes.Add,
                stat: path => Stats.GetValueOrDefault(path),
                time: TimeProvider.System,
                statCatalog: path => CatalogStats.TryGetValue(path, out var stat) ? stat : new(path, false, 0, 0),
                catalogBaselines: catalogBaselines);
        }
    }

    static Harness PiWithAuth(IReadOnlyDictionary<string, CatalogPathStat[]>? catalogBaselines = null) {
        var h = new Harness(catalogBaselines, ("pi", "/bin/pi", [Auth]));
        h.Stats["/bin/pi"] = Old;
        return h;
    }

    [Test]
    public async Task An_unchanged_binary_requests_nothing() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Watcher.Tick();

        await Assert.That(h.Refreshes).IsEmpty();
    }

    [Test]
    public async Task A_changed_binary_requests_one_refresh_naming_the_vendor() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = New;
        h.Watcher.Tick();
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("claude");
    }

    // A symlink flip to a new version directory is the common shape of a vendor update, and the
    // new build can have the same size and a newer mtime that a coarse clock still rounds equal.
    [Test]
    public async Task A_symlink_retarget_alone_counts_as_a_change() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = Old with { ResolvedPath = "/versions/2.1.263/claude" };
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_transient_stat_failure_neither_refreshes_nor_moves_the_baseline() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = null;   // mid-install: the binary is briefly gone
        h.Watcher.Tick();
        h.Stats["/bin/claude"] = Old;    // ...and back, unchanged
        h.Watcher.Tick();

        await Assert.That(h.Refreshes).IsEmpty();
    }

    [Test]
    public async Task A_binary_that_was_missing_at_startup_is_noticed_when_it_appears() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Watcher.PrimeBaselines();      // stat returns null: nothing to fingerprint yet

        h.Stats["/bin/claude"] = New;
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Several_vendors_changing_in_one_tick_share_one_refresh() {
        var h = new Harness(("claude", "/bin/claude"), ("codex", "/bin/codex"));
        h.Stats["/bin/claude"] = Old;
        h.Stats["/bin/codex"]  = new("/lib/codex.js", 500, 1);
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = New;
        h.Stats["/bin/codex"]  = new("/lib/codex.js", 520, 2);
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("claude");
        await Assert.That(h.Refreshes[0]).Contains("codex");
    }

    [Test]
    public async Task Only_the_changed_vendor_is_named() {
        var h = new Harness(("claude", "/bin/claude"), ("codex", "/bin/codex"));
        h.Stats["/bin/claude"] = Old;
        h.Stats["/bin/codex"]  = new("/lib/codex.js", 500, 1);
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/codex"] = new("/lib/codex.js", 520, 2);
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("codex");
        await Assert.That(h.Refreshes[0]).DoesNotContain("claude");
    }

    // The advertised versions are probed at daemon startup, before this service starts; a vendor
    // that updates in between must not be taken as the baseline, or the stale advertisement is
    // never corrected. A fingerprint recorded before the probe wins over the file seen at start.
    [Test]
    public async Task A_baseline_recorded_before_the_startup_probe_wins_over_the_file_at_start() {
        var refreshes = new List<string>();
        var watcher = VendorCliWatcher.ForTest(
            [("claude", "/bin/claude", [])], refreshes.Add, _ => New, TimeProvider.System,
            baselines: new Dictionary<string, CliBinaryStat?> { ["claude"] = Old });
        watcher.PrimeBaselines();

        watcher.Tick();

        await Assert.That(refreshes.Count).IsEqualTo(1);
    }

    // The fingerprint follows every link to the file that actually runs: a bare command name is
    // resolved on PATH, and a symlink chain is walked to its final target.
    [Test]
    public async Task The_real_fingerprint_follows_symlinks_to_the_installed_file() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Symlink creation needs no privilege only on Unix.");
        using var tmp = new TempDir();
        var target = tmp.CreateFile("versions/2.1.263/claude", "#!/bin/sh\necho 2.1.263\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        tmp.CreateDir("bin");
        var link = tmp.PathTo("bin/claude");
        File.CreateSymbolicLink(link, target);

        var stat = VendorCliWatcher.StatCliBinary(Binaries, link);

        await Assert.That(stat).IsNotNull();
        await Assert.That(stat!.Value.ResolvedPath).IsEqualTo(new FileInfo(target).FullName);
        await Assert.That(stat.Value.Size).IsEqualTo(new FileInfo(target).Length);
    }

    [Test]
    public async Task A_missing_binary_has_no_fingerprint() {
        using var tmp = new TempDir();

        await Assert.That(VendorCliWatcher.StatCliBinary(Binaries, tmp.PathTo("nope"))).IsNull();
    }

    [Test]
    public async Task A_changed_catalog_path_requests_one_refresh_naming_the_vendor() {
        var h = PiWithAuth();
        h.CatalogStats[Auth] = new(Auth, true, 10, 1);
        h.Watcher.PrimeBaselines();

        h.CatalogStats[Auth] = new(Auth, true, 12, 2);
        h.Watcher.Tick();
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("pi");
    }

    [Test]
    public async Task An_unchanged_catalog_path_requests_nothing() {
        var h = PiWithAuth();
        h.CatalogStats[Auth] = new(Auth, true, 10, 1);
        h.Watcher.PrimeBaselines();

        h.Watcher.Tick();

        await Assert.That(h.Refreshes).IsEmpty();
    }

    [Test]
    public async Task A_deleted_catalog_file_is_a_change() {
        var h = PiWithAuth();
        h.CatalogStats[Auth] = new(Auth, true, 10, 1);
        h.Watcher.PrimeBaselines();

        h.CatalogStats.Remove(Auth);
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_catalog_file_created_after_start_is_a_change() {
        var h = PiWithAuth();
        h.Watcher.PrimeBaselines();

        h.CatalogStats[Auth] = new(Auth, true, 10, 1);
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_recorded_catalog_baseline_that_differs_at_start_fires_on_the_first_tick() {
        var h = PiWithAuth(new Dictionary<string, CatalogPathStat[]> { ["pi"] = [new(Auth, true, 10, 1)] });
        h.CatalogStats[Auth] = new(Auth, true, 10, 2);
        h.Watcher.PrimeBaselines();

        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_catalog_only_vendors_recorded_binary_that_differs_at_start_fires_on_the_first_tick() {
        var refreshes = new List<string>();
        var watcher = VendorCliWatcher.ForTest(
            [("pi", "/bin/pi", [])], refreshes.Add, _ => New, TimeProvider.System,
            baselines: new Dictionary<string, CliBinaryStat?> { ["pi"] = Old },
            catalogBaselines: new Dictionary<string, CatalogPathStat[]> { ["pi"] = [] });
        watcher.PrimeBaselines();

        watcher.Tick();

        await Assert.That(refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WatchSet_includes_a_catalog_only_vendor_and_a_vendor_known_only_by_its_catalog() {
        var config = new DaemonConfig {
            UnattendedVendors = ["claude"],
            VendorModels      = new(StringComparer.Ordinal) { ["silent"] = [] },
        };
        var factories = new Dictionary<string, IHostedAgentRuntimeFactory>(StringComparer.Ordinal) {
            ["claude"] = new StubCatalogFactory("claude", null),
            ["pi"]     = new StubCatalogFactory("pi", null, paths: ["/a"]),
            ["silent"] = new StubCatalogFactory("silent", null),
            ["codex"]  = new StubCatalogFactory("codex", null),
        };

        var set = VendorCliWatcher.WatchSet(config, factories);

        await Assert.That(set.Select(w => w.Vendor)).IsEquivalentTo(["claude", "pi", "silent"]);
        await Assert.That(set.Single(w => w.Vendor == "pi").CatalogPaths).IsEquivalentTo(["/a"]);
    }
}
