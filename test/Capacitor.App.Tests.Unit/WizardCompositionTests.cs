using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Telemetry;
using AppUnderTest = Capacitor.App.App;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// End-to-end-shaped composition flows: the REAL WizardComposition.BuildGraph, the REAL step view
/// models and the REAL OnboardingViewModel — only externals (HTTP, process spawns, the daemon
/// socket) are scripted. WizardStartupTests already pins the composition SEAMS in isolation
/// (adoptServer:true on Paste via <c>A_pasted_server_is_adopted_and_the_arming_hook_runs_before_the_commit</c>,
/// discovery through the auth proxy via <c>Workos_discovery_routes_through_the_auth_proxy</c>,
/// and beforeCommit==ArmingHook via <c>The_facades_beforeCommit_hook_arms_a_durable_claim_per_identity</c>
/// plus WizardAuthServiceTests) — those are not duplicated here.
/// </summary>
static class WizardCompositionFixtures {
    /// Answers every request with a None-provider /auth/config body, mirroring
    /// WizardStartupResolutionTests' StubAuthHandler — a Paste sign-in commits with no further round trip.
    internal sealed class NoneProviderAuthHandler : HttpMessageHandler {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"provider":"None"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    internal static string ConfigPath(ConfigRoot root) => AppConfig.GetConfigPath(root);

    internal static void WriteConfig(ConfigRoot root, ProfileConfig config) =>
        File.WriteAllText(ConfigPath(root), JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));
}

