using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Services.Onboarding;

/// The façade's constructor arguments as data, so the composition root's choices — which sink,
/// which picker, which before-commit hook — are inspectable.
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
    CliTelemetry                                               Telemetry,
    AuthEndpoints                                              Endpoints,
    TimeProvider                                               Time,
    Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task> BeforeCommit,
    Func<DiscoveredTenant, string, CancellationToken, Task<WorkspaceAnswer>> ProbeWorkspace,
    CommitPrecondition?                                        Precondition = null);

/// What wizard-first mode runs on: the shell, the sign-in driver the close path awaits, every step
/// (including ones the shell filtered out as inapplicable — the summary still names them), the
/// History step by name — its import outlives the wizard, and shutdown cancels it directly — the
/// daemon enabler, which runs beside the steps rather than as one, and the local install's controller,
/// whose child outlives the Install page.
internal sealed record WizardGraph(
    OnboardingViewModel ViewModel, WizardAuthService Auth, IReadOnlyList<IWizardStep> Steps,
    HistoryStepViewModel History, ConnectChoiceViewModel Connect, DaemonEnabler Daemon,
    LocalInstallController Install, PathFixViewModel? PathFix = null);

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
    // The built-in model set the stack setup will run offers, or null.
    Func<CancellationToken, Task<LocalEmbeddingModelManifest?>>                  BuiltinOffer,
    string?                                                                      CliPath,
    bool                                                                         ShimApplicable,
    string?                                                                      ShimTarget,
    string?                                                                      DefaultDaemonName,
    TimeProvider                                                                 Time,
    CancellationToken                                                            ShutdownToken,
    LocalRoot                                                                    LocalRoot,
    // The local edition ships for macOS on Apple silicon only.
    bool                                                                         LocalEditionSupported,
    bool                                                                         AlreadyAuthenticated = false,
    // The name the profile already carries, or null when none was ever saved.
    Func<string?>?                                                               ConfiguredDaemonName = null,
    // Names other machines' daemons hold on this account; best-effort, empty when unknown.
    Func<CancellationToken, Task<IReadOnlyDictionary<string, DaemonNameHolding>>>? HeldDaemonNames = null,
    // KCAP_PROFILE, which the app honours and the local installer does not.
    string?                                                                      ProfileOverride = null,
    // The app's one install controller, shared by every wizard it shows; null builds a graph-local one.
    LocalInstallController?                                                      Install = null,
    // Null, or already acknowledged, leaves the Privacy page out of the run.
    UsageDataNotice?                                                             UsageNotice = null,
    // Tells the configured server this machine has kcap installed; true once it accepted.
    Func<CancellationToken, Task<bool>>?                                         ReportCliSetup = null,
    // The visibility answers the signed-in tenant can honour; null offers every answer.
    Func<CancellationToken, Task<VisibilityOptionsResponse?>>?                   ReadVisibilityOptions = null,
    // Brings any other kcap the local installer would refuse up to date; null finds nothing to update.
    Func<CancellationToken, Task<ClientRepairOutcome>>?                          RepairClients = null,
    // Opens a support email with this report as its body; null offers no support email.
    Func<string, FeedbackMailOutcome>?                                           MailSupport = null,
    // This app sent the user to kcap.ai's sign-up and no sign-in has committed since.
    bool                                                                         ResumeWebSignUp = false);

/// The wizard half of the composition root, split out of App so it can be driven
/// with fakes: nothing here touches a daemon, a socket or the network until a step is used.
internal static class WizardComposition {
    /// How long after setup opened kcap.ai's sign-up a relaunch still opens on sign-in.
    internal static readonly TimeSpan WebSignUpResumeWindow = TimeSpan.FromDays(1);

    /// A sign-up this app opened in the browser, recent enough that a relaunch is the user coming back
    /// with a workspace to sign in to.
    internal static bool ResumesWebSignUp(AppState state, DateTimeOffset now) =>
        state.WebSignUpOpenedAt is { } opened && now >= opened && now - opened <= WebSignUpResumeWindow;

    /// Production bridges: one marshalling boundary, Avalonia's dispatcher in the app.
    internal static WizardBridges BuildBridges(Action<Action> post, CliTelemetry telemetry, AuthEndpoints endpoints) =>
        new(post, telemetry, endpoints);

