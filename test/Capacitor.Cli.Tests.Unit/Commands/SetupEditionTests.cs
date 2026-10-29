using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A bare, interactive <c>kcap setup</c> opens on the app's edition choice. The Spectre singleton and console are
/// process-global, so bare.
/// </summary>
[NotInParallel]
public class SetupEditionTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly RecordingBrowser _browser = new();

    sealed class DiscoveryStarted() : Exception("setup reached workspace discovery");

    SetupCommand Command(bool localAvailable) {
        var http = new PlainHttpClientFactory();

        return new SetupCommand(
            Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), _browser, Home,
            TestHarnesses.Under(Home), new AgentsPaths(Home), new FixedCapacitorHttpClient(),
            new TenantProvisioningClient(new HttpClient(), ClientAgent.Cli), new AuthProviderDiscovery(http, TimeProvider.System),
            NoTelemetry.Facade, AuthEndpoints.Defaults, new FakeFacadeFactory(_ => throw new DiscoveryStarted()),
            FakeImportRunner.Of(_ => throw new InvalidOperationException("must not run import")),
            FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(),
            new ChosenServerHttp(Config.Root, Resolutions.None(Config.Root), ProfileOverrides.None, MachineAuth.None, NoLocalInstallation.Root),
            router: new GitProviderRouter(), workdir: new WorkingDirectory(Config.Directory), TimeProvider.System,
            TestBinaries.None, TestAccounts.None, TestPluginEnvironment.For(Home, Home.PathTo("plugin")), NoLocalInstallation.Root,
            FakeLocalInstallerFactory.Unused) {
            PickHandoffVendor = _ => "Skip",
            LocalEditionAvailable = localAvailable,
        };
    }

    [Test]
    public async Task The_choice_names_the_apps_three_doors() {
        using var console = new ScriptedConsole().Down().Down().Enter();

        await Assert.That(async () => await Command(localAvailable: true).HandleAsync(["setup"])).Throws<DiscoveryStarted>();

        await Assert.That(console.Text).Contains(SetupEdition.LocalLabel);
        await Assert.That(console.Text).Contains(SetupEdition.CloudLabel);
        await Assert.That(console.Text).Contains(SetupEdition.ExistingLabel);
        await Assert.That(_browser.Urls).IsEmpty();
    }

    [Test]
    public async Task Local_runs_the_local_install() {
        using var noUrl   = EnvScope.Exclusive(ProfileOverrides.UrlVar, null);
        using var console = new ScriptedConsole().Enter();
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Command(localAvailable: true).HandleAsync(["setup"]);

        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("This test never runs a local setup.");
    }

    [Test]
    public async Task Cloud_opens_sign_up_in_the_browser_then_discovers_the_new_workspace() {
        using var console = new ScriptedConsole().Down().Enter().Enter();

        await Assert.That(async () => await Command(localAvailable: true).HandleAsync(["setup"])).Throws<DiscoveryStarted>();

        await Assert.That(_browser.Urls).IsEquivalentTo([SetupEdition.SignUpUrl(AuthEndpoints.Defaults)]);
        await Assert.That(console.Text).Contains(SetupEdition.SignUpUrl(AuthEndpoints.Defaults));
    }

    [Test]
    public async Task Where_local_is_unavailable_it_is_not_offered() {
        using var console = new ScriptedConsole().Down().Enter();

        await Assert.That(async () => await Command(localAvailable: false).HandleAsync(["setup"])).Throws<DiscoveryStarted>();

        await Assert.That(console.Text).DoesNotContain(SetupEdition.LocalLabel);
        await Assert.That(_browser.Urls).IsEmpty();
    }

    [Test]
    public async Task A_named_server_skips_the_choice() {
        using var console = new ScriptedConsole();

        // A closed loopback port: the run stops at the server step without asking anything first.
        var exit = await Command(localAvailable: true).HandleAsync(["setup", "--server-url", "http://127.0.0.1:9"]);

        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await Assert.That(console.Text).DoesNotContain(SetupEdition.CloudLabel);
    }
}
