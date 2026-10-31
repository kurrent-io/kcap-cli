using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
[ExcludeOn(OS.Windows)]
public class LocalUninstallCommandTests {
    const string InstallationId = "0123456789abcdef0123456789abcdef";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(null, Home);

    FakeLocalServerService Service => field ??= new(_log, Home);

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    sealed class StackFactory(Func<LocalStack> create) : ILocalStackFactory {
        public Core.LocalEdition.LocalRoot Root => throw new InvalidOperationException("The command read the root without a platform refusal.");

        public LocalStack Create() => create();

        public LocalBundleStager Stager() => throw new InvalidOperationException("The command reached the bundles.");
    }

    sealed class NoBrowser : IBrowserLauncher {
        public bool TryOpen(string url) => throw new InvalidOperationException("The command opened a browser.");
    }

    async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args) {
        var config  = new ConfigRoot(Tmp.CreateDir("cfg"));
        var stacks  = new StackFactory(() => new(Root, Home, Service, new HttpClient(), TimeProvider.System, new FakeOutgoingRecorders(_log), config));
        var command = new LocalCommand(stacks, new LocalUpgradeCommand(stacks), new NoBrowser(), new FakeOutgoingRecorders(_log), config, Home);

        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await command.HandleAsync(["local", "uninstall", .. args]);

        return (exit, capture.GetCapturedOutput(), capture.GetCapturedError());
    }

    [Test]
    public async Task With_nothing_installed_it_says_so_and_succeeds() {
        var (exit, output, _) = await RunAsync("--yes");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("There is no local installation");
    }

    [Test]
    public async Task An_unknown_option_is_refused_before_anything_runs() {
        var (exit, _, errors) = await RunAsync("--purg");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(errors).Contains("--purg");
        await Assert.That(_log).IsEmpty();
    }

    [Test]
    public async Task Uninstall_with_yes_removes_the_server_and_keeps_its_data() {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty.WithActive(new(InstallationId, 9473, "http://localhost:9473", "1.2.3")));
        Service.Install(LocalServerUnit.ForInstallation(Root, 9473));
        Directory.CreateDirectory(Path.Combine(Root.Directory, "data"));

        var (exit, output, _) = await RunAsync("--yes");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Uninstalled the local server");
        await Assert.That(Directory.Exists(Path.Combine(Root.Directory, "data"))).IsTrue();
        await Assert.That(_log).Contains("uninstall");
    }
}
