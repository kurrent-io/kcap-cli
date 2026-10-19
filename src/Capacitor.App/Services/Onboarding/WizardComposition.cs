using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Services.Onboarding;

/// The façade's constructor arguments as data, so the composition root's choices — which sink,
/// which picker, whether a provisioner is armed, which before-commit hook — are inspectable.
internal sealed record WizardFacadeSpec(
    ConfigRoot                                                 Root,
    TokenStore                                                 TokenStore,
    IHttpClientFactory                                         HttpFactory,
    IAuthProxyClient                                           Proxy,
    GitHubOAuthClient                                          GitHub,
    WorkOSClient                                               WorkOS,
    string                                                     Profile,
    IAuthProgress                                              Progress,
    ITenantPicker                                              Picker,
    ITenantProvisioner?                                        Provisioner,
    CliTelemetry                                               Telemetry,
    AuthEndpoints                                              Endpoints,
    TimeProvider                                               Time,
    Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task> BeforeCommit,
    CommitPrecondition?                                        Precondition = null);

/// What wizard-first mode runs on: the shell, the sign-in driver the close path awaits, every step
/// (including ones the shell filtered out as inapplicable — the summary still names them), the
/// History step by name — its import outlives the wizard, and shutdown cancels it directly — the
/// daemon enabler, which runs beside the steps rather than as one, and the local install's controller,
/// whose child outlives the Install page.
internal sealed record WizardGraph(
    OnboardingViewModel ViewModel, WizardAuthService Auth, IReadOnlyList<IWizardStep> Steps,
    HistoryStepViewModel History, ConnectChoiceViewModel Connect, DaemonEnabler Daemon,
    LocalInstallController Install);

/// <summary>Everything wizard-first mode is composed from; the daemon-facing entries are factories so a call never lands on a stale daemon.</summary>
internal sealed record WizardGraphOptions(
    ConfigRoot                                                                   Root,
    TokenStore                                                                   TokenStore,
    IHttpClientFactory                                                           HttpFactory,
    IAuthProxyClient                                                             Proxy,
    GitHubOAuthClient                                                            GitHub,
    WorkOSClient                                                                 WorkOS,
    string                                                                       Profile,
    ConsentFlipClaims                                                            Claims,
    WizardBridges                                                                Bridges,
    Func<WizardFacadeSpec, Func<ConnectIntent, CancellationToken, Task<AuthResult>>> Operation,
    WizardLifecycleSurface                                                       Surface,
    Func<IKcapCli>                                                               ResolveCli,
    // Takes the already-resolved identity's daemon name — never re-resolves one of its own.
    Func<string, ILocalControlOps>                                               ResolveOps,
    // The daemon's OWN identity, nullable: an unreadable/invalid config reads as "not ready".
    Func<(string Profile, string Server, string DaemonName)?>                    ResolveIdentity,
    // The claims path's identity — deliberately the SEPARATE, literal ResolveConsentFlipIdentity
    // (its own doc comment justifies the literal read); never swapped for ResolveIdentity above.
    Func<(string Profile, string Server, string DaemonName)>                     ResolveConsentFlipIdentity,
    Func<MutationRequest, CancellationToken, Task<MutationOutcome>>              RunMutation,
    IDaemonObservation                                                           Observation,
    IAppStateStore                                                               AppState,
    ICliPathInstaller?                                                           ShimInstaller,
    IUrlOpener                                                                   UrlOpener,
    ILoginShellProbe                                                             Probe,
    Func<ILoginShellProbe, Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>>> DetectionFeed,
    string?                                                                      CliPath,
    bool                                                                         ShimApplicable,
    string?                                                                      ShimTarget,
    string?                                                                      DefaultDaemonName,
    TimeProvider                                                                 Time,
    CancellationToken                                                            ShutdownToken,
    LocalRoot                                                                    LocalRoot,
    // The local edition ships for macOS on Apple silicon only.
    bool                                                                         IsMacOs,
    bool                                                                         AlreadyAuthenticated = false,
    // The name the profile already carries, or null when none was ever saved.
    Func<string?>?                                                               ConfiguredDaemonName = null,
    // Names other machines' daemons hold on this account; best-effort, empty when unknown.
    Func<CancellationToken, Task<IReadOnlyDictionary<string, DaemonNameHolding>>>? HeldDaemonNames = null,
    // KCAP_PROFILE, which the app honours and the local installer does not.
    string?                                                                      ProfileOverride = null,
    // The app's one install controller, shared by every wizard it shows; null builds a graph-local one.
    LocalInstallController?                                                      Install = null);