    /// Production operation: the spec IS the façade's arguments, and WizardSignInOperation owns
    /// the intent→call map (paste adopts the server; discover runs WorkOS discovery). No provisioner:
    /// a workspace is created by kcap-web's sign-up in the browser, so discovery finding none ends there.
    internal static Func<ConnectIntent, CancellationToken, Task<AuthResult>> NewOperation(WizardFacadeSpec spec) =>
        WizardSignInOperation.For(new OnboardingFacade(
            spec.Root, spec.TokenStore, spec.HttpFactory, spec.Proxy, spec.GitHub, spec.WorkOS, spec.Progress,
            SystemBrowser.Instance, spec.Picker, null, spec.Telemetry, spec.Endpoints,
            spec.Time, spec.BeforeCommit) { ProbeWorkspace = spec.ProbeWorkspace }, spec.Profile, spec.Precondition,
            spec.Telemetry.Funnel);

    /// The ONE façade a wizard run signs in through, with the consent-flip claim arming hook wired as
    /// before-commit, so a claim exists before anything durable is published.
    internal static Func<ConnectIntent, CancellationToken, Task<AuthResult>> BuildOperation(
            ConfigRoot root, TokenStore tokenStore, IHttpClientFactory httpFactory, IAuthProxyClient proxy,
            GitHubOAuthClient github, WorkOSClient workos,
            string profile, WizardBridges bridges, ConsentFlipClaims claims, TimeProvider time,
            Func<WizardFacadeSpec, Func<ConnectIntent, CancellationToken, Task<AuthResult>>> operation,
            CommitPrecondition? precondition = null) =>
        operation(new WizardFacadeSpec(
            root, tokenStore, httpFactory, proxy, github, workos, profile, bridges.Progress, bridges.Picker,
            bridges.Telemetry, bridges.Endpoints, time,
            WizardAuthService.ArmingHook(claims),
            ProbeWorkspace(httpFactory, bridges.Endpoints, bridges.Progress.ProvisioningWait, bridges.Progress.ProvisioningFailed, time),
            precondition));

    internal static readonly TimeSpan ProvisioningPollInterval = TimeSpan.FromSeconds(5);

    /// How long sign-in waits on a workspace kcap.ai is still creating, the same as the browser sign-up's own wait.
    internal static readonly TimeSpan ProvisioningWaitLimit = SignInStepViewModel.SignUpWait;

    /// <summary>
    /// kcap-web adds the owner to a workspace's organization before the workspace is live, so discovery
    /// can list one still provisioning, whose host 404s until then. kcap-web's own status for it tells
    /// the two apart: a workspace still being created is waited for, so sign-in finishes on its own once
    /// it is live. One kcap-web gave up on is gone; one still not live when the wait ends is pending.
    /// </summary>
    internal static Func<DiscoveredTenant, string, CancellationToken, Task<WorkspaceAnswer>> ProbeWorkspace(
            IHttpClientFactory httpFactory, AuthEndpoints endpoints, Action<string> waiting, Action<string> failed,
            TimeProvider time) {
        // The bearers of discoveries that already found a live workspace. Discovery waits on every probe,
        // so a wait on one still being created would otherwise hold back the live ones.
        var liveFound = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        return async (tenant, bearer, ct) => {
            var answer = await AskAsync(tenant, ct);
            if (answer == WorkspaceAnswer.Live) liveFound.TryAdd(bearer, 0);
            if (answer != WorkspaceAnswer.Gone || tenant.Slug is not { Length: > 0 } slug) return answer;

            var provisioning = new TenantProvisioningClient(
                httpFactory.CreateClient(nameof(TenantProvisioningClient)), ClientAgent.Desktop);
            // The status client reads a cancelled request as a transport failure.
            var status = await provisioning.GetStatusAsync(endpoints.SignupUrl, bearer, slug, ct);
            ct.ThrowIfCancellationRequested();
            if (ProvisioningPoll.Classify(status.StatusCode, status.Body?.State, status.Body?.WorkosOrgId)
                is PollVerdict.Failed or PollVerdict.ActiveNoOrg) {
                failed(tenant.Label);
                return answer;
            }
            if (!ProvisioningPoll.IsPending(status.StatusCode, status.Body?.State)) return answer;

            waiting(tenant.Label);
            var deadline = time.GetUtcNow() + ProvisioningWaitLimit;
            while (time.GetUtcNow() < deadline) {
                if (liveFound.ContainsKey(bearer)) return WorkspaceAnswer.Pending;
                await Task.Delay(ProvisioningPollInterval, time, ct);
                if (liveFound.ContainsKey(bearer)) return WorkspaceAnswer.Pending;

                answer = await AskAsync(tenant, ct);
                if (answer == WorkspaceAnswer.Live) {
                    liveFound.TryAdd(bearer, 0);
                    return answer;
                }

                // An expired bearer can no longer see a failure, so the wait ends there and sign-in offers another try.
                status = await provisioning.GetStatusAsync(endpoints.SignupUrl, bearer, slug, ct);
                ct.ThrowIfCancellationRequested();
                if (status.StatusCode == 401) return WorkspaceAnswer.Pending;
                if (ProvisioningPoll.Classify(status.StatusCode, status.Body?.State, status.Body?.WorkosOrgId)
                    is PollVerdict.Failed or PollVerdict.NotFound or PollVerdict.Forbidden or PollVerdict.ActiveNoOrg) {
                    failed(tenant.Label);
                    return WorkspaceAnswer.Gone;
                }
            }

            return WorkspaceAnswer.Pending;
        };

        async Task<WorkspaceAnswer> AskAsync(DiscoveredTenant tenant, CancellationToken ct) {
            using var client = httpFactory.CreateClient(CapacitorClients.Anonymous);

            return await WorkspaceProbe.AskAsync(client, tenant.Origin, time, ct);
        }
    }

