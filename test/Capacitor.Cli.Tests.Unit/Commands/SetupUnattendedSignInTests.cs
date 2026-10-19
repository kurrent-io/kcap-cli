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
/// <c>kcap setup --no-prompt</c> against a server that needs a sign-in: a machine credential or a stored token is used
/// before any person is asked. Console and the process-wide <c>/auth/config</c> memo are global, so bare.
/// </summary>
[NotInParallel]
public class SetupUnattendedSignInTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string MachineToken = "machine-bearer";

    sealed class SignInAttempted() : Exception("setup reached the person sign-in");

    SetupCommand Command(MachineAuth machine) {
        var http = new PlainHttpClientFactory();

        return new SetupCommand(
            Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new RecordingBrowser(), Home,
            TestHarnesses.Under(Home), new AgentsPaths(Home), new FixedCapacitorHttpClient(),
            new TenantProvisioningClient(new HttpClient(), ClientAgent.Cli), new AuthProviderDiscovery(http, TimeProvider.System),
            NoTelemetry.Facade, AuthEndpoints.Defaults, new FakeFacadeFactory(_ => throw new SignInAttempted()),
            FakeImportRunner.Of(_ => throw new InvalidOperationException("must not run import")),
            FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(),
            new ChosenServerHttp(Config.Root, Resolutions.None(Config.Root), ProfileOverrides.None, machine, NoLocalInstallation.Root),
            router: new GitProviderRouter(), workdir: new WorkingDirectory(Config.Directory), TimeProvider.System,
            TestBinaries.None, TestAccounts.None, TestPluginEnvironment.For(Home, Home.PathTo("plugin")), NoLocalInstallation.Root,
            FakeLocalInstallerFactory.Unused) {
            PickHandoffVendor = _ => "Skip"
        };
    }

    static WireMockServer WorkOSServer(string acceptedBearer) {
        var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"WorkOS","client_id":"client_test"}"""));

        server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet().WithHeader("Authorization", $"Bearer {acceptedBearer}"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .AtPriority(2)
            .RespondWith(Response.Create().WithStatusCode(401));

        return server;
    }

    static void Mints(WireMockServer server, int status) =>
        server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"access_token":"{{MachineToken}}","expires_in":3600}"""));

    static MachineAuth Machine(WireMockServer server) => new("client_test", "secret", $"{server.Url}/oauth2/token");

    static string[] Args(WireMockServer server) =>
        ["setup", "--server-url", server.Url!, "--no-prompt", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags];

    async Task<Profile> ActiveProfileAsync() {
        var cfg = await AppConfig.LoadProfileConfig(Config.Root);

        return cfg.Profiles[cfg.ActiveName];
    }

    [Test]
    public async Task A_working_machine_credential_configures_the_server_without_a_sign_in() {
        using var server = WorkOSServer(MachineToken);
        Mints(server, 200);
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(Machine(server)).HandleAsync(Args(server));

        var output = capture.GetCapturedOutput() + capture.GetCapturedError() + spectre.Text;
        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(output);
        await Assert.That(output).Contains(MachineAuth.ClientIdVar);
        await Assert.That((await ActiveProfileAsync()).ServerUrl).IsEqualTo(server.Url!.TrimEnd('/'));
        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("default")).IsNull();
    }

    /// <summary>The service unit is a file on disk and never carries the secret, so a service would start a daemon that
    /// cannot authenticate.</summary>
    [Test]
    public async Task A_machine_credential_leaves_the_daemon_service_alone_and_says_so() {
        using var server = WorkOSServer(MachineToken);
        Mints(server, 200);
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        await Command(Machine(server)).HandleAsync(Args(server));

        var output = capture.GetCapturedOutput() + capture.GetCapturedError() + spectre.Text;
        await Assert.That(output).DoesNotContain("Running the agent daemon as a background service");
        await Assert.That(output).Contains("No daemon service was set up");
    }

    [Test]
    public async Task A_machine_credential_the_token_endpoint_refuses_fails_without_falling_back_to_a_person() {
        using var server = WorkOSServer(MachineToken);
        Mints(server, 401);
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(Machine(server)).HandleAsync(Args(server));

        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("machine credential");
        await Assert.That((await ActiveProfileAsync()).ServerUrl).IsNull();
    }

    [Test]
    public async Task Under_json_a_refused_machine_credential_ends_with_its_own_code() {
        using var server = WorkOSServer(MachineToken);
        Mints(server, 401);
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(Machine(server)).HandleAsync([.. Args(server), "--json"]);

        var last = System.Text.Json.Nodes.JsonNode.Parse(capture.GetCapturedOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1])!;
        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await Assert.That(last["code"]?.GetValue<string>()).IsEqualTo(SetupFailureCodes.MachineCredentialRejected);
        await Assert.That(last["step"]?.GetValue<string>()).IsEqualTo(SetupJsonReport.SignIn);
    }

    [Test]
    public async Task A_machine_credential_the_server_refuses_fails() {
        using var server = WorkOSServer("some-other-bearer");
        Mints(server, 200);
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(Machine(server)).HandleAsync(Args(server));

        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("401");
    }

    /// <summary>A minted token the server never confirmed is not a sign-in: with --skip-import nothing later would
    /// check it, and the script would be told it succeeded.</summary>
    [Test]
    [Arguments(500)]
    [Arguments(503)]
    [Arguments(429)]
    [Arguments(408)]
    [Arguments(302)]
    public async Task A_machine_credential_the_server_cannot_confirm_fails(int status) {
        using var server = WorkOSServer(MachineToken);
        Mints(server, 200);
        server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status));
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(Machine(server)).HandleAsync(Args(server));

        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("could not confirm");
        await Assert.That((await ActiveProfileAsync()).ServerUrl).IsNull();
    }

    [Test]
    public async Task A_machine_credential_the_server_does_not_answer_for_fails() {
        using var server = WorkOSServer(MachineToken);
        Mints(server, 200);
        server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(UnattendedCredential.ProbeTimeout + TimeSpan.FromSeconds(1)));
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(Machine(server)).HandleAsync(Args(server));

        await Assert.That(exit).IsEqualTo(SetupExit.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("could not confirm");
    }

    /// <summary>A server without the probe endpoint cannot be asked, and that is no reason to refuse a credential
    /// its token endpoint accepted.</summary>
    [Test]
    public async Task A_machine_credential_on_a_server_without_the_probe_is_accepted() {
        using var server = WorkOSServer(MachineToken);
        Mints(server, 200);
        server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(404));
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(Machine(server)).HandleAsync(Args(server));

        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(capture.GetCapturedError());
    }

    [Test]
    public async Task A_token_stored_for_this_server_is_used_instead_of_signing_in_again() {
        using var server = WorkOSServer("stored-bearer");
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root),
            """{"version":2,"active_profile":"default","profiles":{"default":{"server_url":""" + $"\"{server.Url!.TrimEnd('/')}\"" + "}}}");
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", new StoredTokens {
            AccessToken    = "stored-bearer",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.WorkOS,
            ServerUrl      = server.Url!.TrimEnd('/'),
        });
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(MachineAuth.None).HandleAsync(Args(server));

        var output = capture.GetCapturedOutput() + capture.GetCapturedError() + spectre.Text;
        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(output);
        await Assert.That(output).Contains("alice");
    }

    [Test]
    public async Task With_neither_setup_signs_a_person_in_as_before() {
        using var server = WorkOSServer("stored-bearer");
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        await Assert.That(async () => await Command(MachineAuth.None).HandleAsync(Args(server))).Throws<SignInAttempted>();
    }
}
