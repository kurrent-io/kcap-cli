using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// An app's uninstaller removes only the units that run its own daemon copy; a unit pointing
/// anywhere else belongs to another install and must survive.
[NotInParallel]
public class DaemonCommandsServiceUninstallUnderTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Unreadable = "<unreadable>";

    sealed class RecordingManager(Dictionary<string, string?> binaries) : IServiceManager {
        public readonly List<string> Uninstalled = [];
        int _statusReads;
        public int StatusReads => Volatile.Read(ref _statusReads);
        public string Describe() => "fake";
        public string UnitDirectory => "";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public bool ListingFails;
        public IReadOnlyList<string> ListInstalled() => [.. binaries.Keys];
        public IReadOnlyList<string>? TryListInstalled() => ListingFails ? null : ListInstalled();
        public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => null;
        public ServiceStatus Status(string serviceId) {
            Interlocked.Increment(ref _statusReads);
            return binaries[serviceId] == Unreadable
                ? throw new IOException("unreadable wrapper")
                : new(ServiceState.Installed, binaries[serviceId]);
        }
        public ServiceQuery Query(string serviceId) => new(LabelProbe.Loaded, true, ServiceState.Installed, binaries[serviceId], null);
        public bool Install(ServiceSpec spec, bool startNow, out string? error) { error = null; return true; }
        public void WriteAndBootstrap(ServiceSpec spec) { }

        public bool Uninstall(string serviceId, out string? error) {
            Uninstalled.Add(serviceId);
            error = null;
            return true;
        }

        public bool Start(string serviceId, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, out string? error) { error = null; return true; }
    }

    DaemonServiceCommands Commands(IServiceManager manager) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "unused", Home, TimeProvider.System);

    [Test]
    public async Task Removes_only_units_whose_daemon_is_inside_the_folder() {
        var app = Path.GetFullPath("/apps/KurrentCapacitor/current");
        var manager = new RecordingManager(new() {
            ["laptop"] = Path.Combine(app, "kcap-daemon.exe"),
            ["npm"]    = Path.GetFullPath("/npm/node_modules/@kurrent/kcap/bin/kcap-daemon.exe"),
            ["near"]   = Path.GetFullPath("/apps/KurrentCapacitor/current-old/kcap-daemon.exe"),
            ["blank"]  = null,
        });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(manager.Uninstalled).IsEquivalentTo(["laptop"]);
    }

    [Test]
    public async Task A_unit_another_operation_holds_is_left_and_reported() {
        var app = Path.GetFullPath("/apps/KurrentCapacitor/current");
        var manager = new RecordingManager(new() { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") });
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "laptop", TimeSpan.Zero, TimeProvider.System);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Uninstalled).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains("'laptop'");
    }

    [Test]
    public async Task A_unit_repointed_while_the_lock_was_held_is_left_alone() {
        var app = Path.GetFullPath("/apps/KurrentCapacitor/current");
        var binaries = new Dictionary<string, string?> { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") };
        var manager = new RecordingManager(binaries);
        var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "laptop", TimeSpan.Zero, TimeProvider.System);
        using var console = ConsoleOutput.StartFullCapture();

        var run = Commands(manager).UninstallUnder(app);
        while (manager.StatusReads == 0) await Task.Delay(10);
        binaries["laptop"] = Path.GetFullPath("/npm/node_modules/@kurrent/kcap/bin/kcap-daemon.exe");
        held!.Dispose();
        var exit = await run;

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(manager.Uninstalled).IsEmpty();
    }

    [Test]
    public async Task A_unit_that_cannot_be_read_does_not_stop_the_others() {
        var app = Path.GetFullPath("/apps/KurrentCapacitor/current");
        var manager = new RecordingManager(new() {
            ["broken"] = Unreadable,
            ["laptop"] = Path.Combine(app, "kcap-daemon.exe"),
        });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Uninstalled).IsEquivalentTo(["laptop"]);
        await Assert.That(console.GetCapturedError()).Contains("'broken'");
    }

    /// <summary>A listing that failed is not an empty one: reporting success would leave the app's task
    /// retrying its deleted daemon at every sign-in.</summary>
    [Test]
    public async Task A_listing_that_fails_is_reported_not_taken_as_no_units() {
        var app = Path.GetFullPath("/apps/KurrentCapacitor/current");
        var manager = new RecordingManager(new() { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") }) { ListingFails = true };
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Uninstalled).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains("Could not list the installed services");
    }

    [Test]
    [Arguments(new[] { "--json" }, null)]
    [Arguments(new[] { "--under", "C:/apps/kcap" }, "C:/apps/kcap")]
    [Arguments(new[] { "--under" }, "")]
    [Arguments(new[] { "--under", "" }, "")]
    [Arguments(new[] { "--under", "  " }, "")]
    [Arguments(new[] { "--under", "--name", "foo" }, "")]
    public async Task UnderArg_refuses_a_missing_blank_or_flag_value(string[] rest, string? expected) {
        await Assert.That(DaemonServiceCommands.UnderArg(rest)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/a/b/kcap-daemon", "/a/b", false, true)]
    [Arguments("/a/b/kcap-daemon", "/a/b/", false, true)]
    [Arguments("/a/bc/kcap-daemon", "/a/b", false, false)]
    [Arguments("/A/B/kcap-daemon", "/a/b", false, false)]
    [Arguments("/A/B/kcap-daemon", "/a/b", true, true)]
    [Arguments("/a/b/../c/kcap-daemon", "/a/b", false, false)]
    [Arguments("/a/kcap-daemon", "/", false, true)]
    public async Task IsUnder_compares_whole_path_segments(string path, string dir, bool ignoreCase, bool expected) {
        await Assert.That(DaemonServiceCommands.IsUnder(Path.GetFullPath(path), dir, ignoreCase)).IsEqualTo(expected);
    }
}