    internal static LocalInstallController NewInstallController(WizardGraphOptions options) {
        var cli = new LateBoundKcapCli(options.ResolveCli, options.CliPath);

        return new LocalInstallController(
            () => cli, options.LocalRoot, () => IsCommittedLocal(options), options.Bridges.Post,
            options.Bridges.Telemetry.Funnel);
    }

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

        // The journal, config and registry are read here, on Edition entry and when an install settles,
        // never per evaluation: PassThrough, BackVisible and bindings run on the UI thread.
        var durable = new BehaviorSubject<LocalSetupSnapshot>(ReadDurable());
        LocalSetupSnapshot ReadDurable() => new(LocalSetupState.Pending(root), IsCommittedLocal(options));
        void RefreshDurable() => durable.OnNext(ReadDurable());
        bool Pending()        => durable.Value.Pending;
        bool CommittedLocal() => durable.Value.CommittedLocal;

        var install = options.Install ?? NewInstallController(options);
        bool Installed() => install.Outcome is LocalInstallOutcome.Installed;
        void OnSettling(LocalSetupSnapshot snapshot) => durable.OnNext(snapshot);
        install.Settling += OnSettling;

        var committedLocalAtStart = CommittedLocal();
        // A workspace already signed in at start is this run's: setup neither moves it to the other edition
        // nor signs it in again.
        var signedInAtStart = options.AlreadyAuthenticated && !committedLocalAtStart;
        var edition = new EditionStepViewModel(
            options.Bridges.Telemetry.Funnel,
            () => {
                RefreshDurable();
                return LocalUnavailableReason(options.LocalEditionSupported, options.Root, root);
            },
            locked: () => signedInAtStart || Installed() || Pending() || CommittedLocal());
        bool OnLocalPath() => edition.Choice == EditionChoice.Local || Pending() || CommittedLocal();
        var installStep = new InstallStepViewModel(
            install,
            appProfile: () => options.ResolveIdentity()?.Profile ?? options.ProfileOverride,
            configActiveProfile: () => ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(options.Root), out var config)
                ? config.ActiveName
                : null,
            // A committed machine has nothing left to install.
            onLocalPath: () => OnLocalPath() && !CommittedLocal(),
            builtinOffer: options.BuiltinOffer,
            recorded: () => LocalEmbeddingFile.Read(root),
            repairClients: options.RepairClients,
            time: options.Time,
            mailSupport: options.MailSupport is { } mail ? failures => mail(InstallSupportReport.Build(failures, root)) : null,
            resuming: Pending);
        var connect  = new ConnectChoiceViewModel();
        var signIn   = new SignInStepViewModel(
            auth, connect, options.Bridges, claims, options.AppState, options.UrlOpener, time: options.Time,
            passThrough: () => signedInAtStart || OnLocalPath(), signingUp: () => edition.Choice == EditionChoice.Cloud);
        if (signedInAtStart) signIn.RestoreCommitted();
        var pathFix  = options.ShimApplicable && options.ShimInstaller is { } installer
            ? new PathFixViewModel(installer, options.AppState, options.ShimTarget, OnLocalPath)
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
            onLocalPath: OnLocalPath,
            visibilityOptions: options.ReadVisibilityOptions);
        var history = new HistoryStepViewModel(cli, options.Bridges.Post, machine, options.Time, OnLocalPath);
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
        // Only the settled outcome counts: a progress line is not an installed server. The terminal
        // command is part of a local install, harnesses or not; the harness step joins this run.
        void OnInstallSettled(LocalInstallOutcome outcome) {
            if (outcome is not LocalInstallOutcome.Installed) return;
            _ = daemon.EnsureAsync(options.ShutdownToken);
            _ = pathFix?.LinkAsync(options.ShutdownToken);
        }
        install.Settled += OnInstallSettled;
        if (committedLocalAtStart) _ = daemon.EnsureAsync(options.ShutdownToken);

        OnboardingViewModel? shell = null;
        // The daemon and its outcome lines change while Done is open; the page re-reads its facts on each.
        var serviceChanged = daemon.WhenAnyValue(x => x.Satisfied, x => x.Busy, x => x.Status, x => x.Message, (_, _, _, _) => Unit.Default)
            .Merge(options.Surface.WhenAnyValue(x => x.StatusText, x => x.AttentionText, (_, _) => Unit.Default));
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
                local ? LocalEndpoints.Read(root).Active?.ServerUrl : null,
                daemon.Busy,
                options.Surface.AttentionText ?? options.Surface.StatusText);
        }, options.UrlOpener, PrepareWorkspaceAsync, serviceChanged,
            // The same page Help and support → Set up kcap leads through to connect an agent.
            addAgent: () => shell?.TryGoTo(WizardStepId.Harnesses));
        IWizardStep[] steps = [edition, installStep, signIn, harnesses, history, new UsageDataStepViewModel(options.UsageNotice), done];

        // Before a server is connected the app has nothing to show, so deferring is offered only for the optional steps after.
        var canFinishLater = signIn.WhenAnyValue(x => x.Satisfied).Select(_ => Unit.Default)
            .Merge(install.WhenAnyValue(x => x.Outcome).Select(_ => Unit.Default))
            .Merge(durable.Select(_ => Unit.Default))
            .Select(_ => Connected());
        // Someone coming back from kcap.ai's sign-up already has a workspace; everyone else chooses first.
        var startAt = options.ResumeWebSignUp && !signedInAtStart && !OnLocalPath() && !Installed()
            ? WizardStepId.SignIn
            : (WizardStepId?)null;
        var wizard = shell = new OnboardingViewModel(
            steps, options.ShutdownToken, options.Surface, startAt, canFinishLater,
            preflight: new HarnessPreflight(detect), restate: durable.Select(_ => Unit.Default));
        signIn.SignUpExpired += () => {
            edition.ShowNotice(SignInStepViewModel.SignUpExpiredNotice);
            wizard.TryGoTo(WizardStepId.Edition);
        };
        var funnel = WizardFunnel.Attach(wizard, options.Bridges.Telemetry.Funnel,
            daemon.WhenAnyValue(x => x.Satisfied, x => x.Row, (satisfied, row) => (satisfied, row == DaemonRow.AlreadyEnabled)));
        wizard.CloseRequested += signIn.EndSignUpWait;
        // The controller outlives this graph: a later wizard over it must not drive this one's enabler.
        wizard.CloseRequested += () => {
            install.Settling -= OnSettling;
            install.Settled -= OnInstallSettled;
            installStep.Detach();
            funnel.Dispose();
        };

        return new WizardGraph(wizard, auth, steps, history, connect, daemon, install, pathFix);

        bool Connected() => signIn.Satisfied || Installed() || CommittedLocal();

        // A run already in flight is waited out, never queued behind: a second run straight after a
        // declined takeover would ask again. A failed run is repeated only when the user retries.
        async Task<WorkspaceReadiness> PrepareWorkspaceAsync(bool retry) {
            await daemon.QuiescedAsync().ConfigureAwait(true);
            if (!daemon.Satisfied && (retry || daemon.Problem is null))
                await daemon.EnsureAsync(options.ShutdownToken).ConfigureAwait(true);
            if (!daemon.Satisfied) return WorkspaceReadiness.DaemonNotRunning;

            return options.ReportCliSetup is null || await options.ReportCliSetup(options.ShutdownToken).ConfigureAwait(true)
                ? WorkspaceReadiness.Ready
                : WorkspaceReadiness.Unreported;
        }
    }

    /// Why this machine cannot take the local edition, or null when it can. A cloud profile anywhere
    /// in the config is the CLI's own refusal, so the card says so before the install would.
    internal static string? LocalUnavailableReason(bool localEditionSupported, ConfigRoot config, LocalRoot root) {
        if (!localEditionSupported) return EditionStepViewModel.UnsupportedPlatform;

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
