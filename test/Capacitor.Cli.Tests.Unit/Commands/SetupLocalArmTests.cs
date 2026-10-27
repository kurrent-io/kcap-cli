using TUnit.Core.Enums;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.PrDetection;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Spectre.Console;
using TUnit.Assertions.Enums;
using Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap setup --local</c> through <see cref="SetupCommand.HandleAsync"/>, over a real installer whose unit, server
/// and bundle are fakes. Console, the Spectre singleton, <c>KCAP_URL</c> and the interrupt seam are process-global.
/// </summary>
[NotInParallel, ExcludeOn(OS.Windows)]
internal sealed class SetupLocalArmTests : IDisposable {
    const string Version = "1.3.0";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];

    LocalRoot Root => field ??= LocalRoot.Resolve(null, Home);

    FakeLocalServer Server => field ??= new(_log, Root);

    /// <summary>The built-in model set the bundle offers and serves; none while empty. Filled before the first run.</summary>
    readonly Dictionary<string, byte[]> _modelFiles = [];

    FakeBundle Bundle => field ??= new(Tmp.PathTo("bundle.tar.gz"), Version) { ModelFiles = _modelFiles.Count > 0 ? _modelFiles : null };

    FakeOllama? _ollama;

    /// <summary>Answers nothing until a test makes it run, so no test reaches a real Ollama on this machine.</summary>
    FakeOllama Ollama => _ollama ??= new();

    FakeLocalServerService? _service;

    public void Dispose() => _ollama?.Dispose();

    [Before(Test)]
    public void WriteConfig() =>
        File.WriteAllText(AppConfig.GetConfigPath(Config.Root), """{"version":2,"active_profile":"default","profiles":{"default":{}}}""");

    /// <param name="wrap">Wraps <see cref="Server"/> as the installer's transport.</param>
    SetupCommand Command(Func<HttpMessageHandler, HttpMessageHandler>? wrap) {
        var factory = new FakeLocalInstallerFactory(() => {
            var service = _service = new FakeLocalServerService(_log, Home);
            var local   = new HttpClient(wrap?.Invoke(Server) ?? Server);

            return new LocalInstaller(Root, Config.Root, Home, service, Bundle.Client(), local, Bundle.Key, MacArm, TimeProvider.System,
                installer => [new LocalSetupRecovery(service, Home, local, TimeProvider.System, installer.Progress, installer.RolledForward)]) {
                Version     = Version,
                SocketLimit = int.MaxValue,
                Ollama      = Ollama.Client(),
            };
        });

        var http = new PlainHttpClientFactory();

        return new SetupCommand(
            Config.Root, Resolutions.None(Config.Root),
            AuthFixtures.NewTokenStore(Config.Root),
            new RecordingBrowser(), Home, TestHarnesses.Under(Home), new AgentsPaths(Home), new FixedCapacitorHttpClient(Server),
            new TenantProvisioningClient(new HttpClient(), ClientAgent.Cli),
            new AuthProviderDiscovery(http, TimeProvider.System), NoTelemetry.Facade, AuthEndpoints.Defaults,
            new FakeFacadeFactory(_ => throw new InvalidOperationException("a local setup signs in nowhere")),
            FakeImportRunner.Of(_ => throw new InvalidOperationException("must not run import")),
            FakeBackgroundImportSpawner.Running(), FakeHandoffAgentLauncher.Ran(),
            new ChosenServerHttp(Config.Root, Resolutions.None(Config.Root), ProfileOverrides.None, MachineAuth.None, NoLocalInstallation.Root),
            router: new GitProviderRouter(), workdir: new WorkingDirectory(Config.Directory), TimeProvider.System,
            TestBinaries.None, TestAccounts.None, TestPluginEnvironment.For(Home, Home.PathTo("plugin")), NoLocalInstallation.Root,
            factory) {
            PickHandoffVendor = _ => "Skip"
        };
    }

    Task<(int Exit, string[] Stdout, string Spectre, string Stderr)> Run(params string[] args) => RunWith(null, null, args);

    /// <param name="console">Reads the Spectre console the caller swapped in; null swaps in a capture here.</param>
    async Task<(int Exit, string[] Stdout, string Spectre, string Stderr)> RunWith(
            Func<HttpMessageHandler, HttpMessageHandler>? wrap, Func<string>? console, params string[] args) {
        using var noUrl   = EnvScope.Exclusive(ProfileOverrides.UrlVar, null);
        using var spectre = console is null ? new SpectreCapture() : null;
        using var capture = ConsoleOutput.StartFullCapture("\n");

        var exit = await Command(wrap).HandleAsync(args);

        return (exit, capture.GetCapturedOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries), console?.Invoke() ?? spectre!.Text,
            capture.GetCapturedError());
    }

    static string[] Events(string[] stdout) => [.. stdout.Select(l => JsonNode.Parse(l)!["event"]!.GetValue<string>())];

    static async Task AssertAllJson(string[] lines) {
        foreach (var line in lines) {
            var node = JsonNode.Parse(line);
            await Assert.That(node?["v"]?.GetValue<int>()).IsEqualTo(1).Because(line);
        }
    }

    Profile ActiveProfile() {
        var cfg = ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root));
        return cfg.Profiles[cfg.ActiveName];
    }

    [Test]
    public async Task Json_without_single_user_fails_with_answer_required_line() {
        var (exit, stdout, spectre, _) = await Run("setup", "--local", "--json", "--yes");

        await Assert.That(exit).IsEqualTo(SetupExit.Refused);
        await AssertAllJson(stdout);
        await Assert.That(spectre).IsEmpty();

        var last = JsonNode.Parse(stdout[^1])!;
        await Assert.That(last["event"]?.GetValue<string>()).IsEqualTo("failed");
        await Assert.That(last["code"]?.GetValue<string>()).IsEqualTo("answer_required");
        await Assert.That(last["message"]?.GetValue<string>()).Contains("--single-user");
        await Assert.That(_log).DoesNotContain("install");
    }

    [Test]
    public async Task Json_success_ends_with_committed_and_exit_0() {
        // The visibility step under --no-prompt would write org_public over it.
        File.WriteAllText(AppConfig.GetConfigPath(Config.Root),
            """{"version":2,"active_profile":"default","profiles":{"default":{"default_visibility":"private"}}}""");

        var (exit, stdout, spectre, _) = await Run("setup", "--local", "--json", "--single-user", "--yes");

        await Assert.That(exit).IsEqualTo(0);
        await AssertAllJson(stdout);
        await Assert.That(spectre).IsEmpty();

        var events = stdout.Select(l => JsonNode.Parse(l)!["event"]!.GetValue<string>()).ToArray();
        await Assert.That(events[0]).IsEqualTo("preflight_passed");
        await Assert.That(events).Contains("committing");

        var skipped = stdout.Select(l => JsonNode.Parse(l)!).Single(n => n["event"]!.GetValue<string>() == "embeddings_skipped");
        await Assert.That(skipped["code"]?.GetValue<string>()).IsEqualTo("ollama_not_found");
        await Assert.That(Array.IndexOf(events, "embeddings_skipped")).IsLessThan(Array.IndexOf(events, "starting"));

        var last = JsonNode.Parse(stdout[^1])!;
        await Assert.That(last["event"]?.GetValue<string>()).IsEqualTo("committed");
        await Assert.That(last["version"]?.GetValue<string>()).IsEqualTo(Version);

        var profile = ActiveProfile();
        await Assert.That(profile.ServerUrl).IsEqualTo(last["server_url"]!.GetValue<string>());
        await Assert.That(profile.DefaultVisibility).IsEqualTo("private");
        await Assert.That(profile.Daemon).IsNull();
    }

    /// <summary>Pins the report that clears the dashboard's install nudge: the app's wizard links to the dashboard
    /// straight after the install-only run, and a no-auth server gets the report without a bearer.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Committed_install_reports_cli_setup_without_a_bearer(bool wholeRun) {
        string? authorization = null;
        Server.Override = path => path == "/api/users/me/cli-setup" ? new(System.Net.HttpStatusCode.NoContent) : null;
        Server.OnCliSetupReport = request => authorization ??= request.Headers.Authorization?.ToString() ?? "";

        string[] args = wholeRun
            ? ["setup", "--local", "--json", "--no-prompt", "--single-user", "--yes", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags]
            : ["setup", "--local", "--json", "--single-user", "--yes"];

        var (exit, _, _, stderr) = await Run(args);

        await Assert.That(exit).IsEqualTo(wholeRun ? SetupExit.Done : 0).Because(stderr);
        await Assert.That(_log).Contains("POST /api/users/me/cli-setup").Because(stderr);
        await Assert.That(authorization).IsEqualTo("");
    }

    [Test]
    public async Task Json_with_no_prompt_reports_the_install_and_every_later_step() {
        var (exit, stdout, spectre, stderr) = await Run([
            "setup", "--local", "--json", "--no-prompt", "--single-user", "--yes", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags,
        ]);

        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(stderr);
        await AssertAllJson(stdout);
        await Assert.That(spectre).IsEmpty();
        await Assert.That(stderr).Contains("Step 2/6");

        var events = Events(stdout);
        await Assert.That(events[0]).IsEqualTo("step_started");
        await Assert.That(events).Contains("committed");
        await Assert.That(Array.IndexOf(events, "committed")).IsLessThan(Array.LastIndexOf(events, "step_started"));
        await Assert.That(events[^1]).IsEqualTo("done");
        await Assert.That(JsonNode.Parse(stdout[^1])!["server_url"]?.GetValue<string>()).IsEqualTo(ActiveProfile().ServerUrl);
    }

    [Test]
    public async Task Json_with_discover_fails_with_invalid_arguments_line() {
        var (exit, stdout, _, _) = await Run("setup", "--local", "--discover", "--json");

        await Assert.That(exit).IsEqualTo(SetupExit.Refused);
        await Assert.That(stdout.Length).IsEqualTo(1);

        var line = JsonNode.Parse(stdout[0])!;
        await Assert.That(line["code"]?.GetValue<string>()).IsEqualTo("invalid_arguments");
        await Assert.That(line["message"]?.GetValue<string>()).Contains("--discover");
    }

    [Test]
    public async Task Json_interrupt_before_the_commit_ends_with_cancelled_and_exits_130() {
        var exits    = new List<int>();
        var original = InteractiveLifetime.Exit;
        InteractiveLifetime.Exit = exits.Add;

        (int Exit, string[] Stdout, string Spectre, string Stderr) run;

        try {
            run = await RunWith(inner => new InterruptOnReady(inner), null, "setup", "--local", "--json", "--single-user", "--yes");
        } finally {
            InteractiveLifetime.Exit = original;
        }

        await AssertAllJson(run.Stdout);

        var last = JsonNode.Parse(run.Stdout[^1])!;
        await Assert.That(last["code"]?.GetValue<string>()).IsEqualTo("cancelled");
        await Assert.That(run.Stdout.Select(l => JsonNode.Parse(l)!["event"]!.GetValue<string>())).Contains("rolled_back");
        await Assert.That(exits).IsEquivalentTo([130]);
        await Assert.That(ActiveProfile().ServerUrl).IsNull();
    }

    /// <summary>The roll-forward ignores the interrupt and commits; the setup must report that commit rather than the
    /// cancellation a later step would observe.</summary>
    [Test]
    public async Task Json_interrupt_during_a_roll_forward_ends_with_committed_and_exits_130() {
        using (FaultPoints.Arm(point => { if (point == "setup:after-committing") throw new SimulatedCrash(); })) {
            var crashed = await Run("setup", "--local", "--json", "--single-user", "--yes");
            await Assert.That(crashed.Exit).IsEqualTo(1);
        }

        await Assert.That(LocalStateFiles.ReadJournal(Root) is StateRead<LifecycleJournalEntry>.Present).IsTrue();

        var exits    = new List<int>();
        var original = InteractiveLifetime.Exit;
        InteractiveLifetime.Exit = exits.Add;

        (int Exit, string[] Stdout, string Spectre, string Stderr) run;

        try {
            using var interrupt = FaultPoints.Arm(point => {
                if (point == "setup:after-registry") InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            });

            run = await Run("setup", "--local", "--json", "--single-user", "--yes");
        } finally {
            InteractiveLifetime.Exit = original;
        }

        await AssertAllJson(run.Stdout);
        await Assert.That(Events(run.Stdout)).Contains("recovered");
        await Assert.That(Events(run.Stdout)[^1]).IsEqualTo("committed").Because(string.Join('\n', run.Stdout));
        await Assert.That(exits).IsEquivalentTo([130]);
        await Assert.That(LocalStateFiles.ReadJournal(Root) is StateRead<LifecycleJournalEntry>.Absent).IsTrue();
    }

    [Test]
    public async Task No_prompt_without_yes_fails() {
        var (exit, _, _, stderr) = await Run("setup", "--local", "--single-user", "--no-prompt");

        await Assert.That(exit).IsEqualTo(SetupExit.Refused);
        await Assert.That(stderr).Contains("Pass --yes");
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(ActiveProfile().ServerUrl).IsNull();
    }

    [Test]
    public async Task Terminal_arm_hands_the_local_url_and_none_to_later_steps() {
        var (exit, stdout, spectre, stderr) = await Run([
            "setup", "--local", "--single-user", "--yes", "--no-prompt", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags,
        ]);

        await Assert.That(exit).IsEqualTo(0).Because(string.Join('\n', stdout) + spectre + stderr);

        var registry = (LocalStateFiles.ReadRegistry(Root) as StateRead<InstallRegistry>.Present)!.Value.Active!;
        var profile  = ActiveProfile();

        await Assert.That(profile.ServerUrl).IsEqualTo(registry.ServerUrl);
        await Assert.That(profile.AuthProvider).IsEqualTo(new AuthProviderStamp(AuthProvider.None, registry.ServerUrl));
        await Assert.That(string.Join('\n', stdout) + spectre).Contains("No sign-in needed for a local installation.");
    }

    /// <summary>A local installation has one user, so its sessions default to private, as the app's local path does.</summary>
    [Test]
    [Arguments(null, "private")]
    [Arguments("org_public", "org_public")]
    public async Task No_prompt_local_visibility_defaults_to_private(string? flag, string expected) {
        var (exit, stdout, spectre, stderr) = await Run([
            "setup", "--local", "--single-user", "--yes", "--no-prompt", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags,
            .. flag is null ? Array.Empty<string>() : ["--default-visibility", flag],
        ]);

        await Assert.That(exit).IsEqualTo(SetupExit.Done).Because(string.Join('\n', stdout) + spectre + stderr);
        await Assert.That(ActiveProfile().DefaultVisibility).IsEqualTo(expected);
    }

    [Test]
    public async Task Json_with_ollama_running_configures_the_default_model_before_the_first_start() {
        Ollama.Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);

        var (exit, stdout, _, _) = await Run("setup", "--local", "--json", "--single-user", "--yes");

        await Assert.That(exit).IsEqualTo(0);
        await AssertAllJson(stdout);

        var events = Events(stdout);
        await Assert.That(events).Contains("embeddings_ollama_found");
        await Assert.That(events).Contains("embeddings_model_ready");
        await Assert.That(Array.IndexOf(events, "embeddings_configured")).IsLessThan(Array.IndexOf(events, "starting"));

        var configured = stdout.Select(l => JsonNode.Parse(l)!).Single(n => n["event"]!.GetValue<string>() == "embeddings_configured");
        await Assert.That(configured["provider"]?.GetValue<string>()).IsEqualTo("ollama");
        await Assert.That(configured["model"]?.GetValue<string>()).IsEqualTo(OllamaClient.DefaultModel);

        await Assert.That(_service!.Installed!.Environment).Contains(new KeyValuePair<string, string>("Embedding__Provider", "ollama"));
        await Assert.That(_service.Installed.Environment)
            .Contains(new KeyValuePair<string, string>("Embedding__Ollama__Model", OllamaClient.DefaultModel));
    }

    [Test]
    public async Task Json_without_ollama_downloads_and_records_the_builtin_model_the_manifest_offers() {
        _modelFiles["model.onnx"] = [1, 2, 3, 4];
        _modelFiles["vocab.txt"]  = [5, 6];

        var (exit, stdout, _, stderr) = await Run("setup", "--local", "--json", "--single-user", "--yes");

        await Assert.That(exit).IsEqualTo(0).Because(string.Join('\n', stdout) + stderr);
        var events = Events(stdout);
        await Assert.That(events).Contains("embeddings_builtin_downloading");
        await Assert.That(Array.IndexOf(events, "embeddings_configured")).IsLessThan(Array.IndexOf(events, "starting"));

        var configured = stdout.Select(l => JsonNode.Parse(l)!).Single(n => n["event"]!.GetValue<string>() == "embeddings_configured");
        await Assert.That(configured["provider"]?.GetValue<string>()).IsEqualTo("builtin");
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Bundle.ModelSet));
    }

    [Test]
    public async Task Json_no_embeddings_leaves_ollama_alone() {
        Ollama.Running();

        var (exit, stdout, _, _) = await Run("setup", "--local", "--json", "--single-user", "--yes", "--no-embeddings");

        await Assert.That(exit).IsEqualTo(0);

        var skipped = stdout.Select(l => JsonNode.Parse(l)!).Single(n => n["event"]!.GetValue<string>() == "embeddings_skipped");
        await Assert.That(skipped["code"]?.GetValue<string>()).IsEqualTo("declined");
        await Assert.That(Ollama.Requests("/api/version")).IsEqualTo(0);
    }

    [Test]
    public async Task Json_rerun_after_no_embeddings_keeps_search_by_keyword() {
        Ollama.Running().Pulls("""{"status":"success"}""").Embeds(1024);
        await Assert.That((await Run("setup", "--local", "--json", "--single-user", "--yes", "--no-embeddings")).Exit).IsEqualTo(0);

        var (exit, stdout, _, _) = await Run("setup", "--local", "--json", "--single-user");

        await Assert.That(exit).IsEqualTo(0);
        var skipped = stdout.Select(l => JsonNode.Parse(l)!).Single(n => n["event"]!.GetValue<string>() == "embeddings_skipped");
        await Assert.That(skipped["code"]?.GetValue<string>()).IsEqualTo("declined");
        await Assert.That(Ollama.Requests("/api/pull")).IsEqualTo(0);
    }

    [Test]
    public async Task No_prompt_with_ollama_running_and_no_embedding_answer_uses_ollama() {
        Ollama.Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);

        var (exit, stdout, spectre, stderr) = await Run([
            "setup", "--local", "--single-user", "--yes", "--no-prompt", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags,
        ]);

        await Assert.That(exit).IsEqualTo(0).Because(string.Join('\n', stdout) + spectre + stderr);
        await Assert.That(LocalEmbeddingFile.Read(Root)?.Model).IsEqualTo(OllamaClient.DefaultModel);
    }

    [Test]
    public async Task No_prompt_with_an_embedding_model_flag_pulls_and_configures_it() {
        Ollama.Running().Pulls("""{"status":"success"}""").Embeds(768);

        var (exit, stdout, spectre, stderr) = await Run([
            "setup", "--local", "--single-user", "--yes", "--no-prompt", "--embedding-model", "nomic-embed-text", "--skip-import",
            .. SetupCommandTests.SkipAllAgentInstallFlags,
        ]);

        await Assert.That(exit).IsEqualTo(0).Because(string.Join('\n', stdout) + spectre + stderr);
        await Assert.That(string.Join('\n', stdout) + spectre).Contains("Semantic search uses nomic-embed-text through Ollama");
        await Assert.That(LocalEmbeddingFile.Read(Root)?.Model).IsEqualTo("nomic-embed-text");
    }

    [Test]
    public async Task Plain_setup_refuses_json() {
        var (exit, stdout, spectre, _) = await Run("setup", "--json");

        await Assert.That(exit).IsEqualTo(SetupExit.Refused);
        await Assert.That(stdout.Length).IsEqualTo(1);
        await Assert.That(spectre).IsEmpty();
        await Assert.That(JsonNode.Parse(stdout[0])!["code"]?.GetValue<string>()).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task Json_reuse_without_yes_ends_reused_then_committed() {
        var first = await Run("setup", "--local", "--json", "--single-user", "--yes");
        await Assert.That(first.Exit).IsEqualTo(0);

        var (exit, stdout, _, _) = await Run("setup", "--local", "--json", "--single-user");

        await Assert.That(exit).IsEqualTo(0).Because(string.Join('\n', stdout));
        await Assert.That(Events(stdout)[^2..]).IsEquivalentTo(["reused", "committed"], CollectionOrdering.Matching);
        await Assert.That(Events(stdout)).DoesNotContain("downloading");
    }

    /// <summary>The registry says reuse when the terminal reads the plan, and is gone by the time the installer
    /// rechecks it under the lease; skipping the question on a reuse must not count as a confirmed install.</summary>
    [Test]
    public async Task A_reuse_that_becomes_an_install_under_the_lease_needs_yes() {
        Directory.CreateDirectory(Root.Directory);
        LocalStateFiles.PublishRegistry(Root, new(new("0123456789abcdef0123456789abcdef", 9473, "http://localhost:9473", Version), []));

        var text     = new StringBuilder();
        var original = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(new TriggerWriter(text, "one user", () => File.Delete(Root.RegistryPath))),
        });

        (int Exit, string[] Stdout, string Spectre, string Stderr) run;

        try {
            run = await RunWith(null, text.ToString, "setup", "--local", "--single-user", "--no-prompt");
        } finally {
            AnsiConsole.Console = original;
        }

        await Assert.That(run.Exit).IsEqualTo(SetupExit.Refused);
        await Assert.That(run.Stderr).Contains("--yes");
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(Directory.Exists(Path.Combine(Root.Directory, "bin"))).IsFalse();
    }

    [Test]
    public async Task Terminal_decline_cancels_and_exits_0() {
        using var console = new ScriptedConsole().Line("n");

        var (exit, _, spectre, _) = await RunWith(null, () => console.Text, "setup", "--local");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(spectre).Contains("Setup cancelled.");
        await Assert.That(_log).DoesNotContain("install");
    }

    [Test]
    public async Task Terminal_interrupt_after_the_commit_exits_130_before_step_2() {
        var exits    = new List<int>();
        var original = InteractiveLifetime.Exit;
        InteractiveLifetime.Exit = exits.Add;

        (int Exit, string[] Stdout, string Spectre, string Stderr) run;

        try {
            using var interrupt = FaultPoints.Arm(point => {
                if (point == "setup:after-registry") InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            });

            run = await Run(["setup", "--local", "--single-user", "--yes", "--no-prompt", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags]);
        } finally {
            InteractiveLifetime.Exit = original;
        }

        await Assert.That(exits).IsEquivalentTo([130]);
        await Assert.That(run.Exit).IsEqualTo(130);
        await Assert.That(run.Spectre).Contains("Committed");
        await Assert.That(run.Spectre).DoesNotContain("Step 2/6");
        await Assert.That(ActiveProfile().AuthProvider).IsEqualTo(new AuthProviderStamp(AuthProvider.None, ActiveProfile().ServerUrl!));
    }

    /// <summary>The process exits as the deferral is disposed, so the closing line has to be on stdout by then: read
    /// at the moment <see cref="InteractiveLifetime.Exit"/> is called, not after the faked exit returns.</summary>
    [Test]
    public async Task Json_interrupt_after_the_commit_ends_with_cancelled_before_the_exit() {
        using var noUrl   = EnvScope.Exclusive(ProfileOverrides.UrlVar, null);
        using var spectre = new SpectreCapture();
        using var capture = ConsoleOutput.StartFullCapture("\n");

        string? atExit   = null;
        var     original = InteractiveLifetime.Exit;
        InteractiveLifetime.Exit = _ => atExit ??= capture.GetCapturedOutput();

        try {
            using var interrupt = FaultPoints.Arm(point => {
                if (point == "setup:after-registry") InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            });

            await Command(null).HandleAsync([
                "setup", "--local", "--json", "--no-prompt", "--single-user", "--yes", "--skip-import", .. SetupCommandTests.SkipAllAgentInstallFlags,
            ]);
        } finally {
            InteractiveLifetime.Exit = original;
        }

        var lines = atExit!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(Events(lines)).Contains("committed");

        var last = JsonNode.Parse(lines[^1])!;
        await Assert.That(last["event"]?.GetValue<string>()).IsEqualTo("failed");
        await Assert.That(last["code"]?.GetValue<string>()).IsEqualTo("cancelled");
    }

    /// <summary>Interrupts at the first readiness probe and holds it until the interrupt has cancelled the install,
    /// so the cancellation lands before the commit whatever the scheduling.</summary>
    sealed class InterruptOnReady(HttpMessageHandler inner) : DelegatingHandler(inner) {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.RequestUri!.AbsolutePath == "/healthz/ready") {
                InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
                await Task.Delay(Timeout.Infinite, ct);
            }

            return await base.SendAsync(request, ct);
        }
    }

    /// <summary>Runs <paramref name="onMarker"/> once, as soon as the text written so far contains
    /// <paramref name="marker"/>.</summary>
    sealed class TriggerWriter(StringBuilder text, string marker, Action onMarker) : StringWriter(text, CultureInfo.InvariantCulture) {
        bool _fired;

        public override void Write(char value) {
            base.Write(value);
            Check();
        }

        public override void Write(string? value) {
            base.Write(value);
            Check();
        }

        public override void Write(char[] buffer, int index, int count) {
            base.Write(buffer, index, count);
            Check();
        }

        void Check() {
            if (_fired || !GetStringBuilder().ToString().Contains(marker, StringComparison.Ordinal)) return;

            _fired = true;
            onMarker();
        }
    }
}
