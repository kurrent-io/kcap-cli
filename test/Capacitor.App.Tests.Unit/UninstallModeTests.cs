using System.Text.Json;
using System.Reactive.Linq;
using Capacitor.App.Platform.Linux;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Tests.Unit;

public class UninstallModeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Runner : IProcessRunner {
        public string[]? StreamedArgs;
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            Task.FromResult(new ProcessResult(0, "", "", false));
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) {
            StreamedArgs = args;
            onLine(new StreamedLine(ProcessStreamKind.Stdout, "Removed kcap"));
            return Task.FromResult(new StreamingResult(0, false, []));
        }
    }

    sealed class Removal : IAppRemoval {
        public bool Trashed;
        public Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public bool DeleteSupportFiles() => true;
        public Task<bool> TrashBundleAsync(CancellationToken ct) {
            Trashed = true;
            return Task.FromResult(true);
        }
        public void RevealBundle() { }
    }

    [Test]
    [Arguments(null, false, false)]
    [Arguments(new string[0], false, false)]
    [Arguments(new[] { "--setup", "--relaunch" }, false, false)]
    [Arguments(new[] { "--purge-local" }, false, false)]
    [Arguments(new[] { "--uninstall", "--relaunch" }, true, false)]
    [Arguments(new[] { "--uninstall", "--purge-local", "--relaunch" }, true, true)]
    public async Task Parse_reads_the_flags(string[]? args, bool uninstall, bool purgeLocal) {
        var mode = UninstallMode.Parse(args);

        await Assert.That(mode is not null).IsEqualTo(uninstall);
        if (mode is not null) await Assert.That(mode.PurgeLocal).IsEqualTo(purgeLocal);
    }

    string Bundle(bool withInfoPlist) {
        Tmp.CreateDir("kcap.app/Contents/MacOS");
        if (withInfoPlist) Tmp.CreateFile("kcap.app/Contents/Info.plist", "<plist/>");
        return Tmp.PathTo("kcap.app/Contents/MacOS/kcap-desktop");
    }

    /// <summary>Only an AppImage the running app came from, and that is still on disk, may reach the removal.</summary>
    [Test]
    public async Task A_linux_run_needs_the_appimage_it_came_from() {
        var cli     = new FakeKcapCli();
        var removal = new Removal();
        var image   = Tmp.CreateFile("kcap-x86_64.AppImage", "image");
        AppImageInstall? handed = null;

        await Assert.That(new UninstallMode(false).BuildAppImageRun(null, _ => removal, cli, () => { })).IsNull();
        await Assert.That(new UninstallMode(false).BuildAppImageRun(new AppImageInstall(Tmp.PathTo("gone.AppImage")), _ => removal, cli, () => { })).IsNull();

        var run = new UninstallMode(false).BuildAppImageRun(new AppImageInstall(image), install => { handed = install; return removal; }, cli, () => { });

        await Assert.That(run).IsNotNull();
        await Assert.That(handed!.Image).IsEqualTo(image);
    }

    [Test]
    public async Task A_run_outside_an_installed_bundle_builds_nothing() {
        var cli = new FakeKcapCli();
        var removal = new Removal();

        await Assert.That(new UninstallMode(false).BuildRun(Tmp.PathTo("bin/Debug/kcap-desktop"), _ => removal, cli, () => { })).IsNull();
        await Assert.That(new UninstallMode(false).BuildRun(Bundle(withInfoPlist: false), _ => removal, cli, () => { })).IsNull();
        await Assert.That(new UninstallMode(false).BuildRun(null, _ => removal, cli, () => { })).IsNull();
    }

    [Test]
    public async Task Removal_is_built_over_the_bundle_root() {
        string? root = null;

        var run = new UninstallMode(false).BuildRun(Bundle(withInfoPlist: true), r => { root = r; return new Removal(); }, new FakeKcapCli(), () => { });

        await Assert.That(run).IsNotNull();
        await Assert.That(root).IsEqualTo(Tmp.PathTo("kcap.app"));
    }

    /// The app's own composition for this mode — telemetry, the CLI identity read and the run —
    /// over a populated config root, which must come out exactly as it went in.
    [Test]
    public async Task Uninstall_mode_writes_nothing_under_the_config_root() {
        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new() { ["default"] = new Profile { ServerUrl = "https://kcap.example" } }
        };
        File.WriteAllText(AppConfig.GetConfigPath(Config.Root), JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));
        var before = Snapshot();

        var mode = UninstallMode.Parse(["--uninstall", "--purge-local", "--relaunch"])!;
        await CliTelemetry.Start(App.DesktopTelemetry(mode, "https://signup.example"), Config.Root, TimeProvider.System).FlushAndClose();
        var runner = new Runner();
        var cli = App.NewWizardCli(Config.Root, ProfileOverrides.None, NoLocalInstallation.Root, runner, "/test/kcap", new FakeLoginShellProbe());
        var removal = new Removal();
        var quit = false;
        var run = mode.BuildRun(Bundle(withInfoPlist: true), _ => removal, cli, () => quit = true, action => action())!;
        await run.RunAsync();

        await Assert.That(run.State).IsEqualTo(UninstallRunState.Finished);
        await Assert.That(runner.StreamedArgs).Contains("--purge-local");
        await Assert.That(removal.Trashed).IsTrue();
        await Assert.That(quit).IsFalse();
        await run.CloseCommand.Execute();
        await Assert.That(quit).IsTrue();
        await Assert.That(Snapshot()).IsEquivalentTo(before);
    }

    [Test]
    public async Task Uninstall_mode_suppresses_telemetry() {
        await Assert.That(App.DesktopTelemetry(new UninstallMode(false), "https://signup.example").Suppressed).IsTrue();
        await Assert.That(App.DesktopTelemetry(null, "https://signup.example").Suppressed).IsFalse();
    }

    string[] Snapshot() =>
        Directory.EnumerateFileSystemEntries(Config.Directory, "*", SearchOption.AllDirectories)
            .Select(p => $"{p}|{(File.Exists(p) ? File.ReadAllText(p) : "")}")
            .Order(StringComparer.Ordinal).ToArray();
}