/// The wizard half of the composition root, split out of App so it can be driven
/// with fakes: nothing here touches a daemon, a socket or the network until a step is used.
internal static class WizardComposition {
    /// Production bridges: one marshalling boundary (Avalonia's dispatcher in the app) and a
    /// provisioner built from the bridges' OWN sink, per WizardBridges' contract.
    internal static WizardBridges BuildBridges(
            Action<Action> post, TenantProvisioningClient provisioning, CliTelemetry telemetry,
            AuthEndpoints endpoints, TimeProvider time) =>
        new(post, telemetry, endpoints, progress => new WizardTenantProvisioner(
            provisioning, endpoints.SignupUrl, progress, telemetry, time));

    /// Production operation: the spec IS the façade's arguments, and WizardSignInOperation owns
    /// the intent→call map (paste adopts the server; create/discover run WorkOS discovery).
    internal static Func<ConnectIntent, CancellationToken, Task<AuthResult>> NewOperation(WizardFacadeSpec spec) =>
        WizardSignInOperation.For(new OnboardingFacade(
            spec.Root, spec.TokenStore, spec.HttpFactory, spec.Proxy, spec.GitHub, spec.WorkOS, spec.Progress,
            SystemBrowser.Instance, spec.Picker, spec.Provisioner, spec.Telemetry, spec.Endpoints,
            spec.Time, spec.BeforeCommit), spec.Profile, spec.Precondition);

    /// The ONE façade a wizard run signs in through — provisioner armed (a provisioner-less façade
    /// dead-ends "Create a workspace" at "ask your admin") and the consent-flip claim arming hook wired as
    /// before-commit, so a claim exists before anything durable is published.
    internal static Func<ConnectIntent, CancellationToken, Task<AuthResult>> BuildOperation(
            ConfigRoot root, TokenStore tokenStore, IHttpClientFactory httpFactory, IAuthProxyClient proxy,
            GitHubOAuthClient github, WorkOSClient workos,
            string profile, WizardBridges bridges, ConsentFlipClaims claims, TimeProvider time,
            Func<WizardFacadeSpec, Func<ConnectIntent, CancellationToken, Task<AuthResult>>> operation,
            CommitPrecondition? precondition = null) =>
        operation(new WizardFacadeSpec(
            root, tokenStore, httpFactory, proxy, github, workos, profile, bridges.Progress, bridges.Picker,
            bridges.Provisioner, bridges.Telemetry, bridges.Endpoints, time,
            WizardAuthService.ArmingHook(claims), precondition));

    internal static LocalInstallController NewInstallController(WizardGraphOptions options) {
        var cli = new LateBoundKcapCli(options.ResolveCli, options.CliPath);

        return new LocalInstallController(
            () => cli, options.LocalRoot, () => IsCommittedLocal(options), options.Bridges.Post,
            options.Bridges.Telemetry.Funnel);
    }

    // Read live: the profile, the journal and the user's choice all change during a run.
    static bool IsCommittedLocal(WizardGraphOptions options) =>
        !LocalSetupState.Pending(options.LocalRoot)
     && options.ResolveIdentity() is { } id
     && LocalEndpoints.Read(options.LocalRoot).Classify(id.Server) is LocalEndpointClass.Local;

