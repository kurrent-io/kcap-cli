using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The exit codes and the JSON result the desktop app and <c>kcap update</c> read.</summary>
[NotInParallel]
[ExcludeOn(OS.Windows)]
public class LocalUpgradeCommandTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class StackFactory(Func<LocalStack> create, Func<LocalBundleStager>? stager = null) : ILocalStackFactory {
        public LocalStack Create() => create();

        public LocalBundleStager Stager() => stager?.Invoke() ?? throw new InvalidOperationException("The command reached the bundles.");
    }

    UserHome Home => field ??= NewHome();

    Core.LocalEdition.LocalRoot Root => field ??= Core.LocalEdition.LocalRoot.Resolve(Tmp.PathTo("r"), Home);

    FakeLocalServerService Service => field ??= new([], Home);

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    async Task<(int Exit, string Out)> RunAsync(string version, params string[] args) => await RunAsync(version, null, args);

    async Task<(int Exit, string Out)> RunAsync(string version, Func<LocalBundleStager>? stager, params string[] args) {
        var stack = new LocalStack(Root, Home, Service, new HttpClient(), TimeProvider.System, new FakeOutgoingRecorders([]), new ConfigRoot(Tmp.PathTo("cfg"))) {
            FindHolder = _ => null,
        };

        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await new LocalUpgradeCommand(new StackFactory(() => stack, stager)) { Version = version }.HandleAsync(["local", "upgrade", .. args]);

        return (exit, capture.GetCapturedOutput());
    }

    [Test]
    public async Task Without_an_installation_json_reports_nothing_to_do_and_exits_3() {
        var (exit, output) = await RunAsync("1.3.0", "--json");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.NothingToDo);
        await Assert.That(output.Trim()).IsEqualTo("""{"v":1,"event":"nothing_to_do","version":"1.3.0","reason":"not_installed"}""");
    }

    [Test]
    public async Task If_installed_is_silent_without_an_installation() {
        var (exit, output) = await RunAsync("1.3.0", "--if-installed");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.NothingToDo);
        await Assert.That(output).IsEmpty();
    }

    [Test]
    public async Task A_build_that_is_not_a_release_names_no_bundle() {
        var (exit, output) = await RunAsync("unknown", "--json");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.Failed);
        await Assert.That(output).Contains("\"code\":\"invalid_arguments\"");
    }

    /// <summary>A CLI release can reach its channel before its bundle; that is not an upgrade failure.</summary>
    [Test]
    public async Task An_unpublished_bundle_exits_4_and_changes_nothing() {
        Root.EnsureCreated();
        Core.LocalEdition.LocalStateFiles.PublishRegistry(Root,
            Core.LocalEdition.InstallRegistry.Empty.WithActive(new("0123456789abcdef0123456789abcdef", 47391, "http://localhost:47391", "1.2.3")));
        Tmp.CreateFile(["r", "bin", "1.2.3", "kcap-server"]);
        LocalBin.Switch(Root, "1.2.3");
        Service.Install(LocalServerUnit.ForInstallation(Root, 47391));

        using var bundle = new FakeBundle(Tmp.PathTo("bundle.tar.gz"), "1.3.0") { ManifestMissing = true };
        var platform = new LocalPlatform(true, false, false, System.Runtime.InteropServices.Architecture.Arm64);

        var (exit, output) = await RunAsync("1.3.0", () => new(Root, bundle.Client(), bundle.Key, platform), "--json");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.NotPublished);
        await Assert.That(output).Contains("\"code\":\"bundle_not_published\"");
        await Assert.That(Service.Running).IsTrue();
    }

    [Test]
    public async Task An_unknown_option_is_refused() {
        var (exit, output) = await RunAsync("1.3.0", "--json", "--bogus");

        await Assert.That(exit).IsEqualTo(LocalUpgradeCommand.Failed);
        await Assert.That(output).Contains("--bogus");
    }
}
