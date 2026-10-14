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

    sealed class RecordingManager(Dictionary<string, string?> binaries) : IServiceManager {
        public readonly List<string> Uninstalled = [];
        int _statusReads;
        public int StatusReads => Volatile.Read(ref _statusReads);
        public string Describe() => "fake";
        public string UnitDirectory => "";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [.. binaries.Keys];
        public ServiceStatus Status(string serviceId) {
            Interlocked.Increment(ref _statusReads);
            return new(ServiceState.Installed, binaries[serviceId]);
        }
        public ServiceQuery Query(string serviceId) => new(LabelProbe.Loaded, true, ServiceState.Installed, binaries[serviceId], null);
        public void Install(ServiceSpec spec, bool startNow) { }
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
    [Arguments("/a/b/kcap-daemon", "/a/b", false, true)]
    [Arguments("/a/b/kcap-daemon", "/a/b/", false, true)]
    [Arguments("/a/bc/kcap-daemon", "/a/b", false, false)]
    [Arguments("/A/B/kcap-daemon", "/a/b", false, false)]
    [Arguments("/A/B/kcap-daemon", "/a/b", true, true)]
    [Arguments("/a/b/../c/kcap-daemon", "/a/b", false, false)]
    public async Task IsUnder_compares_whole_path_segments(string path, string dir, bool ignoreCase, bool expected) {
        await Assert.That(DaemonServiceCommands.IsUnder(Path.GetFullPath(path), dir, ignoreCase)).IsEqualTo(expected);
    }
}