    internal static WizardGraph BuildGraph(WizardGraphOptions options) {
        var claims = options.Claims;
        var cli    = new LateBoundKcapCli(options.ResolveCli, options.CliPath);
        var auth   = new WizardAuthService(BuildOperation(options.Root, options.TokenStore, options.HttpFactory, options.Proxy,
        options.GitHub, options.WorkOS, options.Profile, options.Bridges, claims, options.Time, options.Operation));

        var root = options.LocalRoot;
        bool CommittedLocal() => IsCommittedLocal(options);

        var install = options.Install ?? NewInstallController(options);
        bool Installed() => install.Outcome is LocalInstallOutcome.Installed;

        var edition = new EditionStepViewModel(
            options.Bridges.Telemetry.Funnel,
            () => LocalUnavailableReason(options.IsMacOs, options.Root, root),
            locked: () => Installed() || LocalSetupState.Pending(root) || CommittedLocal());
        bool OnLocalPath() => edition.Choice == EditionChoice.Local || LocalSetupState.Pending(root) || CommittedLocal();
        var installStep = new InstallStepViewModel(
            install,
            appProfile: () => options.ResolveIdentity()?.Profile ?? options.ProfileOverride,
            configActiveProfile: () => ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(options.Root), out var config)
                ? config.ActiveName
                : null,
            // A committed machine has nothing left to install.
            onLocalPath: () => OnLocalPath() && !CommittedLocal());
        var connect  = new ConnectChoiceViewModel();
        var signIn   = new SignInStepViewModel(
            auth, connect, options.Bridges, claims, options.AppState, options.UrlOpener, time: options.Time,
            passThrough: OnLocalPath);
        var committedLocalAtStart = CommittedLocal();
        if (options.AlreadyAuthenticated && !committedLocalAtStart) signIn.RestoreCommitted();
        var pathFix  = options.ShimApplicable && options.ShimInstaller is { } installer
            ? new PathFixViewModel(installer, options.AppState, options.ShimTarget)
            : null;
        // The name persists to the same fresh identity the enabler gates on, falling back to ActiveProfile.
        var machineName = new MachineNameViewModel(options.Root, options.DefaultDaemonName, () => options.ResolveIdentity()?.Profile);
        var machine     = MachineLabel(Environment.MachineName);
        // ONE detection feed for both vendor steps: two would probe the login shell twice for the
        // same answer, and the two steps' vendor lists could then disagree.
        var detect = options.DetectionFeed(options.Probe);
        var offers = new HarnessOfferStore(options.Root, options.Time);
        var harnesses = new HarnessesStepViewModel(
            cli, detect,
            declined: () => AgentVendors.All.Where(v => offers.Load().Entry(v.Id)?.Declined == true).Select(v => v.Id).ToHashSet(),
            stampOffered: ids => offers.StampOffered(ids, options.Time.GetUtcNow()),
            options.Root, pathFix,
            ct => options.Probe.SetVariablesAsync(HarnessesStepViewModel.ProviderKeys, ct),
            machine,
            () => options.ResolveIdentity()?.Profile, options.ShutdownToken,
            onLocalPath: OnLocalPath);
        var history = new HistoryStepViewModel(
            cli, () => harnesses.Recording, options.Bridges.Post, machine, options.Time);
        var daemon = new DaemonEnabler(
            cli, options.RunMutation,
            // Gated on a committed sign-in or local install and resolved fresh per call, never the startup-cached profile.
            () => Connected() ? options.ResolveIdentity() : null,
            options.Observation,
            // The step's own gate above keeps the socket un-dialed: a null resolution never reaches ResolveOps.
            new LateBoundLocalControlOps(() => options.ResolveOps(options.ResolveIdentity() is { } id
                ? id.DaemonName
                : options.DefaultDaemonName ?? "daemon")),
            claims,
            options.ResolveConsentFlipIdentity, options.Surface, options.Probe.TerminalPathAsync, options.Time,
            machineName,
            ct => PickDaemonNameAsync(options.ConfiguredDaemonName?.Invoke(), options.HeldDaemonNames, ct));

