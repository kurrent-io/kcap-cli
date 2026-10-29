using System.Xml.Linq;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class ApplicationReferencesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Launchctl : IProcessRunner {
        public bool Loaded = true;
        public int Starts, Stops;
        public bool FailNextStart;
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            if (fileName != "/bin/launchctl") throw new InvalidOperationException("Unexpected executable: " + fileName);
            if (args[0] == "print") return Task.FromResult(Loaded
                ? new ProcessResult(0, "state = running\npid = 123\n", "", false)
                : new ProcessResult(113, "", "Could not find service", false));
            if (args[0] == "bootout") { Loaded = false; Stops++; }
            if (args[0] == "bootstrap") {
                Starts++;
                if (FailNextStart) { FailNextStart = false; return Task.FromResult(new ProcessResult(1, "", "failed to bootstrap", false)); }
                Loaded = true;
            }
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    (string Old, string Target, string Unit, string Link, DaemonStore Store, UserHome Home) Setup() {
        var old = Tmp.CreateDir("home/Applications/kcap.app");
        var target = Tmp.CreateDir("Applications/kcap.app");
        Tmp.CreateFile("home/Applications/kcap.app/Contents/MacOS/kcap");
        Tmp.CreateFile("Applications/kcap.app/Contents/MacOS/kcap");
        var unit = Tmp.CreateFile("home/Library/LaunchAgents/io.kurrent.kcap.daemon.work.plist");
        var dict = new XElement("dict", new XElement("key", "Label"), new XElement("string", "io.kurrent.kcap.daemon.work"),
            new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", old + "/Contents/MacOS/kcap-daemon"), new XElement("string", "--name"), new XElement("string", "work")),
            new XElement("key", "EnvironmentVariables"), new XElement("dict", new XElement("key", "KCAP_PROFILE"), new XElement("string", "my-profile")),
            new XElement("key", "ProcessType"), new XElement("string", "Standard"));
        File.WriteAllText(unit, new XDocument(new XElement("plist", dict)).ToString());
        var link = Path.Combine(Tmp.CreateDir("bin"), "kcap");
        File.CreateSymbolicLink(link, old + "/Contents/MacOS/kcap");
        return (old, target, unit, link, new DaemonStore(Tmp.CreateDir("daemons")), new UserHome(Tmp.PathTo("home")));
    }

    sealed class BundleRunner(Launchctl launchctl) : IProcessRunner {
        readonly ProcessRunner _copy = new(TimeProvider.System);
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            fileName == "ditto" ? _copy.RunAsync(fileName, args, options, ct) : launchctl.RunAsync(fileName, args, options, ct);
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    /// The running copy keeps its bundle while it can still load from it: references move before the installed copy
    /// opens, and the old bundle is left for the installed copy to remove once this process has exited.
    [Test]
    public async Task A_user_Applications_copy_moves_real_files_and_references_before_opening_and_is_handed_over() {
        if (!OperatingSystem.IsMacOS()) return;
        var paths = Setup();
        static void Bundle(string root, string version) {
            File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"),
                $"<plist><dict><key>CFBundleIdentifier</key><string>io.kurrent.kcap</string><key>CFBundleVersion</key><string>{version}</string></dict></plist>");
            File.WriteAllText(Path.Combine(root, "Contents", "MacOS", "kcap-desktop"), version);
        }
        Bundle(paths.Old, "1.1.0");
        Directory.Delete(paths.Target, recursive: true);
        var ctl = new Launchctl();
        var runner = new BundleRunner(ctl);
        var activated = false;
        var retirement = new ApplicationRetirement(new ConfigRoot(Tmp.CreateDir("config")), TimeProvider.System, _ => []);
        var mover = new ApplicationsMover(runner, ApplicationsMover.PromoteExclusive, Path.GetDirectoryName(paths.Target)!,
            ApplicationsMover.SwapAtomic,
            userApplicationsDir: Path.GetDirectoryName(paths.Old), retirement: retirement,
            migrateReferences: () => new ApplicationReferences(runner, paths.Home, paths.Store, TimeProvider.System,
                (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(true), "gui/123", [Path.GetDirectoryName(paths.Link)!]),
            activate: (root, _) => {
                activated = root == paths.Target && ctl.Loaded && Directory.Exists(paths.Old)
                    && File.ReadAllText(paths.Unit).Contains(paths.Target, StringComparison.Ordinal)
                    && new FileInfo(paths.Link).LinkTarget == paths.Target + "/Contents/MacOS/kcap";
                return Task.FromResult(activated);
            });
        var outcome = await mover.MoveAsync(paths.Old, CancellationToken.None);
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(activated).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(paths.Target, "Contents", "MacOS", "kcap-desktop"))).IsEqualTo("1.1.0");
        await Assert.That(Directory.GetDirectories(Path.GetDirectoryName(paths.Target)!).Length).IsEqualTo(1);
        await Assert.That(ctl.Starts).IsEqualTo(1);
        await Assert.That(await retirement.RunPendingAsync(paths.Target, CancellationToken.None)).IsEqualTo(paths.Old);
        await Assert.That(Directory.Exists(paths.Old)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Migration_repoints_owned_references_preserves_service_settings_and_keeps_stopped_services_stopped(bool loaded) {
        if (OperatingSystem.IsWindows()) return;
        var paths = Setup();
        var runner = new Launchctl { Loaded = loaded };
        var kept = 0;
        await using var refs = new ApplicationReferences(runner, paths.Home, paths.Store, TimeProvider.System,
            (_, _) => { kept++; return Task.FromResult(true); }, (_, _) => Task.FromResult(true),
            "gui/123", [Path.GetDirectoryName(paths.Link)!]);
        await refs.PrepareAsync([paths.Old], paths.Target, CancellationToken.None);
        await refs.ApplyAsync(CancellationToken.None);
        await Assert.That(new FileInfo(paths.Link).LinkTarget).IsEqualTo(paths.Target + "/Contents/MacOS/kcap");
        await Assert.That(File.ReadAllText(paths.Unit)).Contains(paths.Target + "/Contents/MacOS/kcap-daemon");
        await Assert.That(File.ReadAllText(paths.Unit)).Contains("my-profile");
        await Assert.That(kept).IsEqualTo(loaded ? 1 : 0);
        await Assert.That(runner.Starts).IsEqualTo(loaded ? 1 : 0);
        await Assert.That(File.GetUnixFileMode(paths.Unit)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Refused_agent_preservation_leaves_the_running_service_and_links_intact() {
        if (OperatingSystem.IsWindows()) return;
        var paths = Setup();
        var original = File.ReadAllText(paths.Unit);
        var runner = new Launchctl();
        await using var refs = new ApplicationReferences(runner, paths.Home, paths.Store, TimeProvider.System,
            (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(true), "gui/123", [Path.GetDirectoryName(paths.Link)!]);
        await Assert.That(async () => await refs.PrepareAsync([paths.Old], paths.Target, CancellationToken.None)).Throws<IOException>();
        await refs.RollbackAsync(() => { });
        await Assert.That(runner.Stops).IsEqualTo(0);
        await Assert.That(runner.Starts).IsEqualTo(0);
        await Assert.That(File.ReadAllText(paths.Unit)).IsEqualTo(original);
        await Assert.That(new FileInfo(paths.Link).LinkTarget).IsEqualTo(paths.Old + "/Contents/MacOS/kcap");
    }

    [Test]
    public async Task A_failed_daemon_start_restores_the_old_unit_link_and_running_service() {
        if (OperatingSystem.IsWindows()) return;
        var paths = Setup();
        var original = File.ReadAllText(paths.Unit);
        var runner = new Launchctl { FailNextStart = true };
        await using var refs = new ApplicationReferences(runner, paths.Home, paths.Store, TimeProvider.System,
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(new FileInfo(paths.Link).LinkTarget == paths.Old + "/Contents/MacOS/kcap"), "gui/123", [Path.GetDirectoryName(paths.Link)!]);
        await refs.PrepareAsync([paths.Old], paths.Target, CancellationToken.None);
        await Assert.That(async () => await refs.ApplyAsync(CancellationToken.None)).Throws<IOException>();
        var restoredBundle = false;
        await refs.RollbackAsync(() => restoredBundle = true);
        await Assert.That(restoredBundle).IsTrue();
        await Assert.That(File.ReadAllText(paths.Unit)).IsEqualTo(original);
        await Assert.That(new FileInfo(paths.Link).LinkTarget).IsEqualTo(paths.Old + "/Contents/MacOS/kcap");
        await Assert.That(runner.Loaded).IsTrue();
        await Assert.That(runner.Starts).IsEqualTo(2);
    }

    [Test]
    public async Task Rollback_keeps_new_agents_for_resumption_before_restoring_the_old_bundle() {
        if (OperatingSystem.IsWindows()) return;
        var paths = Setup();
        var runner = new Launchctl();
        var kept = 0;
        await using var refs = new ApplicationReferences(runner, paths.Home, paths.Store, TimeProvider.System,
            (_, _) => { kept++; return Task.FromResult(true); }, (_, _) => Task.FromResult(true), "gui/123", [Path.GetDirectoryName(paths.Link)!]);
        await refs.PrepareAsync([paths.Old], paths.Target, CancellationToken.None);
        await refs.ApplyAsync(CancellationToken.None);
        var stoppedBeforeRestore = false;
        await refs.RollbackAsync(() => stoppedBeforeRestore = !runner.Loaded && kept == 2);
        await Assert.That(stoppedBeforeRestore).IsTrue();
        await Assert.That(runner.Loaded).IsTrue();
        await Assert.That(new FileInfo(paths.Link).LinkTarget).IsEqualTo(paths.Old + "/Contents/MacOS/kcap");
    }

    [Test]
    public async Task Temporary_link_cleanup_does_not_hide_the_original_failure() {
        if (OperatingSystem.IsWindows()) return;
        var staging = Path.Combine(Tmp.CreateDir("bin"), "kcap.migrating-test");
        File.CreateSymbolicLink(staging, Tmp.PathTo("missing/kcap"));
        ApplicationReferences.CleanupLink(staging, _ => throw new UnauthorizedAccessException("protected directory"));
        await Assert.That(new FileInfo(staging).LinkTarget).IsNotNull();
    }

    [Test]
    public async Task Foreign_terminal_links_are_left_untouched() {
        if (OperatingSystem.IsWindows()) return;
        var paths = Setup();
        File.Delete(paths.Link);
        File.CreateSymbolicLink(paths.Link, Tmp.CreateFile("npm/kcap"));
        var original = new FileInfo(paths.Link).LinkTarget;
        var runner = new Launchctl { Loaded = false };
        await using var refs = new ApplicationReferences(runner, paths.Home, paths.Store, TimeProvider.System,
            domainOverride: "gui/123", shimFolders: [Path.GetDirectoryName(paths.Link)!]);
        await refs.PrepareAsync([paths.Old], paths.Target, CancellationToken.None);
        await refs.ApplyAsync(CancellationToken.None);
        await Assert.That(new FileInfo(paths.Link).LinkTarget).IsEqualTo(original);
    }
}