/// (a) Fresh-machine happy path: a None-provider Paste sign-in through the REAL façade (the only
/// scripted external is the /auth/config HTTP call), driven through the REAL step VMs and the
/// REAL OnboardingViewModel all the way to the Done summary — with no kcap CLI resolved anywhere
/// in the composition, the fresh-machine shape.
///
/// [NotInParallel]: the process-global headless session, since composing the wizard constructs
/// ReactiveUI VMs.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionHappyPathTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ProfileName = "acme";
    const string ServerUrl   = "https://acme.example";

    [Test]
    public async Task Resuming_an_authenticated_setup_starts_at_connections_without_signing_in_again() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { AlreadyAuthenticated = true });
            await graph.ViewModel.PendingEnterForTesting;
            await Assert.That(graph.ViewModel.Current.Id).IsEqualTo(WizardStepId.Harnesses);
            await Assert.That(graph.Steps.OfType<SignInStepViewModel>().Single().Satisfied).IsTrue();
            await Assert.That(graph.Auth.Current).IsNull();
        });
    }

    [Test]
    public async Task Resuming_an_authenticated_setup_enables_the_daemon_without_a_step() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            new ServiceSnapshot("daemon-a", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false));

        var (requests, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { AlreadyAuthenticated = true });
            await graph.Daemon.EnsureAsync(CancellationToken.None); // waits out the ensure the build kicked

            return (harness.Lane.Requests.ToList(), graph.Daemon.Satisfied);
        });

        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(satisfied).IsTrue();
    }

    [Test]
    public async Task The_done_page_reports_the_install_once_the_daemon_runs_and_only_then_opens() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            new ServiceSnapshot("daemon-a", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false));
        bool? daemonRanFirst = null;

        var readiness = await AvaloniaSession.DispatchAsync(async () => {
            WizardGraph? graph = null;
            graph = WizardComposition.BuildGraph(harness.Options() with {
                AlreadyAuthenticated = true,
                ReportCliSetup = _ => {
                    daemonRanFirst = graph!.Daemon.Satisfied;
                    return Task.FromResult(true);
                },
            });
            var done = graph.Steps.OfType<DoneStepViewModel>().Single();
            await done.OnEnterAsync(CancellationToken.None);

            return await done.WhenAnyValue(x => x.Readiness).FirstAsync(r => r != WorkspaceReadiness.Preparing).ToTask();
        });

        await Assert.That(readiness).IsEqualTo(WorkspaceReadiness.Ready);
        await Assert.That(daemonRanFirst).IsTrue();
    }

    [Test]
    public async Task The_done_page_does_not_report_an_install_whose_daemon_cannot_run() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.Cli.CliPath = null;
        var reports = 0;

        var readiness = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with {
                AlreadyAuthenticated = true,
                ReportCliSetup = _ => {
                    reports++;
                    return Task.FromResult(true);
                },
            });
            var done = graph.Steps.OfType<DoneStepViewModel>().Single();
            await done.OnEnterAsync(CancellationToken.None);

            return await done.WhenAnyValue(x => x.Readiness).FirstAsync(r => r != WorkspaceReadiness.Preparing).ToTask();
        });

        await Assert.That(readiness).IsEqualTo(WorkspaceReadiness.DaemonNotRunning);
        await Assert.That(reports).IsEqualTo(0);
    }

    [Test]
    public async Task A_configured_name_another_machine_holds_is_saved_as_its_free_variant_before_the_install() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig { ActiveProfile = "default", Profiles = new() { ["default"] = new Profile() } });
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            new ServiceSnapshot("daemon-a", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false));
        var options = harness.Options() with {
            AlreadyAuthenticated = true,
            HeldDaemonNames = _ => Task.FromResult<IReadOnlyDictionary<string, DaemonNameHolding>>(
                new Dictionary<string, DaemonNameHolding> { ["daemon-a"] = new("linux", "1.0.0") }),
        };

        var installs = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(options);
            await graph.Daemon.EnsureAsync(CancellationToken.None);

            return harness.Lane.Requests.Count;
        });

        var config = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(config.Profiles["default"].Daemon?.Name).IsEqualTo("daemon-a-1");
        await Assert.That(installs).IsEqualTo(1);
    }

    [Test]
    public async Task Without_a_sign_in_the_graph_never_reads_the_daemon_or_its_name() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var nameReads = 0;
        harness.ConfiguredDaemonName = () => {
            nameReads++;
            return "daemon-a";
        };

        await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
        });

        await Assert.That(nameReads).IsEqualTo(0);
        await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
        await Assert.That(harness.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Fresh_machine_paste_sign_in_reaches_a_correct_done_summary() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = new Profile() } });

        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.CliPath        = null; // no kcap CLI resolved anywhere on this fresh machine
        harness.Cli.CliPath    = null;
        harness.ShimTarget     = null;
        harness.ShimApplicable = false;
        harness.Identity       = (ProfileName, ServerUrl, "daemon-a");
        harness.Profile        = ProfileName;
        harness.ConfiguredDaemonName = () => null;
        using var authHandler = new WizardCompositionFixtures.NoneProviderAuthHandler();

        var options = harness.Options() with {
            HttpFactory = new PlainHttpClientFactory(authHandler),
            Proxy       = new AuthProxyClient(new HttpClient(authHandler, disposeHandler: false), TimeProvider.System),
            Operation = spec => WizardSignInOperation.For(new OnboardingFacade(
                spec.Root, spec.TokenStore, spec.HttpFactory, spec.Proxy, spec.GitHub, spec.WorkOS, spec.Progress,
                new RecordingBrowser(), spec.Picker, null, spec.Telemetry, spec.Endpoints, TimeProvider.System,
                spec.BeforeCommit), spec.Profile),
        };

        var summary = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(options);
            await graph.ViewModel.PendingEnterForTesting;

            var connect = graph.Connect;
            connect.Choice          = ConnectChoice.Paste;
            connect.ServerInputText = ServerUrl;
            await Assert.That(connect.Validate()).IsTrue(); // a valid intent is staged before sign-in

            await graph.Steps.OfType<EditionStepViewModel>().Single().SignInCommand.Execute().ToTask(); // Edition -> Sign-in
            await graph.ViewModel.PendingAdvanceForTesting;

            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(signIn.Satisfied).IsTrue();
            await Assert.That(authHandler.Requests.Any(r => r.Contains("/auth/config"))).IsTrue();
            await Assert.That(harness.Claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo([ProfileName]);

            await graph.ViewModel.NextCommand.Execute().ToTask(); // Sign-in -> Harnesses
            await Assert.That(graph.ViewModel.CanGoNext).IsFalse();
            graph.Steps.OfType<HarnessesStepViewModel>().Single().Visibility = "private";
            await graph.ViewModel.NextCommand.Execute().ToTask(); // Harnesses -> History (persists; no CLI, nothing installable)
            await graph.ViewModel.NextCommand.Execute().ToTask(); // History -> Done (nothing to import)
            await graph.Daemon.EnsureAsync(CancellationToken.None); // waits out the ensure sign-in kicked

            return (graph.ViewModel.Current.Id, graph.Daemon.Row, graph.Daemon.Satisfied);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(summary.Id).IsEqualTo(WizardStepId.Done);
        await Assert.That(summary.Row).IsEqualTo(DaemonRow.CliMissing);
        await Assert.That(summary.Satisfied).IsFalse();

        var config = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(config.Profiles[ProfileName].ServerUrl).IsEqualTo(ServerUrl);
        await Assert.That(config.Profiles[ProfileName].AuthProvider?.Provider).IsEqualTo(AuthProvider.None);
        await Assert.That(config.Profiles[ProfileName].DefaultVisibility).IsEqualTo("private");
        // No name was ever saved, so the machine's own is.
        await Assert.That(config.Profiles[ProfileName].Daemon?.Name).IsEqualTo(DaemonNameHolder.MachineDefault());

        // No daemon touchpoint reached with no CLI resolved: no lane traffic, no IPC, no CLI spawns.
        await Assert.That(harness.Lane.Requests).IsEmpty();
        await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
        await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
        await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
        await Assert.That(harness.Cli.PluginInstallCallCount).IsEqualTo(0);
        await Assert.That(harness.Cli.ImportCallCount).IsEqualTo(0);
    }
}