        // Not a question: the daemon is part of setup, and enabled the moment there is an identity to enable.
        signIn.WhenAnyValue(x => x.Satisfied)
            .Where(satisfied => satisfied)
            .Subscribe(signedIn => _ = daemon.EnsureAsync(options.ShutdownToken));
        // Only the settled outcome counts: a progress line is not an installed server.
        void OnInstallSettled(LocalInstallOutcome outcome) {
            if (outcome is LocalInstallOutcome.Installed) _ = daemon.EnsureAsync(options.ShutdownToken);
        }
        install.Settled += OnInstallSettled;
        if (committedLocalAtStart) _ = daemon.EnsureAsync(options.ShutdownToken);

        // Read on every entry, so a Back-then-forward re-render sees each step's current state.
        var done = new DoneStepViewModel(() => {
            var recording = harnesses.Rows.Where(r => r is { Record: true, Succeeded: true }).ToList();
            var local = OnLocalPath();

            return new DoneFacts(
                [.. recording.Select(r => r.Label)],
                recording.Any(r => r.Id == HarnessId.Codex),
                harnesses.PathHazard,
                history.Run,
                daemon.Satisfied,
                machine,
                !local && Connected() ? options.ResolveIdentity()?.Server : null,
                daemon.Problem,
                local,
                local ? LocalEndpoints.Read(root).Active?.ServerUrl : null);
        }, options.UrlOpener);
        IWizardStep[] steps = [edition, installStep, signIn, harnesses, history, done];

        var startAt = LocalSetupState.Pending(root) ? WizardStepId.Install
            : options.AlreadyAuthenticated || committedLocalAtStart ? WizardStepId.Harnesses
            : (WizardStepId?)null;
        var canFinishLater = edition.WhenAnyValue(x => x.Choice).Select(_ => Unit.Default)
            .Merge(install.WhenAnyValue(x => x.Outcome).Select(_ => Unit.Default))
            .Select(_ => !OnLocalPath() || Installed() || CommittedLocal());
        var wizard = new OnboardingViewModel(steps, options.ShutdownToken, options.Surface, startAt, canFinishLater);
        // The controller outlives this graph: a later wizard over it must not drive this one's enabler.
        wizard.CloseRequested += () => {
            install.Settled -= OnInstallSettled;
            installStep.Detach();
        };

        return new WizardGraph(wizard, auth, steps, history, connect, daemon, install);

        bool Connected() => signIn.Satisfied || Installed() || CommittedLocal();
    }

    /// Why this machine cannot take the local edition, or null when it can. A cloud profile anywhere
    /// in the config is the CLI's own refusal, so the card says so before the install would.
    internal static string? LocalUnavailableReason(bool isMacOs, ConfigRoot config, LocalRoot root) {
        if (!isMacOs) return EditionStepViewModel.UnsupportedPlatform;

        return ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var profiles)
            && LocalSetupState.HasForeignServerProfile(profiles, LocalEndpoints.Read(root))
            ? EditionStepViewModel.CloudProfilePresent
            : null;
    }

    /// The configured name, or this machine's, moved to the first free numbered variant when another
    /// machine already holds it. Null when the configured name can stay.
    internal static async Task<string?> PickDaemonNameAsync(
            string? configured,
            Func<CancellationToken, Task<IReadOnlyDictionary<string, DaemonNameHolding>>>? heldDaemonNames,
            CancellationToken ct) {
        var held = heldDaemonNames is null
            ? FrozenDictionary<string, DaemonNameHolding>.Empty
            : await heldDaemonNames(ct).ConfigureAwait(true);
        var name = DaemonNameHolder.FirstFree(
            string.IsNullOrWhiteSpace(configured) ? DaemonNameHolder.MachineDefault() : configured, held);

        return name == configured ? null : name;
    }

    /// The machine's own name as a person would say it: no ".local", lower case.
    internal static string MachineLabel(string machineName) {
        var name = machineName.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? machineName[..^6] : machineName;

        return name.ToLowerInvariant();
    }
}
