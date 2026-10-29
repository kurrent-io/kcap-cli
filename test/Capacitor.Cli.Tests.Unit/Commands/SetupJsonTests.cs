using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap setup --json --no-prompt</c> against a hosted server: one JSON object per line on stdout for the whole run,
/// the narration on stderr. Console, the Spectre singleton and the <c>/auth/config</c> memo are process-global, so bare.
/// </summary>
[NotInParallel]
public class SetupJsonTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SetupCommand Command() {
        var http = new PlainHttpClientFactory();

        return new SetupCommand(
            Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new RecordingBrowser(), Home,
            TestHarnesses.Under(Home), new AgentsPaths(Home), new FixedCapacitorHttpClient(),
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
            PickHandoffVendor = _ => "Skip"
        };
    }

    async Task<(int Exit, string[] Stdout, string Spectre, string Stderr)> Run(params string[] args) {
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command().HandleAsync(args);

        return (exit, capture.GetCapturedOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries), spectre.Text, capture.GetCapturedError());
    }

    static JsonNode[] Lines(string[] stdout) => [.. stdout.Select(l => JsonNode.Parse(l)!)];

    static string[] Events(string[] stdout) => [.. Lines(stdout).Select(n => n["event"]!.GetValue<string>())];

    static async Task AssertAllJson(string[] stdout) {
        foreach (var line in stdout)
            await Assert.That(JsonNode.Parse(line)?["v"]?.GetValue<int>()).IsEqualTo(1).Because(line);
    }

    static WireMockServer NoAuthServer() {
        var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"None"}"""));

        return server;
    }

    [Test]
    public async Task A_finished_run_reports_each_step_and_ends_with_done() {
        using var server = NoAuthServer();

        var (exit, stdout, spectre, stderr) = await Run(
            ["setup", "--server-url", server.Url!, "--json", "--no-prompt", "--skip-import", "--default-visibility", "private",
             .. SetupCommandTests.SkipAllAgentInstallFlags]);

        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(stderr);
        await AssertAllJson(stdout);
        await Assert.That(spectre).IsEmpty();
        await Assert.That(stderr).Contains("Step 1/6");

        var started = Lines(stdout).Where(n => n["event"]!.GetValue<string>() == "step_started").Select(n => n["step"]!.GetValue<string>());
        await Assert.That(started).IsEquivalentTo(["server", "sign_in", "visibility", "harnesses", "daemon", "import"]);

        var completed = Lines(stdout).Where(n => n["event"]!.GetValue<string>() == "step_completed").ToDictionary(n => n["step"]!.GetValue<string>());
        await Assert.That(completed["server"]["server_url"]?.GetValue<string>()).IsEqualTo(server.Url!.TrimEnd('/'));
        await Assert.That(completed["sign_in"]["credential"]?.GetValue<string>()).IsEqualTo("none");
        await Assert.That(completed["visibility"]["visibility"]?.GetValue<string>()).IsEqualTo("private");
        await Assert.That(completed["import"]["outcome"]?.GetValue<string>()).IsEqualTo("skipped");

        var done = Lines(stdout)[^1];
        await Assert.That(done["event"]?.GetValue<string>()).IsEqualTo("done");
        await Assert.That(done["server_url"]?.GetValue<string>()).IsEqualTo(server.Url!.TrimEnd('/'));
    }

    [Test]
    public async Task An_unreachable_server_ends_with_a_failed_line_and_exit_1() {
        // A closed loopback port.
        var (exit, stdout, _, _) = await Run(["setup", "--server-url", "http://127.0.0.1:9", "--json", "--no-prompt"]);

        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await AssertAllJson(stdout);

        var last = Lines(stdout)[^1];
        await Assert.That(last["event"]?.GetValue<string>()).IsEqualTo("failed");
        await Assert.That(last["code"]?.GetValue<string>()).IsEqualTo("server_unreachable");
        await Assert.That(last["step"]?.GetValue<string>()).IsEqualTo("server");
    }

    [Test]
    public async Task A_missing_server_is_refused_with_answer_required_and_exit_2() {
        var (exit, stdout, _, _) = await Run("setup", "--json", "--no-prompt");

        await Assert.That(exit).IsEqualTo(SetupExit.Refused);
        await Assert.That(stdout.Length).IsEqualTo(1);
        await Assert.That(Lines(stdout)[0]["code"]?.GetValue<string>()).IsEqualTo("answer_required");
        await Assert.That(Lines(stdout)[0]["message"]?.GetValue<string>()).Contains("--server-url");
    }

    [Test]
    public async Task A_bad_argument_is_refused_with_invalid_arguments() {
        var (exit, stdout, _, _) = await Run("setup", "--json", "--no-prompt", "--org", "Acme");

        await Assert.That(exit).IsEqualTo(SetupExit.Refused);
        await Assert.That(Events(stdout)).IsEquivalentTo(["failed"]);
        await Assert.That(Lines(stdout)[0]["code"]?.GetValue<string>()).IsEqualTo("invalid_arguments");
        await Assert.That(Lines(stdout)[0]["message"]?.GetValue<string>()).Contains("--slug");
    }

    [Test]
    public async Task Json_without_no_prompt_is_refused_naming_it() {
        var (exit, stdout, _, _) = await Run("setup", "--json");

        await Assert.That(exit).IsEqualTo(SetupExit.Refused);
        await Assert.That(Lines(stdout)[0]["message"]?.GetValue<string>()).Contains("--no-prompt");
    }
}