/// The name the enabler saves before it installs: the configured one unless another machine holds it.
public class WizardDaemonNamePickTests {
    static Func<CancellationToken, Task<IReadOnlyDictionary<string, DaemonNameHolding>>> Held(params string[] names) =>
        _ => Task.FromResult<IReadOnlyDictionary<string, DaemonNameHolding>>(
            names.ToDictionary(n => n, _ => new DaemonNameHolding("linux", "1.0.0")));

    [Test]
    public async Task A_configured_name_nobody_else_holds_is_kept() {
        var picked = await WizardComposition.PickDaemonNameAsync("studio", Held("laptop"), CancellationToken.None);

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task A_configured_name_another_machine_holds_moves_to_the_first_free_variant() {
        var picked = await WizardComposition.PickDaemonNameAsync("studio", Held("studio", "studio-1"), CancellationToken.None);

        await Assert.That(picked).IsEqualTo("studio-2");
    }

    [Test]
    public async Task Without_a_configured_name_the_machines_own_is_picked() {
        var picked = await WizardComposition.PickDaemonNameAsync(null, heldDaemonNames: null, CancellationToken.None);

        await Assert.That(picked).IsEqualTo(DaemonNameHolder.MachineDefault());
    }

    [Test]
    public async Task Without_a_configured_name_a_held_machine_name_moves_to_its_first_free_variant() {
        var machine = DaemonNameHolder.MachineDefault();

        var picked = await WizardComposition.PickDaemonNameAsync(" ", Held(machine), CancellationToken.None);

        await Assert.That(picked).IsEqualTo($"{machine}-1");
    }
}

/// The Paste arm is the one the re-auth dialog runs, and it is the only arm that can carry a
/// precondition: a mismatch can only be refused if the operation forwarded it to LoginAsync.
public class WizardSignInOperationPreconditionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ProfileName = "acme";
    const string ServerUrl   = "https://acme.kcap.ai";

    [Test]
    public async Task A_paste_sign_in_is_refused_when_the_profile_does_not_name_the_expected_server() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig {
                ActiveProfile = ProfileName,
                Profiles      = new() { [ProfileName] = new Profile { ServerUrl = ServerUrl } }
            });

        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var operation = WizardSignInOperation.For(
            AuthFixtures.NewFacade(Config.Root, new RecordingAuthProgress(), handler), ProfileName,
            new CommitPrecondition.ExpectServer("https://elsewhere.example"));

        var result = await operation(new ConnectIntent.Paste(ServerUrl), CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(File.Exists(Config.PathTo("tokens", $"{ProfileName}.json"))).IsFalse();
    }

    /// The paste path counts sign-ins as the CLI does, so desktop and CLI funnels compare.
    [Test]
    [Arguments("committed", "cli_setup_signin_completed")]
    [Arguments("committed-none", "")]
    [Arguments("denied", "cli_setup_signin_failed")]
    [Arguments("unreachable", "")]
    [Arguments("other", "")]
    [Arguments("unsaved", "")]
    [Arguments("cancelled", "")]
    public async Task A_paste_outcome_reaches_the_funnel_as_the_cli_counts_it(string outcome, string expected) {
        var probe = TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl), Config.Root);
        TelemetryTestGuards.AssertEnabled("app", Config.Root, probe.Telemetry);
        AuthResult result = outcome switch {
            "committed"      => new AuthResult.Committed(ProfileName, ServerUrl, AuthProvider.GitHubApp, "ana", []),
            "committed-none" => new AuthResult.Committed(ProfileName, ServerUrl, AuthProvider.None, null, []),
            "unsaved"        => new AuthResult.Committed(ProfileName, ServerUrl, AuthProvider.GitHubApp, "ana", [], CredentialSaved: false),
            "denied"         => new AuthResult.Failed("denied", AuthFailureReason.SigninDenied),
            "unreachable"    => new AuthResult.Failed("unreachable", AuthFailureReason.Unreachable),
            "other"          => new AuthResult.Failed("other"),
            _                => new AuthResult.Cancelled(),
        };

        WizardSignInOperation.Recorded(result, probe.Funnel);

        await Assert.That(string.Join(",", probe.Names.Where(n => n.StartsWith("cli_setup_signin", StringComparison.Ordinal)))).IsEqualTo(expected);
    }
}

