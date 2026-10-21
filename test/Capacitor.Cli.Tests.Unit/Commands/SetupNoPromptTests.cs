using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap setup --no-prompt</c> on a keyboard it must never read. The Spectre singleton, console, environment and the
/// <c>/auth/config</c> memo are process-global, so bare.
/// </summary>
[NotInParallel]
public class SetupNoPromptTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Bin { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home, TestBinaries.Searching(Bin, "codex"));

    SetupCommand Command() {
        var http = new PlainHttpClientFactory();

        return new SetupCommand(
            Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new RecordingBrowser(), Home,
            Harnesses, new AgentsPaths(Home), new FixedCapacitorHttpClient(),
            new TenantProvisioningClient(new HttpClient(), ClientAgent.Cli), new AuthProviderDiscovery(http, TimeProvider.System),
            NoTelemetry.Facade, AuthEndpoints.Defaults,
            new SetupFacadeFactory(Config.Root, AuthFixtures.NewTokenStore(Config.Root), http,
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new GitHubOAuthClient(http),
                new WorkOSClient(http, TimeProvider.System), new RecordingBrowser(), NoTelemetry.Facade, AuthEndpoints.Defaults,
                TimeProvider.System, new TenantProvisioningClient(new HttpClient(), ClientAgent.Cli)),
            FakeImportRunner.Succeeding(), FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(),
            new ChosenServerHttp(Config.Root, Resolutions.None(Config.Root), ProfileOverrides.None, MachineAuth.None, NoLocalInstallation.Root),
            router: new GitProviderRouter(), workdir: new WorkingDirectory(Config.Directory), TimeProvider.System,
            TestBinaries.None, TestAccounts.None, TestPluginEnvironment.For(Home, Home.PathTo("plugin")), NoLocalInstallation.Root,
            FakeLocalInstallerFactory.Unused) {
            PickHandoffVendor = _ => throw new InvalidOperationException("--no-prompt must not offer the handoff picker")
        };
    }

    static WireMockServer NoAuthServer() {
        var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"None"}"""));

        return server;
    }

    /// <summary>An interactive console with no keys throws at the first prompt, and every question this run could raise
    /// is live: a detected harness, its Codex network question, and a provider key in the environment.</summary>
    [Test]
    public async Task A_run_where_every_question_applies_asks_none_of_them() {
        using var server  = NoAuthServer();
        using var key     = EnvScope.Exclusive("ANTHROPIC_API_KEY", "sk-test");
        using var console = new ScriptedConsole();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command().HandleAsync(["setup", "--server-url", server.Url!, "--no-prompt"]);

        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(console.Text + capture.GetCapturedError());
        await Assert.That(File.Exists(Harnesses.Of<CodexHarness>().Paths.UserHooksJson)).IsTrue();
    }

    [Test]
    public async Task Skip_harnesses_installs_nothing_for_a_detected_agent() {
        using var server  = NoAuthServer();
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command().HandleAsync(["setup", "--server-url", server.Url!, "--no-prompt", "--skip-harnesses", "--skip-import"]);

        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(spectre.Text + capture.GetCapturedError());
        await Assert.That(File.Exists(Harnesses.Of<CodexHarness>().Paths.UserHooksJson)).IsFalse();
        await Assert.That(File.Exists(Harnesses.Of<CodexHarness>().Paths.ConfigToml)).IsFalse();
        await Assert.That(spectre.Flat).Contains("--skip-harnesses");
    }
}