/// (b) Abandon before sign-in: staging a valid Connect intent and closing WITHOUT ever calling
/// Begin must leave nothing durable — no claim, no lane traffic, no CLI spawn, and the config
/// file untouched.
///
/// [NotInParallel]: same shared resources as WizardCompositionHappyPathTests, for the same reason.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionAbandonTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ProfileName = "acme";

    [Test]
    public async Task Staging_connect_input_and_closing_without_signing_in_writes_nothing() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = new Profile() } });
        var configBefore = await File.ReadAllTextAsync(WizardCompositionFixtures.ConfigPath(Config.Root));

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            var connect = graph.Connect;
            connect.Choice          = ConnectChoice.Paste;
            connect.ServerInputText = "https://acme.example";
            await Assert.That(connect.Validate()).IsTrue(); // a valid intent is staged — Begin is never called

            graph.ViewModel.RequestClose();
            await AppUnderTest.HandoffAfterWizardAsync(
                    graph.Auth, graph.Install, () => Task.CompletedTask, TimeSpan.FromSeconds(5), new OutcomeChannel(), TimeProvider.System)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.InstallVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.StartVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.PluginInstallCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.ImportCallCount).IsEqualTo(0);

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(await File.ReadAllTextAsync(WizardCompositionFixtures.ConfigPath(Config.Root))).IsEqualTo(configBefore);
    }
}

/// (c) Sign-in commit -> claims armed -> relaunch fixture: a claim armed by the wizard's own
/// composition (the REAL ArmingHook, wired exactly as WizardComposition.BuildGraph wires it) must
/// still be found and applied by a FRESH ConsentFlipCoordinator over the SAME durable claims file —
/// modelling a wizard session followed by the normal app's next startup, never a shared in-memory instance.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionRelaunchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task A_claim_armed_during_the_wizard_is_applied_by_a_relaunched_coordinator() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var canonicalServer = ServerIdentity.Canonicalize("https://acme.example")!;
        const string profileName = "acme";
        const string daemonName  = "daemon-a";

        // Scripted rather than the full network-backed façade: this proves the RELAUNCH seam, not
        // the boundary itself (already pinned by WizardStartupTests' beforeCommit-wiring tests).
        // The hook invoked here IS the composition's own WizardFacadeSpec.BeforeCommit.
        harness.Operation = async (_, ct) => {
            await harness.Spec!.BeforeCommit([new AuthIdentity(profileName, canonicalServer)], ct);
            return new AuthResult.Committed(profileName, canonicalServer, AuthProvider.None, null, []);
        };

        var result = await AvaloniaSession.DispatchAsync(async () => {
            var graph   = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Paste("https://acme.example"));
            return await attempt.Result;
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(harness.Claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo([profileName]);

        // The relaunch: a fresh ConsentFlipClaims instance over the SAME file, and a fresh
        // coordinator — never the wizard's own claims object or WizardAuthService.
        var relaunchClaims = new ConsentFlipClaims(harness.ClaimsRoot);
        var client   = new FakeDaemonClientService();
        var ops      = new ScriptedLocalControlOps();
        var surface  = new FakeLifecycleSurface();
        var appState = new WizardFixtures.NoopAppStateStore();
        var coordinator = new ConsentFlipCoordinator(
            client, ops, relaunchClaims, () => (profileName, canonicalServer, daemonName), surface, appState,
            CancellationToken.None);

        ops.QueueGet(new ConsentPolicyDto("allow", 30, []));
        ops.QueuePutV2(true, null);

        coordinator.Start();
        client.StatusSubject.OnNext(new AttachStatus(
            AttachState.Connected, null, [ConsentFlipCoordinator.ConsentV3Capability]));

        await WizardFixtures.WaitUntilAsync(
            () => relaunchClaims.Pending().Count == 0, what: "the relaunched coordinator to consume the claim");

        await Assert.That(ops.PutV2Calls).IsEqualTo(1);
        var put = ops.PutV2Payloads[0];
        await Assert.That(put.ExpectedName).IsEqualTo(daemonName);
        await Assert.That(put.ExpectedServerUrl).IsEqualTo(canonicalServer);
        await Assert.That(put.Policy.Default).IsEqualTo("prompt");
        // Same durable file: the wizard's own (now-stale) claims handle agrees the claim is gone.
        await Assert.That(harness.Claims.Pending()).IsEmpty();
    }
}
