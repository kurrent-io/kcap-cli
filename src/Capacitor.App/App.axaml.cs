using Capacitor.Cli.Core.Telemetry;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Notifications;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core.Install;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using ReactiveUI.Reactive;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests.Readers;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Core.Setup;
using Capacitor.Remote.Models;
using Capacitor.Cli.Core.Harness;
using Microsoft.Extensions.DependencyInjection;
using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App;

public partial class App : Application {
    // Allows the rename transaction's 100s budget plus CLI discovery and evidence checks to settle.
    static readonly TimeSpan QuiesceShutdownCap = TimeSpan.FromSeconds(150);

    // One socket dial's bound inside DaemonMutationLane's own confirmation polling (its
    // DetachedPollInterval is 1s) — short enough that a handful of polls still fit inside the
    // lane's 10s DetachedConfirmWindow.
    static readonly TimeSpan OneShotProbeTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan UninstallTerminalPathTimeout = TimeSpan.FromSeconds(2);

    // Linked to the app's shutdown sequence below; the token StartDaemonCommand's WAIT is built
    // against — never CancellationToken.None, or an unbounded wait would survive app exit.
    readonly CancellationTokenSource _shutdown = new();

    // The app's one read of KCAP_DAEMONS_DIR.
    readonly DaemonStore _daemonStore = DaemonStore.FromEnvironment();

    // And its one read of KCAP_CONFIG_DIR, besides Program's for the crash log.
    readonly ConfigRoot _config = ConfigRoot.FromEnvironment();

    // And its one read of KCAP_URL / KCAP_PROFILE.
    readonly ProfileOverrides _serverEnv  = ProfileOverrides.FromEnvironment();
    readonly MachineAuth      _machineEnv = MachineAuth.FromEnvironment();
    readonly AuthEndpoints    _endpoints  = AuthEndpoints.FromEnvironment();
    readonly UserHome   _userHome = UserHome.FromEnvironment();

    LifecycleLog? _lifecycleLogInstance;
    LifecycleLog LifecycleLog => _lifecycleLogInstance ??= new LifecycleLog(_config, _time, _userHome.Path,
        closed: () => _uninstalling || _uninstallMode is not null);

    // The update this launch finishes, once StartAsync has taken its record; null on an ordinary launch.
    AppUpdateTransition? _postUpdate;
    DateTimeOffset _startedAt;

    // And its one read of KCAP_LOCAL_ROOT. The registry under it is read again at each resolution:
    // the wizard can create an installation while the app runs.
    readonly LocalRoot _localRoot = LocalRoot.FromEnvironment(UserHome.FromEnvironment());

    // The app's one clock, named here because App is the composition root. A field initializer
    // cannot read another instance field, so the fields below name it again rather than take it.
    readonly TimeProvider _time = TimeProvider.System;

    /// The app's own facade, labelled <c>desktop</c>. The CLI children it spawns stay silent under
    /// <c>KCAP_APP_SPAWN_NO_TELEMETRY</c>, so a desktop step is never also counted as a CLI one.
    readonly CliTelemetry _telemetry;

    /// The one-time disclosure, shared by the wizard and the main window so acknowledging it in one
    /// hides it in both.
    readonly UsageDataNotice _usageNotice;

    /// Process-lifetime rather than per wizard run — a provisioning poll can outlive the window that
    /// started it, and this client degrades a transport failure but not a disposed handler. What it
    /// holds, and why it holds only that, is <see cref="AppHttpServices.AddAppForeignHttp"/>.
    readonly ServiceProvider _foreignHttp;

    // The app's one store over app-state.json. Its lock is per instance, so a second store lets
    // two read-modify-write cycles interleave and drop an update.
    readonly AppStateStore _appState;

    readonly UiScaleSetting _uiScale;
    readonly BetaUpdatesSetting _betaUpdates;

    // Set only in a copy started to uninstall; nothing of the normal app is built in it.
    readonly UninstallMode? _uninstallMode = Program.Uninstall;
    UninstallRunViewModel? _uninstallRun;

    public App() {
        // A live facade creates the device id and the spool under the config root being removed.
        _telemetry   = CliTelemetry.Start(DesktopTelemetry(_uninstallMode, _endpoints.SignupUrl), _config, _time);
        _usageNotice = new UsageDataNotice(_telemetry, _config);
        _foreignHttp = new ServiceCollection().AddAppForeignHttp(_config, _serverEnv).BuildValidated();
#pragma warning disable RS0030 // the one store the ban points every other site at
        _appState = new AppStateStore(_config.Path("app-state.json"));
#pragma warning restore RS0030
        _uiScale = UiScaleSetting.LoadAsync(_appState).GetAwaiter().GetResult();
        _betaUpdates = BetaUpdatesSetting.LoadAsync(_appState).GetAwaiter().GetResult();
    }

    internal static TelemetryStartup DesktopTelemetry(UninstallMode? uninstall, string signupUrl) =>
        TelemetryStartup.ForDesktopApp(serverUrl: null, signupUrl) with { Suppressed = uninstall is not null };

    /// The authenticated lanes. They cannot join <see cref="_foreignHttp"/>: their handlers need a
    /// resolved server, and the app starts before a profile has named one. Process-lifetime for the
    /// same reason that one is — what draws from it outlives any single window.
    ServiceProvider? _serverHttp;

    ServiceProvider? ServerLane(ProfileContext? profiles) {
        if (profiles?.Resolution.ServerUrl is not { Length: > 0 } url) return null;

        return _serverHttp ??= new ServiceCollection()
            .AddSingleton(_config)
            .AddSingleton(profiles)
            .AddSingleton(new CapacitorServer(url, _config, profiles, _localRoot))
            .AddCapacitorHttp(_serverEnv, _machineEnv)
            .BuildValidated();
    }

    ICapacitorHttpClient? ServerHttp(ProfileContext? profiles) => ServerLane(profiles)?.GetRequiredService<ICapacitorHttpClient>();

    ICredentialSource? ServerCredentials(ProfileContext? profiles) => ServerLane(profiles)?.GetRequiredService<ICredentialSource>();

    // And its one read of KCAP_APP_PTY_DUMP: a file every terminal feed frame is appended to as
    // received, for seeing what the emulator was given.
    static readonly string? PtyDumpPath = Environment.GetEnvironmentVariable("KCAP_APP_PTY_DUMP");

    // Both are app-lifetime and BOTH exist before any graph does: OnShutdownRequested latches and
    // drains them whether or not StartAsync ever got as far as building a window. The gate
    // is shared by every MainWindowViewModel the coordinator builds — including one built between
    // the two shutdown passes, which is the case a per-window latch cannot cover.
    readonly NavigationGate _navigation = new();
    readonly WorkspaceTeardownTracker _workspaceTeardown = new(
        TimeProvider.System,
        (context, ex) => Console.Error.WriteLine($"kcap app: {context} failed: {ex}"));
    // Constructed FIRST (before any other graph object) in StartAsync and disposed LAST — every
    // daemon mutation in the app runs through it, so nothing that might still call RunAsync can outlive it.
    DaemonMutationLane? _lane;
    DaemonClientService? _service; // concrete type: IAsyncDisposable is not on the interface
    // Started before _service.Start() begins pumping, and disposed before _service: it subscribes
    // to _service's streams.
    DaemonLifecycleController? _lifecycle;
    // No disposal needed — it holds no subscription, only a one-shot await chain that cancelling
    // _shutdown stops.
    ShimOfferCoordinator? _shimOffer;
    // The wizard's PATH link, which can outlive the wizard; the shim offer waits on it.
    Task<string?> _wizardPathLink = Task.FromResult<string?>(null);
    // No disposal needed — its Status subscription dies with _service's own subject disposal below.
    ConsentFlipCoordinator? _consentFlip;
    // Inert outside a packed bundle (CreateUpdater degrades a construction failure the same way);
    // replaced by CreateUpdater in StartAsync before any graph exists.
    IAppUpdater _updater = InertAppUpdater.Instance;
    // No disposal needed — its schedule loop exits on _shutdown; ApplyPendingOnExit is called
    // explicitly at shutdown rather than through Dispose.
    UpdateCoordinator? _updates;
    IDisposable? _updateCheckSubscription;
    UpdateCheckWindow? _updateCheckWindow;
    bool _updateCheckHidden;
    UpdateCheckProgress? _updateCheckProgress;
    // Assigned by StartAsync's success path only; every one is still null on a startup failure
    // (and cleared again by the catch, which disposes whatever had been built). Teardown —
    // shutdown and startup-failure alike — disposes them in reverse creation order, tray icon
    // first, so a quit never strands a dead icon in the menu bar.
    MainWindowCoordinator? _coordinator;
    PauseController? _pause;
    ConsentService? _consent;
    PermissionService? _permissions;
    AccountsService? _accounts;
    IObservable<DateTimeOffset>? _usageClock;
    NotificationSettingsService? _notificationSettings;
    IDesktopNotificationSink? _notificationSink;
    IDesktopNotificationAccess? _notificationAccess;
    IDisposable? _notificationAccessPrompt;
    DesktopNotificationCoordinator? _desktopNotifications;
    readonly Subject<string?> _viewedAgent = new();
    NotificationSessionSubscriptions? _notificationSessions;
    // The server lane's half of the permission graph. Disposed as a group with _permissions: the
    // feed and the tracker first (both push into the cache and hold timers), the access service
    // last, since its transitions are what the feed subscribes to.
    ServerPermissionFeed? _permissionFeed;
    SessionAttentionTracker? _attention;
    BackgroundCommandActivity? _commands;
    SessionAccessService? _sessionAccess;
    PullRequestToneCache? _pullRequestTones;
    ConsentPromptCoordinator? _promptCoordinator;
    // Disposed with the other UI services below: it holds a constructor-scoped subscription to
    // the shared ticker, which is RefCount'd — an undisposed subscriber keeps the Interval (and
    // this object) running past teardown. Held as a field so it survives StartAsync's own stack
    // frame: the prompt window factory and BuildAndShowMainWindow both close over the SAME
    // instance.
    ActivityViewModel? _activity;
    LaunchApprovalsCoordinator? _launchApprovals;
    WebWorkspaceViewModel? _webWorkspace;
    // Both constructed INSIDE BuildAndShowMainWindow, over the same `service` instance, and read
    // back off the built window's DataContext so they can be disposed.
    HomeViewModel? _home;
    SessionRailViewModel? _rail;
    // Shared by every window Home over this graph, so owned here rather than by any one of them.
    LocalServerRecoveryViewModel? _localServer;
    // Only on a machine whose server is its local installation; a quit settles the upgrade like the install.
    LocalUpgradeController? _localUpgrade;
    LocalSearchController? _localSearch;
    LocalStackViewModel? _localStack;
    // The server-side clients that outlive a window rebuild (MainWindowCoordinator can build a
    // second window over the same launch client) and own live transports. Torn down through one
    // holder on both teardown paths, after _home — never before, or a launch still in flight would
    // lose its transport mid-invoke.
    ServerClients? _serverClients;
    // Disposed together, before _serverClients, inside DisposeServerClientsAsync — _directory
    // subscribes to both _remoteAgents and serverLane, so it goes first.
    RemoteAgentsService? _remoteAgents;
    ServerSessionsService? _sessions;
    AgentDirectory? _directory;
    ServerVendorModelCatalog? _modelCatalog;
    GitHubCliReaderProvider? _gh;
    TrayViewModel? _trayVm;
    TrayIconManager? _tray;
    DaemonRestartPendingWatcher? _restartPending;
    ExpectedDaemonRestart? _expectedRestart;
    StaleDaemonNudge? _staleDaemonNudge;
    // Wizard-first mode only: the sign-in driver shutdown cancels and awaits before anything is
    // disposed, and the window that owns dialogs while no main window exists. Both are cleared again
    // by the handoff at the end of RunWizardModeAsync.
    WizardAuthService? _wizardAuth;
    // The History step, kept past the handoff: its import outlives onboarding and only shutdown stops it.
    HistoryStepViewModel? _historyImport;
    // One per app run, shared by every wizard so a reopened one shows the settled outcome; every
    // close and a quit settle it before moving on.
    LocalInstallController? _localInstall;
    Window? _wizardWindow;
    // The onboarding window once its flow has finished, waiting for BuildAndShowMainWindow to take
    // it over so the main surface opens in place rather than in a second window.
    (MainWindow Window, AppState State)? _adoptableWindow;
    // Steady-state re-auth dialog, one at a time — a second Sign in click focuses it. The settle
    // task is FinishSignInAsync for the most recent close; shutdown awaits it so a live attempt is
    // cancelled (or a commit past the boundary finishes) before the process exits — the dialog's
    // counterpart of the wizard sign-in quiesce.
    SignInWindow? _signInWindow;
    SettingsWindow? _settingsWindow;
    readonly BehaviorSubject<Action?> _openSearchSettings = new(null);
    FeedbackWindow? _feedbackWindow;
    internal FeedbackWindow? FeedbackWindowForTests => _feedbackWindow;
    readonly AppMenu _appMenu = new(AppKitMenus.ShowAboutPanel);
    // A field, not a local: the report items it retains are enabled only once the profile has
    // resolved, long after the bar is attached.
    AppMenuBar? _menuBar;
    Task? _reauthSettle;
    bool _shutdownStarted;
    bool _shutdownConfirmed;
    bool _setupRestarting;
    bool _uninstalling;
    // 0 = normal shutdown. Set to 1 on a startup failure so the DEFERRED shutdown path (Cmd+Q /
    // platform shutdown while the error window is showing — OnShutdownRequested ->
    // DisposeAndShutdownAsync) still reports failure, instead of TryShutdown()'s platform
    // default of 0 silently overwriting it.
    int _exitCode;

    // Outside a packed bundle Velopack reports not-installed and the coordinator stays inert; a
    // constructor failure degrades the same way rather than taking startup down. In a snap, snapd
    // owns updates and the install is a read-only mount, so Velopack is never consulted.
    static IAppUpdater CreateUpdater(BetaUpdatesSetting betaUpdates) {
        if (SnapLayout.OfRunningBinary() is not null) return InertAppUpdater.Instance;

        try {
            return new VelopackAppUpdater(Environment.GetEnvironmentVariable, () => betaUpdates.Choice);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: updater unavailable: {ex.Message}");
            return InertAppUpdater.Instance;
        }
    }

    public override void Initialize() {
        AvaloniaXamlLoader.Load(this);
        LineSelection.Install();
        ToolTipFocusGate.Install();
        // Here, not later: Avalonia exports the app menu right after Initialize, substituting its own
        // "About Avalonia" when there is none.
        NativeMenu.SetMenu(this, _appMenu.Menu);
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            // Closing the main window hides it; only an explicit quit ends the process.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnShutdownRequested;
            var windowLifecycle = new DesktopWindowLifecycle(this.TryGetFeature<IActivatableLifetime>(),
                () => _shutdownStarted ? null : MainWindowAction(_coordinator), AppKitDock.SetVisible);
            desktop.Exit += (_, _) => { windowLifecycle.Dispose(); Program.Host.Dispose(); };
            Program.Host.SingleInstance.ShowRequested += () => Dispatcher.UIThread.Post(() => {
                if (_uninstallRun is not null && desktop.MainWindow is { } uninstallWindow) {
                    if (uninstallWindow.WindowState == WindowState.Minimized) uninstallWindow.WindowState = WindowState.Normal;
                    uninstallWindow.Activate();
                    return;
                }
                if (_shutdownStarted) return;
                if (MainWindowAction(_coordinator) is { } showMain) {
                    showMain();
                    return;
                }
                // Onboarding and the startup screens come before the main window exists.
                var startup = _wizardWindow is { IsVisible: true } wizard ? wizard : desktop.Windows.FirstOrDefault(w => w.IsVisible);
                if (startup is null) return;
                if (startup.WindowState == WindowState.Minimized) startup.WindowState = WindowState.Normal;
                startup.Activate();
            });
            if (_uninstallMode is { } uninstall) {
                RunUninstallMode(desktop, uninstall);
            } else {
                // Before StartAsync: it shows its first window (the install guard or the wizard) synchronously.
                _menuBar = new AppMenuBar(new ShellUrlOpener(), () => desktop.Windows, () => MainWindowAction(_coordinator));
                _menuBar.Install();
                _ = StartAsync(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // A failed startup latches the coordinator but keeps it, and a quit latches it too: showing a
    // window from either would run the window factory over a graph that is being torn down.
    internal static Action? MainWindowAction(MainWindowCoordinator? coordinator) =>
        coordinator is { QuitInProgress: false } c ? c.ShowMainWindow : null;

    // This continuation is the ONLY path to a visible window: OnFrameworkInitializationCompleted
    // fires it fire-and-forget and returns immediately, so an exception escaping here would
    // otherwise leave a live process with an empty dispatcher loop, no window, and no error
    // surface (stderr is invisible for a GUI-launched WinExe) — it must fail loudly instead.
    /// <param name="guarded">False when the install guard already ran and let this launch go on.</param>
    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, bool guarded = true) {
        try {
            if (guarded) {
                _startedAt = _time.GetUtcNow();
                LifecycleLog.Write("app_started", ("executable", Environment.ProcessPath),
                    ("update_relaunch", Program.Host.UpdateRelaunch ? "true" : null),
                    ("installed_handoff", desktop.Args?.Contains("--kcap-installed", StringComparer.Ordinal) == true ? "true" : null));
                if (RunInstallLocationGuard(desktop)) return; // the guard window owns the rest of this run
            }
            _updater = CreateUpdater(_betaUpdates);
            if (UpdateCoordinator.TryApplyPendingAtStartup(_updater, Program.Host.UpdateRelaunch,
                    HandOver(AppUpdateTransition.PendingAtStartup), LifecycleLog)) return; // the process is being replaced
            TakeUpdateTransition();

            // The lane is constructed first — every daemon mutation routes through this one instance, and its dependencies need neither a resolved profile nor a live service.
            var laneRunner = new ProcessRunner(_time);
            var laneProbe  = TerminalPathProbe.Create(laneRunner, Environment.GetEnvironmentVariable);
            var channel    = new OutcomeChannel();
            var lane = new DaemonMutationLane(
                _daemonStore, laneProbe, channel, ResolvePinnedCli,
                (request, pinnedPath) => new KcapCli(
                    laneRunner, pinnedPath, request.DaemonName, request.Profile, laneProbe.TerminalPathAsync,
                    canonicalServer: request.CanonicalServer),
                _ => new OneShotObservation(_daemonStore, _time, OneShotProbeTimeout),
                _time);
            _lane = lane;

            var (gate, profiles) = await ResolveAndEvaluateGateAsync(_config, _foreignHttp.GetRequiredService<TokenStore>(), _serverEnv, _localRoot, _time, _shutdown.Token);
            await RunWizardThenBuildGraphAsync(
                gate, profiles, ShouldShowWizard(gate, desktop.Args),
                (current, currentProfiles) => RunWizardModeAsync(desktop, lane, channel, laneRunner, laneProbe, currentProfiles,
                    alreadyAuthenticated: current is GateResult.Complete),
                () => ResolveAndEvaluateGateAsync(_config, _foreignHttp.GetRequiredService<TokenStore>(), _serverEnv, _localRoot, _time, _shutdown.Token),
                (current, currentProfiles, laneQuiesced) => BuildDaemonGraph(desktop, lane, channel, current, currentProfiles, laneQuiesced),
                () => LocalSetupState.Pending(_localRoot), _shutdown.Token, () => LocalSetupCannotRetry(_localInstall));

            // Integration repair must not delay the main window or prevent access to the app.
            if (OperatingSystem.IsMacOS() && !_shutdown.IsCancellationRequested) {
                // Let Loaded attach the notification overlay before an immediate repair failure.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var warning = await new ApplicationIntegrationRefresh(_config, new ProcessRunner(_time))
                    .RunPendingAsync(InstallLocation.BundleRoot(Environment.ProcessPath), _shutdown.Token);
                if (warning is not null) _coordinator?.Window?.Notifier?.Notify(warning);
                _ = RetirePreviousBundleAsync(InstallLocation.BundleRoot(Environment.ProcessPath));
            }
            if (_updateNotApplied is { } failed && !_shutdown.IsCancellationRequested)
                _coordinator?.Window?.Notifier?.Notify(
                    $"kcap {failed.To} did not install. This is still kcap {CapacitorVersion.CurrentDisplay()}, and the update is offered again shortly.");
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
            // A quit landing mid-startup (gate evaluation, the wizard's PATH probe, the post-wizard
            // re-resolve) is not a startup failure: the shutdown path already owns teardown, and an
            // error window here would both lie and outlive the quit.
            _wizardWindow = null;
            _wizardAuth = null;
        } catch (Exception ex) {
            // A quit during cleanup must retain the startup failure's exit code.
            _exitCode = 1;
            _shutdown.Cancel();
            // Block window recreation and hide-on-close before cleanup can yield.
            if (_coordinator is not null) _coordinator.QuitInProgress = true;
            LatchNavigation();
            if (_wizardWindow is { IsVisible: true } wizard) wizard.Close();
            if (TakeAdoptableWindow() is { } adoptable) adoptable.Window.Close();
            Console.Error.WriteLine($"kcap app failed to start: {ex}");
            await QuiesceAppAsync(_wizardAuth, _historyImport, _localInstall,
                lifecycle: null, lane: null, QuiesceShutdownCap, _time);
            await _workspaceTeardown.DrainAsync();
            await HandleStartupFailureAsync(
                desktop, ex, _service, _shutdown,
                [_tray, _trayVm, _promptCoordinator, _consent, _desktopNotifications, _notificationSink,
                    _notificationSessions, _notificationSettings, _permissionFeed, _attention, _commands, _permissions, _accounts, _sessionAccess,
                    _pullRequestTones, _launchApprovals, _webWorkspace, _activity, _home, _localServer, _localStack, _rail, _pause, _staleDaemonNudge, _restartPending, _expectedRestart],
                _lifecycle, _lane, _localUpgrade, _localSearch, _menuBar, _coordinator);
            await DisposeServerClientsAsync(); // after _home above
            // all already disposed above — never let a later OnShutdownRequested (e.g. Cmd+Q
            // while the error window is up) dispose any of them a second time
            _service = null;
            _lifecycle = null;
            _lane = null;
            _localUpgrade = null;
            _localSearch = null;
            _shimOffer = null; // no disposal of its own (see field comment) — just drop the reference
            _consentFlip = null; // same — no disposal of its own
            _tray = null;
            _trayVm = null;
            _promptCoordinator = null;
            _consent = null;
            _desktopNotifications = null;
            _notificationSink = null;
            _notificationSessions = null;
            _notificationSettings = null;
            _permissionFeed = null;
            _attention = null;
            _commands = null;
            _permissions = null;
            _accounts = null;
            _usageClock = null;
            _sessionAccess = null;
            _pullRequestTones = null;
            _pause = null;
            _activity = null;
            _launchApprovals = null;
            _webWorkspace = null;
            _home = null;
            _localServer = null;
            _rail = null;
            _wizardAuth = null;
            _historyImport = null;
            _localInstall = null;
            _wizardWindow = null;
        }
    }

    Action<UpdateCandidate> HandOver(string entry) => candidate =>
        AppUpdateTransition.Begin(entry, _updater.InstalledVersion, candidate.Version, _time).Save(_config);

    /// Reads what an update left for this launch. A record for another version is an update that did not apply:
    /// Velopack relaunches the old version after a failed apply, so it is reported rather than finished.
    void TakeUpdateTransition() {
        var running = _updater.InstalledVersion ?? CapacitorVersion.CurrentDisplay();
        var transition = AppUpdateTransition.Take(_config, _time);
        if (transition is null && Program.Host.UpdateRelaunch)
            transition = new AppUpdateTransition("relaunch", AppUpdateTransition.InApp, null, running, _startedAt);
        if (transition is null) return;
        var fields = new (string, string?)[] {
            ("operation", transition.Id), ("entry", transition.Entry), ("from", transition.From), ("to", transition.To),
            ("handover_ms", ((long)(_startedAt - transition.StartedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
        };
        if (transition.AppliedTo(running)) {
            _postUpdate = transition;
            LifecycleLog.Write("update_relaunched", fields);
        } else {
            _updateNotApplied = transition;
            LifecycleLog.Write("update_not_applied", [.. fields, ("running", running)]);
        }
    }

    /// A launch that owes its local server an upgrade finishes an update whatever route installed it. The disk image
    /// and a copy dragged over the old app leave no record, and the upgrade would otherwise stop and start the server
    /// under the main window.
    AppUpdateTransition? InferUpdate(bool localUpgradeOwed) {
        if (!localUpgradeOwed) return null;
        var running = CapacitorVersion.CurrentDisplay();
        if (LocalEndpoints.Read(_localRoot).Active?.Version is not { } local || !PrereleaseSemver.IsNewer(running, local)) return null;
        LifecycleLog.Write("update_inferred", ("local_server", local), ("to", running));
        return new AppUpdateTransition("components", AppUpdateTransition.Installer, null, running, _startedAt);
    }

    AppUpdateTransition? _updateNotApplied;
    Action? _closeUpdateFinishWindow;
    // Completes once no update is being finished: at once on an ordinary launch, else when its steps settle.
    // The command-line tool offer waits for it, since its prompt would otherwise land in the middle of the update.
    readonly TaskCompletionSource _updateHandedOver = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _holdingMainWindow;

    void ShowUpdateCheckProgress(UpdateCheckProgress? progress) {
        _updateCheckProgress = progress;
        if (progress is null) {
            _updateCheckWindow?.Close();
            _updateCheckHidden = false;
        } else if (!_shutdownStarted && !_updateCheckHidden) {
            if (_updateCheckWindow is null) {
                var window = new UpdateCheckWindow { DataContext = progress };
                window.Closed += (_, _) => {
                    _updateCheckWindow = null;
                    _updateCheckHidden = true;
                };
                _updateCheckWindow = window;
                window.Show();
                window.Activate();
            } else {
                _updateCheckWindow.DataContext = progress;
            }
        }
    }

    Task CheckForAppUpdatesAsync() {
        _updateCheckHidden = false;
        ShowUpdateCheckProgress(_updateCheckProgress);
        return _updates?.CheckForUpdatesAsync() ?? Task.CompletedTask;
    }

    Task RunUpdateMenuActionAsync() {
        _updateCheckHidden = false;
        ShowUpdateCheckProgress(_updateCheckProgress);
        return _updates?.RunMenuActionAsync() ?? Task.CompletedTask;
    }

    void ShowUpdateFinishWindow(AppUpdateTransition transition, IObservable<AttachStatus> status,
            IObservable<(string? Version, string? Connection)> daemon, IObservable<bool> restartPending, IObservable<LocalStepInput?> local) {
        var vm = new UpdateFinishViewModel(transition, status, daemon, restartPending, local, _time);
        var window = new UpdateFinishWindow { DataContext = vm };
        _home?.AttachUpdateProgress(vm.Progress);
        var opened = false;
        void BringForward() {
            if (opened) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }
        void Open() {
            if (opened) return;
            opened = true;
            _closeUpdateFinishWindow = null;
            _holdingMainWindow = false;
            if (_coordinator is { } coordinator) coordinator.ShowInstead = null;
            LifecycleLog.Write("update_finished", ("operation", transition.Id), ("settled", vm.IsSettled ? "true" : "false"),
                ("steps", string.Join("; ", vm.Steps.Select(row => $"{row.Step.Title}: {row.Step.State}"))),
                ("seconds", (_time.GetUtcNow() - _startedAt).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)));
            if (!_shutdownStarted) _coordinator?.ShowMainWindow();
            window.Close();
            // Opened early, it keeps following the steps for the launcher until they settle.
            if (vm.IsSettled) Settled();
            else vm.Progress.Where(p => !p.Finishing).Take(1).Subscribe(_ => Dispatcher.UIThread.Post(Settled));
        }
        void Settled() {
            vm.Dispose();
            _updateHandedOver.TrySetResult();
        }
        var earlierInstances = OperatingSystem.IsMacOS() ? QuitEarlierInstancesAsync(BringForward) : Task.CompletedTask;
        vm.Completed += () => _ = CompleteUpdateAsync(earlierInstances, () => Dispatcher.UIThread.Post(Open));
        vm.OpenRequested += Open;
        window.Closed += (_, _) => Open();
        _closeUpdateFinishWindow = Open;
        if (_coordinator is { } holder) holder.ShowInstead = BringForward;
        BringForward();
    }

    /// Automatic handover waits for predecessor shutdown; an explicit early handover calls Open directly.
    internal static async Task CompleteUpdateAsync(Task earlierInstances, Action open) {
        await earlierInstances.ConfigureAwait(false);
        open();
    }

    async Task QuitEarlierInstancesAsync(Action afterwards) {
        try {
            using var self = Process.GetCurrentProcess();
            var startedAt = self.StartTime;
            var (count, exited) = await Task.Run(() => InstalledAppProcesses.QuitEarlierAsync(startedAt, _time, _shutdown.Token));
            if (count == 0) return;
            LifecycleLog.Write("earlier_instances_quit", ("count", count.ToString(CultureInfo.InvariantCulture)), ("exited", exited ? "true" : "false"));
            Dispatcher.UIThread.Post(afterwards);
        } catch (OperationCanceledException) { }
    }

    /// From the moment an update is accepted until this process exits, which quiescing can stretch.
    void ShowUpdateClosingWindow() {
        if (_updates?.ReadyVersion is not { } to) return;
        var window = new UpdateFinishWindow { DataContext = UpdateFinishViewModel.Closing(to, _time) };
        foreach (var other in (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.ToArray() ?? []) other.Hide();
        window.Show();
        window.Activate();
    }

    /// Where the installer stops the daemon and it leaves no restart marker (Windows), an outage just after an update
    /// relaunch is the expected one. Elsewhere the marker is the evidence, and a daemon that was already down is not.
    bool AppJustUpdated() =>
        OperatingSystem.IsWindows() && _postUpdate is not null && _time.GetUtcNow() - _startedAt < TimeSpan.FromMinutes(2);

    /// The first launch of a version upgrades its committed local server, whichever route installed it. The
    /// stamp moves once that settles without failing, so a failure is tried again on the next launch and an
    /// ordinary launch does nothing.
    async Task SettleComponentsAsync(LocalUpgradeController? localUpgrade, bool isLocalServer, Action skipped) {
        var version = CapacitorVersion.CurrentDisplay();
        try {
            if (!AppComponentStamp.Owed(_config, version)) return;
            if (localUpgrade is null || !isLocalServer) {
                // Settled only with no local installation at all: one the app does not resolve to right now, signed
                // out or on another profile, still owes its upgrade to the launch that does.
                if (LocalEndpoints.Read(_localRoot).Active is not null) return;
                AppComponentStamp.Write(_config, version);
                LifecycleLog.Write("components_settled", ("version", version), ("local_upgrade", "no_local_installation"));
                return;
            }
            void OnSettled(LocalUpgradeOutcome outcome) {
                localUpgrade.Settled -= OnSettled;
                var (result, detail) = outcome switch {
                    LocalUpgradeOutcome.Upgraded u => ("upgraded", $"{u.From} -> {u.To}"),
                    LocalUpgradeOutcome.NothingToDo n => ("nothing_to_do", n.Reason),
                    LocalUpgradeOutcome.NotPublished p => ("not_published", p.Message),
                    LocalUpgradeOutcome.Failed f => ("failed", $"{f.Code}: {f.Message} (switched: {f.Switched})"),
                    _ => ("unknown", null),
                };
                LifecycleLog.Write("local_upgrade_settled", ("version", version), ("result", result), ("detail", detail));
                if (!AppComponentStamp.SettledBy(outcome)) return;
                try { AppComponentStamp.Write(_config, version); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    Console.Error.WriteLine($"kcap: could not record the settled components: {ex.Message}");
                }
            }
            localUpgrade.Settled += OnSettled;
            var started = await localUpgrade.StartAfterUpdateAsync(true, true, () => LocalSetupState.Pending(_localRoot), _shutdown.Token);
            if (!started) {
                localUpgrade.Settled -= OnSettled;
                skipped();
            }
            LifecycleLog.Write(started ? "local_upgrade_started" : "local_upgrade_skipped", ("version", version),
                ("reason", started ? null : "setup_pending"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not settle the local components: {ex.Message}");
        }
    }

    // A standalone launch and the app menu share the same window and removal pipeline.
    // The menu supplies preparation so normal app services stop before cleanup starts.
    void RunUninstallMode(IClassicDesktopStyleApplicationLifetime desktop, UninstallMode uninstall) =>
        StartUninstall(desktop, uninstall, prepareApp: false);

    bool StartUninstall(IClassicDesktopStyleApplicationLifetime desktop, UninstallMode uninstall, bool prepareApp) {
        var previous = prepareApp ? desktop.Windows.ToArray() : [];
        var runner = new ProcessRunner(_time);
        var probe = TerminalPathProbe.Create(runner, Environment.GetEnvironmentVariable);
        var cli = NewWizardCli(_config, _serverEnv, _localRoot, runner, ResolvePinnedCli().Path, probe);
        var quitting = false;
        void Quit(int exitCode) {
            if (quitting) return;
            quitting = true;
            desktop.Shutdown(exitCode);
        }
        Func<Task>? prepare = prepareApp ? () => PrepareAppForUninstallAsync(previous) : null;
        var run = OperatingSystem.IsMacOS()
            ? uninstall.BuildRun(Environment.ProcessPath, root => new MacAppRemoval(runner, _userHome, root), cli, () => Quit(0), prepare: prepare)
            : OperatingSystem.IsLinux()
                ? uninstall.BuildAppImageRun(AppImageInstall.Detect(Environment.GetEnvironmentVariable),
                    install => new LinuxAppRemoval(runner, _userHome, install, Environment.GetEnvironmentVariable, _time), cli, () => Quit(0), prepare: prepare)
                : null;
        if (run is null) {
            Console.Error.WriteLine("kcap: uninstalling from the app needs the installed kcap.app or the kcap AppImage.");
            if (!prepareApp) Dispatcher.UIThread.Post(() => Quit(1));
            return false;
        }

        var window = UninstallWindow.ShowProgress(previous, run);
        _uninstallRun = run;
        window.Closed += (_, _) => Quit(0);
        desktop.MainWindow = window;
        _ = run.RunAsync();
        return true;
    }

    async Task PrepareAppForUninstallAsync(IReadOnlyList<Window> previous) {
        _shutdownStarted = true;
        if (_coordinator is not null) _coordinator.QuitInProgress = true;
        LatchNavigation();
        _appMenu.SetSettingsAction(null);
        _appMenu.SetCheckForUpdatesAction(null);
        _appMenu.SetSetupAction(null);
        _appMenu.SetUninstallAction(null);
        _shutdown.Cancel();
        await DisposeAppAsync(shutdown: false);
        // Close normal windows while their persisted state still exists. Their shutdown latch
        // makes late close notifications inert after the shared services have been disposed.
        foreach (var window in previous) window.Close();
        if (_serverHttp is { } serverHttp) { _serverHttp = null; await serverHttp.DisposeAsync(); }
        await _foreignHttp.DisposeAsync();
        // No crash handler may recreate the config directory after the CLI removes it.
        Program.StopCrashLoggingForUninstall();
    }

    // macOS only: elsewhere there is no bundle. Returns true when a window was shown and startup
    // must stop here; the window's buttons end the process.
    bool RunInstallLocationGuard(IClassicDesktopStyleApplicationLifetime desktop) {
        if (!OperatingSystem.IsMacOS()) return false;
        var root = InstallLocation.BundleRoot(Environment.ProcessPath);
        var kind = InstallLocation.Classify(root, _userHome.Path);
        if (kind == InstallLocationKind.NotABundle) return false;
        if (InstallLocation.Passes(kind) && Path.GetFileName(root!) == "kcap.app"
                && desktop.Args?.Contains("--kcap-installed", StringComparer.Ordinal) == true) return false;
        var userApps = Path.Combine(_userHome.Path, "Applications");
        var directories = ApplicationsMover.ApplicationDirectories(root!, userApps);
        var runner = new ProcessRunner(_time);
        var launcher = new ApplicationsLauncher(runner, InstalledAppState, _time, installationHandoff: true);
        ApplicationsInstallPlan plan = null!;
        string? replacedVersion = null;
        InstallLocationWindow? shown = null;
        ApplicationsMover Mover(string apps) => new(runner, ApplicationsMover.PromoteExclusive, apps,
            swap: ApplicationsMover.SwapAtomic, isRunning: InstalledAppRunning,
            quitRunning: QuitInstalledAppAsync,
            migrateReferences: () => new ApplicationReferences(runner, _userHome, _daemonStore, _time),
            activate: async (target, ct) => {
                new ApplicationIntegrationRefresh(_config, runner).Schedule(target);
                RecordInstallerHandover(plan, replacedVersion);
                return await launcher.OpenAsync(target, ct);
            }, userApplicationsDir: userApps, retirement: new ApplicationRetirement(_config, _time), systemApplicationsDir: "/Applications");
        var mover = Mover(directories[0]);
        plan = mover.Inspect(root!);
        if (directories.Count == 1 && !ShouldPromptToInstall(kind, root!, plan)) return false;
        // Shutdown(0) closes this window as a side effect, which the window's own Closed handler
        // below turns back into a Quit call — this flag is what keeps that reentry from calling
        // Shutdown twice, whichever of the three paths (Quit, a successful move, or the titlebar
        // close) got there first.
        var quitting = false;
        void Quit() {
            if (quitting) return;
            quitting = true;
            desktop.Shutdown(0);
        }
        // Running as the installed copy, only a leftover older bundle prompted: declining goes on with this launch,
        // so a bundle that cannot be removed never locks the user out of the app.
        var installed = InstallLocation.Passes(kind) && Path.GetFileName(root!) == KcapBundle.BundleName
            && directories.Contains(Path.GetDirectoryName(Path.GetFullPath(root!)));
        void NotNow() {
            if (quitting) return;
            quitting = true;
            LifecycleLog.Write("install_deferred", ("replaces", string.Join(", ", plan.Replaces)));
            if (ReferenceEquals(desktop.MainWindow, shown)) desktop.MainWindow = null;
            shown?.Close();
            _ = StartAsync(desktop, guarded: false);
        }
        var window = BuildInstallLocationWindow(
            kind,
            move: async () => {
                replacedVersion = ReplacedVersion(plan);
                LifecycleLog.Write("install_started", ("action", plan.Action.ToString()), ("source", root), ("target", plan.Target),
                    ("from", plan.InstalledVersion), ("to", plan.SourceVersion), ("replaces", string.Join(", ", plan.Replaces)),
                    ("update_relaunch", Program.Host.UpdateRelaunch ? "true" : null));
                var outcome = await mover.MoveAsync(root!, _shutdown.Token);
                LifecycleLog.Write(outcome.Moved ? "install_completed" : "install_failed", ("target", outcome.InstalledPath),
                    ("error", outcome.Error), ("permission_denied", outcome.PermissionDenied ? "true" : null));
                if (outcome.Moved) {
                    if (plan.Action == ApplicationsInstallAction.OpenInstalled
                            && !await launcher.OpenAsync(outcome.InstalledPath!, _shutdown.Token))
                        return new MoveOutcome(false, outcome.InstalledPath, "Could not confirm kcap opened. Open the installed copy, then quit this window.");
                    Quit();
                }
                return outcome;
            },
            quit: installed ? NotNow : Quit, plan: plan,
            destinations: directories.Count > 1 ? directories : null,
            userApplicationsDirectory: userApps, quitLabel: installed ? "Not now" : null,
            keepable: directories.Count > 1 && ApplicationsMover.InstalledInEach(directories)
                ? directories.Where(directory => Mover(directory).Inspect(root!).RetiresOthersIn(directories)).ToArray()
                : null,
            selectDestination: directory => {
                mover = Mover(directory);
                plan = mover.Inspect(root!);
                return plan;
            });
        shown = window;
        desktop.MainWindow = window;
        window.Show();
        // The user already chose to update; a relaunch that lands here only has the move left to finish it.
        if (Program.Host.UpdateRelaunch && directories.Count == 1 && plan.Action != ApplicationsInstallAction.Blocked)
            Dispatcher.UIThread.Post(window.StartMove);
        return true;
    }

    async Task RetirePreviousBundleAsync(string? bundleRoot) {
        try {
            await Task.Run(() => new ApplicationRetirement(_config, _time).RunPendingAsync(bundleRoot, _shutdown.Token));
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: could not remove the previous app bundle: {ex.Message}");
        } catch (OperationCanceledException) { }
    }

    /// An installed copy still prompts while an older bundle it supersedes remains: the daemon's service and the
    /// terminal link may point into that bundle, and it can be opened beside this one.
    internal static bool ShouldPromptToInstall(InstallLocationKind kind, string root, ApplicationsInstallPlan plan) =>
        !InstallLocation.Passes(kind) || Path.GetFullPath(root) != Path.GetFullPath(plan.Target) || plan.Replaces.Count > 0;

    /// The installed copy finishes an update when it replaces an earlier version, or when this copy is itself the
    /// relaunch of one, already updated in place under another name; a first install has nothing to finish.
    void RecordInstallerHandover(ApplicationsInstallPlan plan, string? previous) {
        if (plan.SourceVersion is not { } to) return;
        var from = previous is not null && PrereleaseSemver.IsNewer(to, previous) ? previous : null;
        if (from is null && !Program.Host.UpdateRelaunch) return;
        try {
            AppUpdateTransition.Begin(AppUpdateTransition.Installer, from, to, _time).Save(_config);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not record the installer handover: {ex.Message}");
        }
    }

    /// Read before the move: by the time the installed copy is opened, the bundles it replaces have been set aside.
    static string? ReplacedVersion(ApplicationsInstallPlan plan) =>
        plan.InstalledVersion ?? plan.Replaces.Select(root => KcapBundle.Read(root)?.Version).FirstOrDefault(v => v is not null);

    Task<bool> QuitInstalledAppAsync(string root, CancellationToken ct) =>
        InstalledAppProcesses.QuitAsync(root, _time, ct);

    static bool InstalledAppRunning(string root) => InstalledAppState(root) != false;

    static bool? InstalledAppState(string root) => InstalledAppProcesses.Find(root) is { } pids ? pids.Length > 0 : null;

    internal static InstallLocationWindow BuildInstallLocationWindow(InstallLocationKind kind, Func<Task<MoveOutcome>> move, Action quit,
            ApplicationsInstallPlan? plan = null, IReadOnlyList<string>? destinations = null,
            Func<string, ApplicationsInstallPlan>? selectDestination = null,
            string? userApplicationsDirectory = null, string? quitLabel = null, IReadOnlyCollection<string>? keepable = null) {
        var where = kind switch {
            InstallLocationKind.DmgVolume   => "kcap is running from the disk image.",
            InstallLocationKind.Translocated => "kcap is running from a temporary location.",
            InstallLocationKind.Applications or InstallLocationKind.UserApplications =>
                $"An existing kcap installation was found at {plan?.ExistingPath ?? plan?.Target}.",
            _                                => "kcap is not in your Applications folder.",
        };
        return new InstallLocationWindow(where, move, quit, plan, destinations, selectDestination,
            userApplicationsDirectory, quitLabel, keepable);
    }

    // The ONE resolve+evaluate composition (OnboardingGate.EvaluateAsync), wrapped in the
    // never-brick degrade: the verdict and the resolution come back together, so the daemon identity
    // cannot be read off a second one, and the post-wizard build re-runs this rather than reusing a
    // startup value the wizard may have invalidated.
    internal static Task<(GateResult Gate, ProfileContext? Profiles)> ResolveAndEvaluateGateAsync(
            ConfigRoot config, TokenStore tokenStore, ProfileOverrides env, LocalRoot local, TimeProvider time,
            CancellationToken ct) =>
        EvaluateGateSafelyAsync(new OnboardingGate(config, tokenStore, env, local, time).EvaluateAsync, ct);

    // An incomplete gate builds no daemon graph while the wizard owns the app, and the graph is then
    // built against a fresh resolution, because the wizard is what may have changed the answer. A
    // close that leaves local setup pending reopens the wizard instead: that graph would come up
    // over a half-installed edition. Only an install that can still be retried reopens it; one that
    // cannot hands over to the graph, which an incomplete gate brings up degraded. A quit builds nothing. Only the wizard's handoff can report a
    // lane still live, and a graph built over one must not also drive automatic actions.
    internal static async Task RunWizardThenBuildGraphAsync(
            GateResult gate, ProfileContext? profiles, bool showWizard,
            Func<GateResult, ProfileContext?, Task<bool>> runWizard,
            Func<Task<(GateResult Gate, ProfileContext? Profiles)>> reevaluate,
            Action<GateResult, ProfileContext?, bool> buildGraph, Func<bool> localSetupPending, CancellationToken shutdown,
            Func<bool>? setupCannotRetry = null) {
        var laneQuiesced = true;
        if (showWizard) {
            do {
                laneQuiesced = await runWizard(gate, profiles).ConfigureAwait(true);
                if (shutdown.IsCancellationRequested) return;
                (gate, profiles) = await reevaluate().ConfigureAwait(true);
            } while ((gate is GateResult.Incomplete { Reason: GateReason.LocalSetupPending } || localSetupPending())
                  && setupCannotRetry?.Invoke() != true && !shutdown.IsCancellationRequested);

            if (shutdown.IsCancellationRequested) return;
        }

        buildGraph(gate, profiles, laneQuiesced);
    }

    /// The install's last outcome offers neither Try again nor Back: a reopened wizard could only be quit.
    internal static bool LocalSetupCannotRetry(LocalInstallController? install) =>
        install?.Outcome is LocalInstallOutcome.Failed { Affordance: LocalInstallAffordance.Permanent or LocalInstallAffordance.Busy };

    internal static bool ShouldShowWizard(GateResult gate, string[]? args) =>
        gate is GateResult.Incomplete || args?.Contains("--setup", StringComparer.Ordinal) == true;

    // The steady-state graph, over the resolution the gate was evaluated on (never a second resolve).
    void BuildDaemonGraph(
            IClassicDesktopStyleApplicationLifetime desktop, DaemonMutationLane lane, OutcomeChannel channel,
            GateResult gate, ProfileContext? profiles, bool laneQuiesced) {
        var service = DaemonClientService.CreateResolved(_daemonStore, profiles?.Resolution, _time, lane.RunAsync);

        // Incomplete after the wizard (abandoned, or sign-in skipped) is the carve-out arm, and so
        // is a lane that outran the handoff cap: the graph comes up with every lifecycle
        // auto-action closed and the shim auto-offer suppressed.
        var autoActionsPermanentlyClosed = AutoActionsPermanentlyClosed(gate, laneQuiesced);

        // One LocalControlOps and one AppNotifier for the whole app: the tray menu and the
        // window rows share a single stop/open-in-web code path and a single toast/stderr channel.
        var ops      = new LocalControlOps(_daemonStore, service.DaemonName, _time);
        var notifier = new AppNotifier();

        var restartPending = new DaemonRestartPendingWatcher(
            _daemonStore, service.DaemonName, service.Status, _time, _shutdown.Token);
        restartPending.Start();
        _restartPending = restartPending;
        // The surfaces show this status; the lifecycle controller keeps acting on the service's own.
        var expectedRestart = new ExpectedDaemonRestart(service.Status, restartPending.Pending, AppJustUpdated, service.RestartLoopAsync, _time,
            (expected, lasted) => LifecycleLog.Write("daemon_outage_ended", ("expected", expected ? "true" : "false"),
                ("seconds", lasted.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)), ("after_update", _postUpdate is null ? null : "true")));
        _expectedRestart = expectedRestart;
        var shownService = new StatusOverlayDaemonClientService(service, expectedRestart.Status);

        // BehaviorSubjects, not plain Subjects: MainWindowViewModel and TrayViewModel subscribe
        // later, and a Status/Attention call this early must not be dropped for want of a subscriber.
        var lifecycleStatus    = new BehaviorSubject<string?>(null);
        var lifecycleAttention = new BehaviorSubject<string?>(null);

        // The controller's attach subscription must be live BEFORE service.Start() begins pumping,
        // or the startup phase could miss the first terminal outcome it hinges on.
        var (lifecycle, shimOffer, consentFlip, lifecycleSurface, lifecycleProbe, lifecycleCli) = BuildLifecycleController(
            service, ops, autoActionsPermanentlyClosed,
            // One lock per lane for setting and withdrawing, so a withdrawal never erases a message set beside it.
            message => { lock (lifecycleStatus) lifecycleStatus.OnNext(message); },
            message => { lock (lifecycleAttention) lifecycleAttention.OnNext(message); },
            message => {
                lock (lifecycleStatus) if (ReferenceEquals(lifecycleStatus.Value, message)) lifecycleStatus.OnNext(null);
                lock (lifecycleAttention) if (ReferenceEquals(lifecycleAttention.Value, message)) lifecycleAttention.OnNext(null);
            },
            lane.RunAsync, profiles?.Resolution, () => lane.IsRetired(service.DaemonName));
        lifecycle.RestartExpected = () => expectedRestart.Expected;
        lifecycle.Start();
        _lifecycle = lifecycle;
        // Subscribe-before-run doesn't matter here (Offerable replays); always started so manual install keeps working in Incomplete mode — autoOfferSuppressed skips only the dialog.
        shimOffer.Start();
        _shimOffer = shimOffer;

        consentFlip.Start();
        _consentFlip = consentFlip;

        // After the graph, never during the wizard: the first check waits its own delay, and every
        // dialog goes through the same serialized surface as the skew and shim dialogs.
        // The ready dialog's TCS resolves on the thread pool, so its accept continuation (and
        // therefore this quit) can run off the UI thread — post it back before shutting down.
        _updates = new UpdateCoordinator(_updater, lifecycleSurface, _time, quit: () => Dispatcher.UIThread.Post(() => {
                ShowUpdateClosingWindow();
                desktop.TryShutdown();
            }), _shutdown.Token,
            handingOver: HandOver(AppUpdateTransition.InApp), log: LifecycleLog);
        _updateCheckSubscription = _updates.CheckProgress.Subscribe(progress =>
            Dispatcher.UIThread.Post(() => ShowUpdateCheckProgress(progress)));
        _updates.Start();
        if (_updater.IsAvailable) _appMenu.SetCheckForUpdatesAction(CheckForAppUpdatesAsync);

        // Said once, here, because nothing else in the app would: the graph is up but deliberately degraded.
        AnnounceUnquiescedLane(lifecycleSurface, laneQuiesced);

        // Composition-root outcome consumer: shares lifecycle's own surface/probe/CliVersion and the lane itself, so a Takeover accept re-mutates through the same gate.
        _ = ConsumeMutationOutcomesAsync(
            channel, lifecycleSurface, lane.RunAsync, lifecycleProbe.TerminalPathAsync, () => lifecycle.CliVersion, _shutdown.Token);

        service.Start();
        _service = service;

        var ticker = new UiTicker();
        _pause = new PauseController(ops, notifier.Notify, _shutdown.Token);
        // ConfirmForceStopAsync reads _coordinator at INVOCATION time (a captured field, not
        // a captured value) — safe even though _coordinator is still null right here, because
        // nothing can trigger a protected-kind stop before ShowMainWindow below assigns it.
        var opener = new ShellUrlOpener();
        // Built here rather than with the other server clients below because a remote stop goes
        // over the hub: the lane must exist before the service that calls it. Start() still runs
        // down there, with the rest of the server graph.
        var serverLane = new ServerConnectionService(profiles, ServerCredentials(profiles), _time, _localRoot);
        var actions = new AgentActionService(
            ops, notifier, opener, service.Snapshots, _shutdown.Token, ConfirmForceStopAsync,
            fallbackServerUrl: profiles?.Resolution.ServerUrl, lane: serverLane);

        // One instance: the prompt window factory nudges it on every conclusive ack and
        // MainWindowViewModel renders it.
        var activity = new ActivityViewModel(
            () => ConsentDecisionLogReader.ReadTail(_daemonStore, service.DaemonName, 200),
            () => ActivityStatKey(_daemonStore, service.DaemonName), ticker);
        _activity = activity;
        var launchApprovals = new LaunchApprovalsCoordinator(activity);
        _launchApprovals = launchApprovals;
        _menuBar?.SetLaunchApprovalsAction(launchApprovals.Show);

        // The prompt window is built per raise, never here: the coordinator owns its lifetime
        // and each window gets its own ViewModel over the one shared service.
        var consent = new ConsentService(
            service, ops, ticker, ct => ConsentSubscription.RunAsync(_daemonStore, service.DaemonName, ct),
            _time, _shutdown.Token);
        _consent = consent;

        _promptCoordinator = new ConsentPromptCoordinator(consent, () => new ConsentPromptWindow {
            DataContext = new ConsentPromptViewModel(
                consent, notifier, ticker, _time, _shutdown.Token, activity.RequestRefresh),
            Notifier = notifier,
        });

        // One launch client and one work-context source for the app, not one per window the
        // coordinator builds — each owns a live transport, and only a held instance can be
        // disposed at teardown.
        var workContext = new ServerWorkContextSource(_config, profiles, _serverEnv, _machineEnv, localRoot: _localRoot);
        var pullRequests = new ServerPullRequestSource(_config, profiles, _serverEnv, _machineEnv, _time, localRoot: _localRoot);
        var plans = new ServerPlanSource(_config, profiles, _serverEnv, _machineEnv, localRoot: _localRoot);
        var planArtifacts = new ServerPlanArtifactSource(_config, profiles, _serverEnv, _machineEnv, localRoot: _localRoot);
        var senders = new ServerMessageSenderSource(_config, profiles, _serverEnv, _machineEnv, localRoot: _localRoot);
        var ghRunner = new ProcessRunner(_time);
        var gh = new GitHubCliRunner(ghRunner, OperatingSystem.IsWindows() ? null : new LoginShellProbe(ghRunner, Environment.GetEnvironmentVariable), Environment.GetEnvironmentVariable);
        var serverClients = new ServerClients(serverLane, workContext, pullRequests, plans, planArtifacts, senders);

        var machineId = new MachineId(_config).ReadPersisted();
        var sessionHttp = ServerHttp(profiles);
        var remoteAgents = new RemoteAgentsService(
            serverLane, RemoteAgentsService.HttpFetch(sessionHttp, profiles),
            postRename: RemoteAgentsService.HttpRename(sessionHttp, profiles),
            time: _time,
            onUnauthorized: serverLane.ParkSignedOut);
        var sessions = new ServerSessionsService(
            serverLane, ServerSessionsService.HttpFetch(sessionHttp, profiles), ServerSessionsService.HttpOwners(sessionHttp, profiles),
            _time, onUnauthorized: serverLane.ParkSignedOut, fetchSummary: ServerSearchHttp.Summary(sessionHttp, profiles),
            fetchHandoffContext: ServerSearchHttp.HandoffContext(sessionHttp, profiles));
        var repoIdentity = new RepoIdentityResolver();
        var directory = new AgentDirectory(
            service, remoteAgents, serverLane, repoIdentity, GitRepository.ResolveMainRepoRoot,
            machineId, profiles?.Resolution.ServerUrl, _time, sessions);
        _remoteAgents = remoteAgents;
        _sessions = sessions;
        _directory = directory;
        // Registration order is precedence: local CLI readers before the server.
        var readers = new PullRequestReaderRegistry(new ListedPullRequestSource(pullRequests, directory),
            [_gh = new GitHubCliReaderProvider(gh, _time), new ServerReaderProvider(pullRequests)], _time);
        var railPreferences = new RailPreferencesStore(_appState);

        // The launcher's model dropdown, fetched once from the server (same catalog the web UI
        // uses). Best-effort: a miss leaves the curated per-vendor fallback in place.
        var modelCatalog = new ServerVendorModelCatalog(ServerVendorModelCatalog.HttpFetch(sessionHttp, profiles));
        _modelCatalog = modelCatalog;
        _ = modelCatalog.LoadAsync(_shutdown.Token);

        var reportCliSetup = ServerCliSetupReport.Sender(sessionHttp, profiles, ReportedCliVersion);
        _ = reportCliSetup(_shutdown.Token);

        // After the directory, which feeds it the session→agent map: a server-lane item names a
        // session, and only that map turns it into the agent whose card it belongs on.
        var readDetail = ServerSessionHttp.DetailReader(sessionHttp, profiles);
        var readSubagent = ServerSessionHttp.SubagentEventsReader(sessionHttp, profiles);
        var uploader = new ServerAttachmentUploader(sessionHttp, profiles);
        var permissions = new PermissionService(
            service, ops, ct => PermissionSubscription.RunAsync(_daemonStore, service.DaemonName, ct),
            _time, _shutdown.Token, ServerSessionHttp.Responder(sessionHttp, profiles),
            sessionAgents: directory.SessionAgents);
        _permissions = permissions;
        var accounts = new AccountsService(service, ct => AccountsSubscription.RunAsync(_daemonStore, service.DaemonName, ct), _time, _shutdown.Token);
        _accounts = accounts;
        lane.AccountsRevisionReached = (revision, ct) => accounts.ReachedAsync(revision, TimeSpan.FromSeconds(10), ct);
        var sessionAccess = new SessionAccessService(serverLane, _time);
        var permissionFeed = new ServerPermissionFeed(
            serverLane, sessionAccess, permissions, readDetail, directory.VendorOfSession, _time);
        var attention = new SessionAttentionTracker(serverLane, readDetail, _time);
        _sessionAccess = sessionAccess;
        // Sessions the rail hides are not read: nothing would show their PRs.
        var railShown = new ReplaySubject<Func<AgentRow, bool>>(1);
        var pullRequestTones = new PullRequestToneCache(directory, readers, TimeProvider.System, railShown);
        _pullRequestTones = pullRequestTones;
        _permissionFeed = permissionFeed;
        _attention = attention;

        // Only now: the lane's permission, elicitation and settlement streams are hot and replay
        // nothing, so anything pushed before the feed and the tracker are subscribed is lost.
        serverLane.Start();

        // The rail pips on both halves: agents with a card in the cache, plus sessions the tracker
        // knows are waiting but the app has never opened, mapped back to their agent.
        var agentsWithPending = permissions.AgentsWithPending.CombineLatest(
            attention.SessionsWithAttention, directory.SessionAgents,
            (cards, sessions, map) => (IReadOnlySet<string>)cards
                .Concat(sessions.Select(s => map.GetValueOrDefault(s, "")).Where(a => a.Length > 0))
                .ToHashSet(StringComparer.Ordinal));

        // The signed-in user's own id (JwtClaims sub claim), read fresh per call — never cached,
        // since a re-auth or profile switch must be reflected on the very next read.
        var credentials = ServerCredentials(profiles);
        Func<CancellationToken, Task<string?>> viewerId = async ct => {
            if (credentials is null) return null;
            var token = (await credentials.ResolveAsync(ct).ConfigureAwait(false)).Bearer;
            return token is null ? null : JwtClaims.TryGetString(token, "sub");
        };

        ILaunchClient launch = serverLane;
        _serverClients = serverClients;
        // The single restart trigger for a completed sign-in — RestartAsync serializes rather
        // than coalesces, so RefreshAfterReauthAsync deliberately does not also await it. The model
        // catalog reloads here too: the endpoint needs auth, so a signed-out start left it empty and
        // only a successful sign-in can fill it.
        // Before any pane's own sign-in handler, which runs on the UI thread after this.
        var pullRequestSnapshots = new LastReads<PullRequestSnapshot>();
        serverClients.SignInCompleted.Subscribe(signedIn => {
            pullRequestSnapshots.Clear();
            _ = serverLane.RestartAsync();
            _ = modelCatalog.LoadAsync(_shutdown.Token);
            _ = reportCliSetup(_shutdown.Token);
        });

        // One attach client per attempt, dialed at the daemon's own control socket; 80x24 is a
        // placeholder only — TerminalControl resizes its model to the real pane the moment it is
        // attached to the visual tree (WorkspaceView's own header comment).
        var attachFactory = CoreTerminalAttachClient.Factory(() => _daemonStore.SocketPath(service.DaemonName));
        var isLocalServer = IsLocalServer(profiles?.Resolution.ServerUrl, _localRoot);
        var requestSignIn = SignInRequestFor(isLocalServer, () => {
            // Reachable in the carve-out arm (gate Incomplete after an abandoned wizard), where the
            // rail can show disconnected with no server to re-auth against.
            if (profiles?.Resolution.ServerUrl is not { } serverUrl || !OnboardingGate.ValidServerUrl(serverUrl)) {
                notifier.Notify("No server is configured. Run kcap setup first.");
                return;
            }
            OpenSignInDialog(profiles.Name, serverUrl, refreshAppState: true);
        });
        // The bundled CLI's own coordinator, never a hook: the stack follows the CLI this app ships.
        // The upgrade and a search change both take the installation, so each holds the other off; both start on the UI thread.
        LocalSearchController? localSearch = null;
        var localUpgrade = isLocalServer
            ? new LocalUpgradeController(() => lifecycleCli, action => Dispatcher.UIThread.Post(action), held: () => localSearch?.Running == true)
            : null;
        _localUpgrade = localUpgrade;
        localSearch = localUpgrade is null ? null
            : new LocalSearchController(() => lifecycleCli, action => Dispatcher.UIThread.Post(action), held: () => localUpgrade.Running);
        _localSearch = localSearch;
        var localStack = localUpgrade is null ? null : new LocalStackViewModel(
            () => LocalEndpoints.Read(_localRoot).Active?.Version, serverLane.Status, localUpgrade, lifecycleCli.LocalStartAsync,
            () => serverLane.RestartAsync(), _shutdown.Token, searchChange: localSearch) {
            Search = localSearch is null ? null : new LocalSearchViewModel(
                () => LocalEmbeddingFile.Read(_localRoot), ct => LocalBuiltinOffer.LookupAsync(_localRoot, ct), serverLane.Status, localSearch, localUpgrade, () => serverLane.RestartAsync(),
                _shutdown.Token, openSettings: _openSearchSettings, builtinModelReady: () => LocalBuiltinOffer.InstalledModelReady(_localRoot)),
        };
        _localStack = localStack;
        IObservable<bool>? stackOwnsStart = null;
        if (localStack is not null) stackOwnsStart = localStack.WhenAnyValue(x => x.SuppressesServerStart);
        var localServer = new LocalServerRecoveryViewModel(
            serverLane.Status, isLocalServer, lifecycleCli.LocalStartAsync, () => serverLane.RestartAsync(), _shutdown.Token,
            busy: stackOwnsStart);
        _localServer = localServer;
        var webWorkspace = new WebWorkspaceViewModel(profiles?.Resolution.ServerUrl, opener, notifier.Notify,
            _shutdown.Token, localServer);
        _webWorkspace = webWorkspace;

        var feedbackTrailer = FeedbackTrailerFeed(service, CapacitorVersion.CurrentDisplay(), () => lifecycle.CliVersion);
        // A resolved server and nothing more — deliberately wider than Settings, which also needs a
        // profile name for its store.
        var feedbackApi = sessionHttp is null ? null : _serverHttp!.GetRequiredService<IFeedbackApi>();
        // A local installation has no support intake, so its reports go out as email drafts.
        Func<FeedbackSubmission, FeedbackMailOutcome>? feedbackMail = isLocalServer
            ? s => FeedbackMail.Send(s, SystemBrowser.Instance, Capacitor.Cli.Core.Auth.Clipboard.TryCopy)
            : null;
        Action<FeedbackCategory>? openFeedback = feedbackApi is null
            ? null
            : category => OpenFeedback(feedbackApi, feedbackTrailer, requestSignIn, category, feedbackMail);
        _menuBar?.SetFeedbackAction(openFeedback);

        var appSubject = SignedInSubject.From(serverLane.Status);
        var commands = new BackgroundCommandActivity((key, report, ended, handoff) =>
            new BackgroundCommandMonitor(key, report, ended, service, directory, sessionAccess, readDetail, serverLane, _time, handoff));
        _commands = commands;
        Action linkGitHub = () => {
            if (profiles?.Resolution.ServerUrl is { Length: > 0 } url)
                LinkPolicy.Open(opener, url.TrimEnd('/') + "/auth/github-link/start");
        };
        // Workspace links only: the explicit "Open in web" actions keep the browser.
        var workspaceOpener = new AgentLinkOpener(opener, profiles?.Resolution.ServerUrl,
            agentId => _coordinator?.Window?.DataContext is MainWindowViewModel vm && vm.OpenSession(agentId));
        var drafts = new ComposerDrafts();
        var transcripts = new LocalTranscripts();
        var readings = new AccountReadings(accounts.Snapshot, remoteAgents.DrawableDaemonAccounts, UsageClock.Create(_time));
        _usageClock = readings.Clock;
        WorkspaceViewModel BuildWorkspace(string agentId) => new(
            agentId, service, actions, attachFactory, () => new XtermTerminalSurface(80, 24, PtyDumpPath), _time, workspaceOpener, permissions,
            workContext, ops, uploader,
            requestSignIn: requestSignIn, signInCompleted: serverClients.SignInCompleted, pullRequests: readers,
            linkGitHub: linkGitHub,
            access: sessionAccess, localDaemonOnAppServer: directory.LocalDaemonOnAppServer, directory: directory, plans: plans, planArtifacts: planArtifacts,
            commands: commands, drafts: drafts, inputSending: attention.ReplySending,
            resumeLedger: id => ResumeLedgerStore.For(_daemonStore, service.DaemonName).TryRead(id),
            pullRequestSnapshots: pullRequestSnapshots, transcripts: transcripts,
            pullRequestLinksChanged: pullRequestTones.Refresh, readings: readings, senders: senders,
            appSubject: appSubject, notifier: notifier);
        // The origin lookup below and this call are two reads of a cache the directory's own
        // background recompute mutates, so the row can be gone by the time this runs: no row, no
        // host, and the click opens nothing.
        RemoteSessionViewModel? BuildRemote(string agentId) =>
            directory.Rows.Lookup($"remote:{agentId}") is { HasValue: true, Value: var row }
                ? new RemoteSessionViewModel(row, directory, sessionAccess, permissions, actions, serverLane, readDetail, workspaceOpener, _time, workContext,
                    () => new XtermTerminalSurface(80, 24, PtyDumpPath), planArtifacts, commands,
                    pullRequests: readers, requestSignIn: requestSignIn, linkGitHub: linkGitHub,
                    signInCompleted: serverClients.SignInCompleted, plans: plans, drafts: drafts, inputSending: attention.ReplySending,
                    pullRequestSnapshots: pullRequestSnapshots, pullRequestLinksChanged: pullRequestTones.Refresh, readSubagent: readSubagent,
                    readings: readings, senders: senders, remoteAgents: remoteAgents, notifier: notifier, appSubject: appSubject,
                    serverSessions: sessions)
                : null;
        // A session the server lists with no daemon behind it: read-only, so no terminal surface.
        RemoteSessionViewModel? BuildServer(string sessionId) =>
            directory.Rows.Lookup($"server:{sessionId}") is { HasValue: true, Value: var row }
                ? new RemoteSessionViewModel(row, directory, sessionAccess, permissions, actions, serverLane, readDetail, workspaceOpener, _time, workContext,
                    surfaceFactory: null, planArtifacts, commands,
                    pullRequests: readers, requestSignIn: requestSignIn, linkGitHub: linkGitHub,
                    signInCompleted: serverClients.SignInCompleted, plans: plans, drafts: drafts, inputSending: attention.ReplySending,
                    pullRequestSnapshots: pullRequestSnapshots, pullRequestLinksChanged: pullRequestTones.Refresh, readSubagent: readSubagent,
                    readings: readings, remoteAgents: remoteAgents, notifier: notifier, appSubject: appSubject, serverSessions: sessions)
                : null;

        _coordinator = new MainWindowCoordinator(
            () => TrackViewedAgent(BuildAndShowMainWindow(
                shownService, _config, _appState, actions, notifier, ticker,
                _shutdown.Token, activity, launch, _time, lifecycle.StartActionAsync,
                lifecycleStatus, _navigation, _workspaceTeardown.Track, BuildWorkspace,
                // The tenant slug the rail footer shows — profiles are named after it at sign-in.
                tenantName: profiles?.Resolution?.ProfileName, agentsWithPending: agentsWithPending,
                agentsAwaitingAnswer: permissions.AgentsAwaitingAnswer, agentsRunningCommands: commands.Running,
                requestSignIn: requestSignIn,
                lifecycleAttention: lifecycleAttention, identityReport: lifecycle.IdentityReport,
                pullRequestSummaries: pullRequestTones.Summaries, railShown: railShown,
                directory: directory, remoteAgents: remoteAgents, lane: serverLane, localAccounts: accounts.Snapshot, readings: readings,
                viewerId: viewerId, localMachineId: machineId, restartPending: restartPending.Pending,
                // A row present on both lanes is the local one: the local socket is the richer
                // workspace, and the directory only keeps both rows when the twin is unproven.
                originOf: id => directory.Rows.Lookup($"local:{id}").HasValue || directory.Rows.Lookup($"pending:{id}").HasValue ? AgentOrigin.Local
                    : directory.Rows.Lookup($"remote:{id}").HasValue ? AgentOrigin.Remote
                    : directory.Rows.Lookup($"server:{id}").HasValue ? AgentOrigin.Server
                    : null,
                remoteWorkspaceFactory: BuildRemote, serverWorkspaceFactory: BuildServer,
                sessions: sessions, railPreferences: railPreferences, profileName: profiles?.Name,
                modelCatalog: modelCatalog.Catalog, uploader: uploader, appServerUrl: profiles?.Resolution.ServerUrl,
                openFeedback: openFeedback, settingsAction: _appMenu.SettingsAction, checkForUpdatesAction: _appMenu.CheckForUpdatesAction,
                uninstallAction: _appMenu.UninstallAction,
                adopt: TakeAdoptableWindow(), show: !_holdingMainWindow,
                requestSetup: () => _ = RestartForSetupAsync(desktop),
                historyImport: _historyImport?.Run,
                backgroundPriority: lifecycle.BackgroundPriority, reloadState: lifecycle.ReloadState,
                isReloading: lifecycle.IsReloading, reloadDaemon: lifecycle.ReloadServiceAsync,
                movingToWindowsService: lifecycle.IsMovingToWindowsService,
                repairDaemon: lifecycle.RepairStaleMarkerAsync, repairOffer: lifecycle.RepairOffer,
                localServer: localServer, localStack: localStack, uiScale: _uiScale, usageNotice: _usageNotice,
                web: webWorkspace, openLaunchApprovals: launchApprovals.Show,
                search: string.IsNullOrEmpty(profiles?.Resolution.ServerUrl) ? null : ServerSearchHttp.Search(sessionHttp, profiles))),
            // Both close paths release the workspace: hide-to-tray keeps the window (and its
            // attach) alive, a real close discards the window the next Show() would rebuild.
            releaseWorkspace: window => (window.DataContext as MainWindowViewModel)?.CloseWorkspace());
        // A shutdown that started before this continuation resumed already ran its first
        // pass against a null coordinator, so a window built now must never be
        // close-protected (BeginShutdownPass's rule 1 is the general defense; this is the
        // by-construction one, and it is why the window below cannot even briefly intercept).
        _coordinator.QuitInProgress = _shutdownStarted;
        var componentsOwed = AppComponentStamp.Owed(_config, CapacitorVersion.CurrentDisplay());
        _postUpdate ??= InferUpdate(isLocalServer && localUpgrade is not null && componentsOwed);
        // A launch that finishes an update opens on its progress; the main window follows once that settles.
        _holdingMainWindow = _postUpdate is not null && !_shutdownStarted;
        if (_holdingMainWindow) {
            _coordinator.Prepare();
            // A setup window adopted as the main window is already on screen; it waits for the hand-over too.
            _coordinator.Window?.Hide();
        } else {
            _coordinator.ShowMainWindow();
            _updateHandedOver.TrySetResult();
        }
        desktop.MainWindow = _coordinator.Window;
        // BuildAndShowMainWindow constructs Home and the rail itself (over the same `service`), so
        // they are read back off the window's DataContext for disposal.
        _home = (_coordinator.Window?.DataContext as MainWindowViewModel)?.Home;
        _rail = (_coordinator.Window?.DataContext as MainWindowViewModel)?.Rail;

        var localSkipped = new BehaviorSubject<bool>(false);
        if (!_shutdownStarted) _ = SettleComponentsAsync(localUpgrade, isLocalServer, () => localSkipped.OnNext(true));
        if (_holdingMainWindow && _postUpdate is { } finishing) {
            _staleDaemonNudge = new StaleDaemonNudge(finishing.To, service.Status, service.Snapshots.Select(snap => (string?)snap.Daemon.Version),
                restartPending.Pending, ct => ops.RequestRestartAsync(StaleDaemonNudge.Mode, ct),
                result => LifecycleLog.Write("daemon_restart_requested", ("mode", StaleDaemonNudge.Mode), ("result", result)), _time, _shutdown.Token);
            ShowUpdateFinishWindow(finishing, expectedRestart.Status,
                service.Snapshots.Select(snap => ((string?)snap.Daemon.Version, (string?)snap.Daemon.Connection)),
                restartPending.Pending,
                localUpgrade is null || localStack is null || !isLocalServer || !componentsOwed
                    ? Observable.Return<LocalStepInput?>(null)
                    : localStack.WhenAnyValue(x => x.Upgrading, x => x.Headline, x => x.Detail, (upgrading, headline, detail) => (upgrading, headline, detail))
                        .CombineLatest(localUpgrade.WhenAnyValue(x => x.Outcome), localSkipped,
                            Observable.Switch(localUpgrade.WhenAnyValue(x => x.Progress).Select(p => p.Changed.Select(_ => p).StartWith(p))),
                            (banner, outcome, skipped, p) => (LocalStepInput?)new LocalStepInput(banner.upgrading, outcome, banner.headline,
                                banner.detail, skipped, p.Phase, p.From, p.To, p.DownloadFraction)));
        }

        if (!_shutdownStarted) {
            var notificationSettings = new NotificationSettingsService(_config.Path("notifications.json"));
            _notificationSettings = notificationSettings;
            var notificationSink = Program.Host.CreateNotificationSink(a => Dispatcher.UIThread.Post(a));
            var notificationAccess = DesktopNotifications.AccessOf(notificationSink);
            _notificationSink = notificationSink;
            _notificationAccess = notificationAccess;
            _notificationAccessPrompt = new DesktopNotificationAccessPrompt(
                notificationAccess, notificationSettings.Changes,
                Window.IsActiveProperty.Changed.Where(change => change.NewValue.GetValueOrDefault())
                    .Select(_ => System.Reactive.Unit.Default),
                () => !_shutdownStarted && desktop.Windows.Any(window => window.IsActive),
                ReactiveUI.Reactive.RxSchedulers.MainThreadScheduler);
            _desktopNotifications = new DesktopNotificationCoordinator(
                permissions, directory, notificationSettings.Changes, _notificationSink,
                () => _shutdownStarted || desktop.Windows.Any(window => window.IsActive), _viewedAgent,
                row => {
                    if (_shutdownStarted) return;
                    _coordinator.ShowMainWindow();
                    (_coordinator.Window?.DataContext as MainWindowViewModel)?.OpenSession(row.Id, row.Origin);
                }, notifier, ReactiveUI.Reactive.RxSchedulers.MainThreadScheduler);
            _notificationSessions = new NotificationSessionSubscriptions(
                directory.SessionAgents, sessionAccess.Acquire,
                ReactiveUI.Reactive.RxSchedulers.MainThreadScheduler);
        }

        Action<bool>? openSettingsOn = profiles?.Resolution is { ProfileName: { Length: > 0 } profileName, ServerUrl: { Length: > 0 } serverUrl }
            ? showSearch => OpenSettings(desktop, new SettingsProfileStore(_config, profileName, serverUrl), service, ops, lane, notifier,
                lifecycle.PhaseClosed, lifecycle.InstallWindowsServiceAsync, lifecycleCli.ServiceStatusAsync, showSearch)
            : null;
        Action? openSettings = openSettingsOn is null ? null : () => openSettingsOn(false);
        _openSearchSettings.OnNext(openSettingsOn is null ? null : () => openSettingsOn(true));
        ConfigureSettingsMenu(openSettings);
        _appMenu.SetSetupAction(() => _ = RestartForSetupAsync(desktop));
        if (OperatingSystem.IsMacOS() && InstallLocation.BundleRoot(Environment.ProcessPath) is { } bundleRoot) {
            var facts = UninstallFactsFor(bundleRoot, service.DaemonName, profiles?.Resolution.ServerUrl, lifecycle, lifecycleProbe);
            _appMenu.SetUninstallAction(() => _ = UninstallFromAppAsync(desktop, facts));
        } else if (OperatingSystem.IsLinux() && AppImageInstall.Detect(Environment.GetEnvironmentVariable) is not null) {
            // The links and the CLI copy live under the staged root, so a kcap that resolves there is this app's.
            var facts = UninstallFactsFor(AppImageInstall.StagedRoot(Environment.GetEnvironmentVariable, _userHome),
                service.DaemonName, profiles?.Resolution.ServerUrl, lifecycle, lifecycleProbe);
            _appMenu.SetUninstallAction(() => _ = UninstallFromAppAsync(desktop, facts));
        }

        // LAST, deliberately: anything above throwing lands in the catch with no
        // tray icon ever created, leaving the error window as the only surface.
        _trayVm = new TrayViewModel(
            shownService, _pause, actions, consent, openMainWindow: _coordinator.ShowMainWindow,
            quit: () => desktop.TryShutdown(), openReviewPrompts: _promptCoordinator.ShowPromptWindow,
            lifecycleAttention: lifecycleAttention, shimOfferable: shimOffer.Offerable,
            installShim: shimOffer.RunManualInstallAsync, permissions: permissions,
            remote: TrayViewModel.SummaryFrom(directory, attention.SessionsWithAttention),
            updateMenu: _updates.MenuItem, updateAction: RunUpdateMenuActionAsync,
            restartPending: restartPending.Pending, openSettings: openSettings, openLaunchApprovals: launchApprovals.Show);
        _tray = new TrayIconManager(this, _trayVm);
        LifecycleLog.Write("tray_created", ("executable", Environment.ProcessPath));
    }

    async Task SetBetaUpdatesAsync(bool enabled) {
        await _betaUpdates.SetAsync(enabled);
        if (_updates is { } updates) await updates.RecheckAsync();
    }

    internal void ConfigureSettingsMenu(Action? openSettings) => _appMenu.SetSettingsAction(openSettings);

    async Task RestartForSetupAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        if (_setupRestarting || _uninstalling || _shutdownStarted) return;
        _setupRestarting = true;
        try {
            if (!await ShowLifecyclePromptDialogAsync(_coordinator?.Window,
                SetupRestartPrompt(_historyImport?.Run is { Running: true }), _shutdown.Token)) {
                _setupRestarting = false;
                return;
            }
            var start = Program.Host.NewCopy("--setup");
            desktop.Exit += (_, _) => {
                try { Process.Start(start); }
                catch (Exception ex) { Console.Error.WriteLine($"kcap: could not reopen setup: {ex.Message}"); }
            };
            desktop.TryShutdown();
        } catch (Exception ex) {
            _setupRestarting = false;
            Console.Error.WriteLine($"kcap: could not reopen setup: {ex.Message}");
            _coordinator?.Window?.Notifier?.Notify("Could not reopen setup. Please try again.");
        }
    }

    UninstallFactsSource UninstallFactsFor(string bundleRoot, string daemonName, string? profileServerUrl,
            DaemonLifecycleController lifecycle, ILoginShellProbe probe) => new(
        LocalServerUrl: () => LocalServerFact.Read(_localRoot),
        AttachedDaemon: daemonName,
        Activity: () => lifecycle.LatestActivity,
        LiveDaemons: () => DaemonPidLiveness.LiveDaemons(_daemonStore),
        HostedServerUrl: () => profileServerUrl is { Length: > 0 } && !IsLocalServer(profileServerUrl, _localRoot) ? profileServerUrl : null,
        TerminalPath: probe.TerminalPathAsync,
        BundleRoot: bundleRoot,
        TerminalPathTimeout: UninstallTerminalPathTimeout,
        Time: _time,
        Log: Console.Error,
        PathProfiles: () => OperatingSystem.IsMacOS()
            ? ShellProfile.Removable(_userHome, new KcapLinkRemover(KcapLinkFolders.For(_userHome), Path.Combine(bundleRoot, "Contents", "MacOS", "kcap")).Find())
            : []);

    // The same window owns preparation, removal, and the final acknowledgement.
    async Task UninstallFromAppAsync(IClassicDesktopStyleApplicationLifetime desktop, UninstallFactsSource source) {
        if (_uninstalling || _setupRestarting || _shutdownStarted) return;
        _uninstalling = true;
        var started = false;
        try {
            var request = UninstallConfirmation.Build(await source.GatherAsync(_shutdown.Token));
            if (!await ShowLifecyclePromptDialogAsync(_coordinator?.Window, request.Prompt, _shutdown.Token)) return;
            started = StartUninstall(desktop, new UninstallMode(request.PurgeLocal), prepareApp: true);
            if (!started)
                _coordinator?.Window?.Notifier?.Notify("Uninstall needs the installed kcap app. Run kcap uninstall in a terminal instead.");
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not start the uninstall: {ex.Message}");
            _coordinator?.Window?.Notifier?.Notify("Could not start the uninstall. Please try again.");
        } finally {
            if (!started) _uninstalling = false;
        }
    }

    internal static LifecyclePrompt SetupRestartPrompt(bool importRunning) => new(
        LifecyclePrompt.KindSetup, null, null, false,
        "kcap will restart to reopen setup. Your running sessions will keep going, and your settings and sign-in will stay intact."
        + (importRunning ? " Your history import will stop. Sessions already imported are kept; you can retry from setup." : ""));

    (KcapCli Cli, ILoginShellProbe Probe, ProcessRunner Runner) SettingsCli(SettingsProfileStore settings, string daemonName) {
        var runner = new ProcessRunner(_time);
        var probe = TerminalPathProbe.Create(runner, Environment.GetEnvironmentVariable);
        var cli = new KcapCli(runner,
            CliResolver.ResolvePath(Environment.GetEnvironmentVariable, File.Exists, Program.Host.CliDirectory),
            daemonName, settings.ProfileName, probe.TerminalPathAsync,
            canonicalServer: ServerIdentity.Canonicalize(settings.ServerUrl));
        return (cli, probe, runner);
    }

    HarnessesSettingsViewModel CreateHarnessesSettings(SettingsProfileStore settings, string daemonName) {
        var (cli, probe, runner) = SettingsCli(settings, daemonName);
        var harnesses = HarnessRegistry.FromEnvironment(_userHome);
        var shimInstaller = CliPathInstallers.Create(probe, _userHome);
        var shimTarget = ShimTargetFor(cli.CliPath, Program.Host.CliOutlivesApp);
        return new HarnessesSettingsViewModel(settings, cli,
            HarnessesStepViewModel.BuildDetectionFeed(probe, harnesses), _shutdown.Token,
            id => harnesses[id].Signals.IsWired,
            toolsMemory: _appState,
            ensureTerminalCommand: shimInstaller is null ? null : async ct => {
                // An inconclusive probe never offers an install, as in setup.
                if (await probe.KcapOnPathAsync(ct) != false) return null;
                // A CLI that leaves with the app (a mounted image) has nothing lasting to link.
                if (shimTarget is null)
                    return "kcap is not on your terminal PATH, and this copy cannot be linked there because it is removed when the app quits. Install kcap, then connect again.";
                var fix = new PathFixViewModel(shimInstaller, _appState, shimTarget);
                // A stop with nothing to say is still not an install, so the hooks must not go in.
                return await fix.InstallAsync(ct) ? null : fix.Message ?? "kcap could not be added to your terminal PATH. Try again.";
            });
    }

    async Task<VisibilityOptionsRead?> ReadSettingsVisibilityOptionsAsync(SettingsProfileStore settings, CancellationToken ct) {
        using var timeout = new CancellationTokenSource(WizardVisibilityOptionsTimeout, _time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try {
            await Task.Run(settings.LoadProfile, bounded.Token).ConfigureAwait(false);
            if (_serverHttp?.GetRequiredService<ICapacitorHttpClient>() is not { } http) return null;
            var (client, status, _, _) = await http.ForWaitAsync(bounded.Token).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return null;
                var read = await ServerVisibilityOptions.ReadAsync(client, settings.ServerUrl, bounded.Token).ConfigureAwait(false);
                if (read.Problem is { } problem) Console.Error.WriteLine($"kcap: settings visibility options read failed: {problem}");
                return read;
            }
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            Console.Error.WriteLine($"kcap: settings visibility options read failed: {ex.Message}");
            return null;
        }
    }

    void OpenSettings(IClassicDesktopStyleApplicationLifetime desktop, SettingsProfileStore settings,
            IDaemonClientService service, ILocalControlOps ops, DaemonMutationLane lane, IAppNotifier notifier, Task startupSettled,
            Func<CancellationToken, Task<MutationOutcome?>> moveToWindowsService,
            Func<CancellationToken, Task<ServiceSnapshot?>> serviceStatus, bool showSearch = false) {
        if (_shutdownStarted) return;
        if (_settingsWindow is { } open) {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            if (showSearch) open.ShowSearch();
            open.Activate();
            return;
        }

        ProfilesSettingsViewModel profilesVm;
        SettingsViewModel         vm;
        try {
            var tokenStore = _foreignHttp.GetRequiredService<TokenStore>();
            profilesVm = new ProfilesSettingsViewModel(
                _config, tokenStore, new OnboardingGate(_config, tokenStore, _serverEnv, _localRoot, _time), settings.ProfileName,
                openSignIn: (profile, serverUrl, _) => {
                    OpenSignInDialog(profile, serverUrl, refreshAppState: profile == settings.ProfileName);
                    return Task.CompletedTask;
                },
                confirmRemove: (name, ct) => ShowLifecyclePromptDialogAsync(_settingsWindow,
                    new LifecyclePrompt(LifecyclePrompt.KindRemoveProfile, null, null, false,
                        ProfilesSettingsViewModel.RemovePrompt(name, LatestDaemonProfile(service))), ct),
                appLifetime: _shutdown.Token);

            vm = new SettingsViewModel(settings, service, ops,
                async (name, ct) => (await LocalControlProbe.ProbeAsync(_daemonStore, name, _time, OneShotProbeTimeout, ct)).Reachable,
                lane.RunAsync, (prompt, ct) => ShowLifecyclePromptDialogAsync(_settingsWindow, prompt, ct),
                ct => RelaunchForSettingsAsync(desktop, _time, ct), Program.Host.CanRelaunchNow, startupSettled, lane.CanRetireAsync,
                nameOverridden: Environment.GetEnvironmentVariable("KCAP_DAEMON_NAME") is { Length: > 0 },
                needsAppRestart: lane.IsRetired(service.DaemonName), appLifetime: _shutdown.Token,
                notificationSettings: _notificationSettings, notificationAccess: _notificationAccess,
                profiles: profilesVm, moveToWindowsService: moveToWindowsService, serviceStatus: serviceStatus,
                harnesses: CreateHarnessesSettings(settings, service.DaemonName),
                capture: new CaptureSettingsViewModel(settings, _shutdown.Token,
                    isLocal: IsLocalServer(settings.ServerUrl, _localRoot),
                    visibilityOptions: ct => ReadSettingsVisibilityOptionsAsync(settings, ct))) {
                Accounts = _accounts is { } accounts
                    ? new AccountsSettingsViewModel(accounts, lane.RunAsync, service.DaemonName, settings.ProfileName,
                        discover: () => AccountDiscovery.Find(
                            _userHome, AccountStore.Beside(_daemonStore).TryLoad() ?? new AccountRegistry(), Environment.GetEnvironmentVariable),
                        pickDirectory: PickAccountDirectoryAsync, daemonStatus: service.Status, usageClock: _usageClock, appLifetime: _shutdown.Token)
                    : null,
                Appearance = new AppearanceSettingsViewModel(_uiScale),
                Privacy    = new PrivacySettingsViewModel(_telemetry, _config, _usageNotice),
                Search     = _localStack?.Search,
                Experimental = new ExperimentalSettingsViewModel(new ExperimentalSettingsStore(_config), settings.ProfileName, service),
                About = new AboutSettingsViewModel(service, SettingsCli(settings, service.DaemonName).Cli.VersionAsync, _localStack,
                    _updates?.MenuItem, _updates is not null ? RunUpdateMenuActionAsync : null,
                    followsBetas: () => _updater.AllowsPrerelease, setBetaUpdates: SetBetaUpdatesAsync, appLifetime: _shutdown.Token),
            };
        } catch (Exception ex) {
            notifier.Notify($"Could not open settings: {ex.Message}");
            return;
        }

        var window = new SettingsWindow { DataContext = vm, ContentScale = _uiScale };
        _settingsWindow = window;
        window.Closing += (_, e) => { if (vm.HasActiveOperation && !_shutdownStarted) e.Cancel = true; };
        window.Closed += (_, _) => { _settingsWindow = null; vm.Dispose(); };
        // The list is re-read whenever the window regains focus, so a `kcap use` or `kcap profile
        // add` made in a terminal shows up on return without a file watcher.
        window.Activated += (_, _) => _ = profilesVm.RefreshAsync();
        _ = profilesVm.RefreshAsync();
        _ = vm.About?.LoadAsync();
        if (showSearch) window.ShowSearch();
        _ = vm.Harnesses!.RescanAsync();
        _ = vm.Capture.RefreshVisibilityOptionsAsync();
        window.Show();
        window.Activate();
    }

    async Task<string?> PickAccountDirectoryAsync(CancellationToken ct) {
        if (_settingsWindow?.StorageProvider is not { } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Choose a config directory",
            AllowMultiple = false,
        });
        ct.ThrowIfCancellationRequested();
        return folders is [{ } folder] ? folder.TryGetLocalPath() : null;
    }

    // Deferred, so each window reads the CLI version installed when it opens rather than the one
    // known at startup — the seed is what a daemon that never reported carries. The daemon version
    // is the same stripped form the status line shows, so a report and that chip cannot disagree.
    internal static IObservable<string> FeedbackTrailerFeed(
            IDaemonClientService service, string appVersion, Func<string?> cliVersion) =>
        Observable.Defer(() => service.Snapshots
            // Snapshots arrive on the daemon client's pump thread and the trailer drives a bound
            // hint. BEFORE StartWith, so the seed still arrives synchronously on subscribe — the
            // hint would otherwise render an empty trailer for a beat.
            .ObserveOn(ReactiveUI.Reactive.RxSchedulers.MainThreadScheduler)
            .Select(s => FeedbackTrailer.Build(appVersion, service.DaemonName,
                MainWindowViewModel.StripBuildMetadata(s.Daemon.Version), cliVersion()))
            .StartWith(FeedbackTrailer.Build(appVersion, service.DaemonName, null, cliVersion())));

    internal void OpenFeedback(IFeedbackApi api, IObservable<string> trailer, Action? signIn, FeedbackCategory category,
            Func<FeedbackSubmission, FeedbackMailOutcome>? mail = null) {
        if (_shutdownStarted) return;
        if (_feedbackWindow is { } open) {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            ((FeedbackViewModel)open.DataContext!).Reopen(category);
            return;
        }

        var vm = new FeedbackViewModel(api, category, trailer, RuntimeInformation.OSDescription,
            signIn: signIn, appLifetime: _shutdown.Token, mail: mail);

        var window = new FeedbackWindow { DataContext = vm };
        _feedbackWindow = window;
        window.Closing += (_, e) => { if (vm.IsBusy && !_shutdownStarted) e.Cancel = true; };
        window.Closed += (_, _) => { _feedbackWindow = null; vm.Dispose(); };
        window.Show();
        window.Activate();
    }

    static async Task<bool> RelaunchForSettingsAsync(
            IClassicDesktopStyleApplicationLifetime desktop, TimeProvider time, CancellationToken ct) {
        if (!await Program.Host.StartSecondCopyAsync(desktop, time, ct)) return false;
        desktop.TryShutdown();
        return true;
    }

    /// The re-auth dialog over a fresh ReauthComposition graph, pinned to one profile and server.
    /// A graph is built per open — a settled attempt's rendered state must never leak into the next
    /// sign-in. The commit checks the profile still names the server, so a row edited while the
    /// browser was open is refused rather than stamped.
    void OpenSignInDialog(string profile, string serverUrl, bool refreshAppState) {
        if (_signInWindow is { } open) {
            open.Activate();
            return;
        }

        var graph = ReauthComposition.Build(
            _config,
            _foreignHttp.GetRequiredService<TokenStore>(),
            _foreignHttp.GetRequiredService<IHttpClientFactory>(),
            _foreignHttp.GetRequiredService<IAuthProxyClient>(),
            _foreignHttp.GetRequiredService<GitHubOAuthClient>(),
            _foreignHttp.GetRequiredService<WorkOSClient>(),
            profile, serverUrl,
            // A re-auth is not setup, so none of its sign-in reaches the setup funnel.
            WizardComposition.BuildBridges(action => Dispatcher.UIThread.Post(action), CliTelemetry.Disabled(_time), _endpoints),
            new ConsentFlipClaims(_config),
            _appState,
            new ShellUrlOpener(),
            _time,
            WizardComposition.NewOperation,
            refreshAppState,
            new CommitPrecondition.ExpectServer(serverUrl));
        var window = new SignInWindow { DataContext = graph.SignIn };

        // Hold the dialog on success so "Signed in…" is readable, refresh app state in parallel,
        // then close. Closed below is still the ONE finish path for cancel/quiesce.
        void OnSignInChanged(object? _, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(SignInStepViewModel.Satisfied) && graph.SignIn.Satisfied)
                _ = CloseSignInAfterSuccessAsync(window, refreshAppState);
        }

        graph.SignIn.PropertyChanged += OnSignInChanged;
        window.Closed += (_, _) => {
            graph.SignIn.PropertyChanged -= OnSignInChanged;
            _signInWindow = null;
            _reauthSettle = FinishSignInAsync(graph, refreshAppState);
        };

        _signInWindow = window;
        window.Show();
    }

    async Task CloseSignInAfterSuccessAsync(Window window, bool refreshAppState) {
        var refresh = refreshAppState ? RefreshAfterReauthAsync() : Task.CompletedTask;
        try {
            await Task.WhenAll(refresh, Task.Delay(SignInStepViewModel.SuccessHold, _time)).ConfigureAwait(true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: post-sign-in refresh failed: {ex.Message}");
        }

        if (ReferenceEquals(_signInWindow, window)) window.Close();
    }

    /// Clears the launcher's expired/disconnected sign-in banner path, nudges work-context
    /// refresh, and kicks attach so snapshots catch up sooner after new tokens land. The server
    /// lane's own restart is NOT awaited here — NotifySignInCompleted's subscription
    /// (BuildDaemonGraph) is the single trigger for it.
    async Task RefreshAfterReauthAsync() {
        _home?.NotifySignInCompleted();
        _serverClients?.NotifySignInCompleted();
        if (_service is not null) await _service.RestartLoopAsync().ConfigureAwait(true);
    }

    /// Fire-and-forget from the dialog's Closed handler — nothing may block the UI close. The
    /// quiesce is what stops a still-running attempt from committing after the window is gone.
    async Task FinishSignInAsync(ReauthGraph graph, bool refreshAppState) {
        try {
            await graph.CloseAsync(CancellationToken.None);
            // Success path already refreshed before close; call again so a manual close after
            // Satisfied still clears the banner if the hold was interrupted.
            if (refreshAppState && graph.SignIn.Satisfied) await RefreshAfterReauthAsync();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: sign-in dialog teardown failed: {ex.Message}");
        }
    }

    // Wizard-first mode: no service, no tray, no lifecycle controller, no
    // coordinators — just the wizard, the app-lifetime lane, and the SAME outcome consumer over a
    // wizard-local surface. Returns once the wizard has closed AND the channel has been handed on;
    // false means the lane outran the handoff cap and the graph must close its auto-actions.
    async Task<bool> RunWizardModeAsync(
            IClassicDesktopStyleApplicationLifetime desktop, DaemonMutationLane lane, OutcomeChannel channel,
            IProcessRunner runner, ILoginShellProbe probe, ProfileContext? profiles, bool alreadyAuthenticated) {
        var cliPath = CliResolver.ResolvePath(Environment.GetEnvironmentVariable, File.Exists, Program.Host.CliDirectory);
        // Same rule as the shim coordinator's: only a resolved ABSOLUTE path is linkable.
        var shimTarget = ShimTargetFor(cliPath, Program.Host.CliOutlivesApp);
        var shimInstaller = CliPathInstallers.Create(probe, _userHome);
        var shimApplicable = await ResolveShimApplicableAsync(
            shimInstaller is not null, shimTarget, ct => probe.KcapOnPathAsync(ct), _shutdown.Token);
        var bridges = WizardComposition.BuildBridges(action => Dispatcher.UIThread.Post(action), _telemetry, _endpoints);
        var surface = new WizardLifecycleSurface(ConfirmLifecyclePromptAsync, action => Dispatcher.UIThread.Post(action));

        var options = new WizardGraphOptions(
            _config,
            _foreignHttp.GetRequiredService<TokenStore>(),
            _foreignHttp.GetRequiredService<IHttpClientFactory>(),
            _foreignHttp.GetRequiredService<IAuthProxyClient>(),
            _foreignHttp.GetRequiredService<GitHubOAuthClient>(),
            _foreignHttp.GetRequiredService<WorkOSClient>(),
            // Nothing resolved means the gate's evaluation threw; sign-in then targets the name every
            // fallback lands on, which is what an unresolved config would have answered anyway.
            profiles?.Name ?? ProfileConfig.DefaultName,
            new ConsentFlipClaims(_config),
            bridges,
            WizardComposition.NewOperation,
            surface,
            ResolveCli: () => NewWizardCli(_config, _serverEnv, _localRoot, runner, cliPath, probe),
            ResolveOps: name => new LocalControlOps(_daemonStore, name, _time),
            ResolveIdentity: () => ResolveWizardIdentity(_config, _serverEnv, _localRoot),
            ResolveConsentFlipIdentity: () => ResolveConsentFlipIdentity(_config, _localRoot),
            RunMutation: lane.RunAsync,
            Observation: new OneShotObservation(_daemonStore, _time, OneShotProbeTimeout),
            AppState: _appState,
            ShimInstaller: shimInstaller,
            UrlOpener: new ShellUrlOpener(),
            Probe: probe,
            DetectionFeed: probe => HarnessesStepViewModel.BuildDetectionFeed(probe, HarnessRegistry.FromEnvironment(_userHome)),
            BuiltinOffer: ct => LocalBuiltinOffer.LookupAsync(_localRoot, ct),
            CliPath: cliPath,
            ShimApplicable: shimApplicable,
            ShimTarget: shimTarget,
            DefaultDaemonName: ResolveWizardIdentity(_config, _serverEnv, _localRoot)?.DaemonName,
            Time: _time,
            ShutdownToken: _shutdown.Token,
            LocalRoot: _localRoot,
            LocalEditionSupported: OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64,
            AlreadyAuthenticated: alreadyAuthenticated,
            ConfiguredDaemonName: () => ResolveConfiguredDaemonName(_config, _serverEnv, _localRoot),
            HeldDaemonNames: HeldDaemonNamesAsync,
            ProfileOverride: _serverEnv.Profile,
            UsageNotice: _usageNotice,
            ReportCliSetup: ReportWizardCliSetupAsync,
            ReadVisibilityOptions: ReadWizardVisibilityOptionsAsync,
            RepairClients: StaleClientRepair.ForApp(runner, probe, shimTarget, _userHome).RunAsync,
            MailSupport: report => FeedbackMail.Send(
                new FeedbackSubmission(FeedbackCategory.Bug, report, Guid.NewGuid(), FeedbackSource.Desktop),
                SystemBrowser.Instance, Capacitor.Cli.Core.Auth.Clipboard.TryCopy),
            ResumeWebSignUp: WizardComposition.ResumesWebSignUp(await _appState.LoadAsync(), _time.GetUtcNow()));
        _localInstall ??= WizardComposition.NewInstallController(options);
        var graph = WizardComposition.BuildGraph(options with { Install = _localInstall });

        _wizardAuth = graph.Auth;
        _historyImport = graph.History;
        var window = ShowWizardWindow(desktop, graph.ViewModel, graph.Install, () => _shutdown.IsCancellationRequested, _uiScale);
        _wizardWindow = window;

        // The SAME consumer function, over the wizard's own surface: outcome presentation stays
        // single-owner while no tray or main window exists. No CLI version to disclose here —
        // wizard mode builds no lifecycle controller to have probed one.
        _ = ConsumeMutationOutcomesAsync(
            channel, surface, lane.RunAsync, probe.TerminalPathAsync, () => null, _shutdown.Token,
            WizardAttentionCopyFor);

        await WaitForWizardCloseAsync(graph.ViewModel, _shutdown.Token);
        if (graph.PathFix is { } pathFix) _wizardPathLink = pathFix.UnfinishedAsync();
        var quiesced = await HandoffAfterWizardAsync(
            graph.Auth, graph.Install, () => DaemonThenLaneQuiescedAsync(graph.Daemon, lane), QuiesceShutdownCap, channel, _time);

        _wizardAuth = null;
        if (QuitsAfterWizardClose(graph.ViewModel, windowClosed: !window.IsVisible) && !_shutdown.IsCancellationRequested)
            desktop.TryShutdown();
        // A finished onboarding leaves its window up for the main surface to take over; a window the
        // user closed is gone, and the main surface builds its own.
        if (window.IsVisible) {
            // A pending setup reopens a fresh wizard, so this one is not left beside it.
            if (_shutdown.IsCancellationRequested || LocalSetupState.Pending(_localRoot)) window.Close();
            else {
                var state = await _appState.LoadAsync();
                if (_shutdown.IsCancellationRequested) window.Close();
                else if (window.IsVisible) _adoptableWindow = (window, state);
            }
        }
        _wizardWindow = null;

        return quiesced;
    }

    // The enabler first: its name save and status reads run outside the lane, and the mutation it
    // ends in enters the lane only after them.
    static async Task DaemonThenLaneQuiescedAsync(DaemonEnabler daemon, DaemonMutationLane lane) {
        await daemon.QuiescedAsync().ConfigureAwait(false);
        await lane.QuiescedAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // Avoid a shell spawn when no PATH installer or absolute CLI path is available.
    internal static async Task<bool> ResolveShimApplicableAsync(
            bool hasInstaller, string? shimTarget, Func<CancellationToken, Task<bool?>> probeKcapOnPath, CancellationToken ct) {
        if (!hasInstaller || shimTarget is null) return false;

        return PathFixViewModel.ComputeApplicable(
            hasInstaller, shimTarget, await ProbeKcapOnPathSafelyAsync(probeKcapOnPath, ct).ConfigureAwait(true));
    }

    // A probe failure reads as unknown, never as "offer it anyway" — ComputeApplicable's own null
    // rule. A cancellation matching the caller's token is a quit, not an unknown: it propagates, so
    // no wizard window is built after the app has started shutting down (EvaluateGateSafelyAsync's rule).
    static async Task<bool?> ProbeKcapOnPathSafelyAsync(Func<CancellationToken, Task<bool?>> probe, CancellationToken ct) {
        try {
            return await probe(ct).ConfigureAwait(true);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: PATH probe failed — not offering the PATH fix: {ex.Message}");
            return null;
        }
    }

    // Rebuilt per call (LateBoundKcapCli): the wizard writes the profile, server and daemon name
    // while its steps run, so a binding pinned at composition time would query the wrong service.
    internal static IKcapCli NewWizardCli(
            ConfigRoot config, ProfileOverrides env, LocalRoot local, IProcessRunner runner, string? cliPath, ILoginShellProbe probe) {
        var identity = ResolveWizardIdentity(config, env, local);

        return new KcapCli(
            runner, cliPath, identity?.DaemonName ?? "daemon", identity?.Profile ?? ProfileConfig.DefaultName,
            probe.TerminalPathAsync, canonicalServer: identity?.Server);
    }

    // The main window, with the onboarding pane where the rail and launcher go. ShutdownMode is
    // deliberately untouched (OnExplicitShutdown, pinned in OnFrameworkInitializationCompleted):
    // closing the window never exits the app by itself; QuitsAfterWizardClose decides after the handoff.
    internal static MainWindow ShowWizardWindow(
            IClassicDesktopStyleApplicationLifetime desktop, OnboardingViewModel wizard,
            LocalInstallController? install = null, Func<bool>? shuttingDown = null, UiScaleSetting? uiScale = null) {
        var window = new MainWindow {
            Onboarding = wizard, ContentScale = uiScale,
            Width = 760, Height = 760, WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        // Detached once the flow has closed, so the main surface's own hide-on-close is the window's
        // only close behaviour after handover.
        void OnClosing(object? sender, WindowClosingEventArgs e) {
            // Teardown settles setup itself; requesting handover here would race that cleanup.
            if (shuttingDown?.Invoke() == true) return;
            var userClose = e is { CloseReason: WindowCloseReason.WindowClosing, IsProgrammatic: false };
            if (WizardWindowClosing(wizard, install, userClose)) e.Cancel = true;
        }
        window.Closing += OnClosing;
        wizard.CloseRequested += () => window.Closing -= OnClosing;

        desktop.MainWindow = window;
        window.Show();

        return window;
    }

    /// <summary>
    /// Whether the window must stay open. A user's close during a local install interrupts it and keeps
    /// the wizard on Install, so the rollback and its settled outcome stay in view; the next close goes
    /// through. A quit is never held: the shutdown path settles the install itself.
    /// </summary>
    internal static bool WizardWindowClosing(OnboardingViewModel wizard, LocalInstallController? install, bool userClose) {
        if (userClose && install is { Running: true }) {
            install.Interrupt();
            return true;
        }

        wizard.RequestClose();
        return false;
    }

    /// Before a server is connected the main window has nothing to show, so closing setup's window quits
    /// rather than handing over; the next launch resumes setup. Read after the handoff, so a sign-in it
    /// lets commit still opens the app. The pre-flight's Quit leaves either way.
    internal static bool QuitsAfterWizardClose(OnboardingViewModel wizard, bool windowClosed) =>
        wizard.QuitRequested || (windowClosed && !wizard.Deferrable);

    // One logical close (the Done step's finish and the window's own Closing both route through
    // RequestClose, which is idempotent). Shutdown ends the wait too — a quit must never sit
    // behind a window the user is no longer looking at.
    internal static async Task WaitForWizardCloseAsync(OnboardingViewModel wizard, CancellationToken ct) {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCloseRequested() => closed.TrySetResult();

        wizard.CloseRequested += OnCloseRequested;
        if (wizard.Closed) closed.TrySetResult();
        await using var registration = ct.Register(() => closed.TrySetResult());
        try {
            await closed.Task.ConfigureAwait(true);
        } finally {
            wizard.CloseRequested -= OnCloseRequested;
        }
    }

    /// <summary>Close boundary: settle the local install and sign-in, quiesce the lane under the cap,
    /// then transfer the channel. A history import keeps running into the main window.</summary>
    internal static async Task<bool> HandoffAfterWizardAsync(
            WizardAuthService? auth, LocalInstallController? install, Func<Task> laneQuiescedAsync, TimeSpan cap,
            OutcomeChannel channel, TimeProvider time) {
        if (install is not null) await install.SettleAsync().ConfigureAwait(false);
        await CancelAndAwaitAuthAsync(auth).ConfigureAwait(false);
        var quiesced = await AwaitQuiescedAsync(laneQuiescedAsync, cap, time).ConfigureAwait(false);
        channel.TransferConsumer();

        return quiesced;
    }

    // AuthAttempt.Result never faults (an operation that throws arrives as Failed), so this is a
    // pattern-match, not a try/catch: only a failure has anything left to say at this point.
    internal static async Task<AuthResult?> CancelAndAwaitAuthAsync(WizardAuthService? auth) {
        if (auth?.Current is not { } attempt) return null;

        attempt.Cancel();
        var result = await attempt.Result.ConfigureAwait(false);

        switch (result) {
            case AuthResult.Failed failed:
                Console.Error.WriteLine($"kcap: onboarding sign-in ended with a failure: {failed.Message}");
                break;
            case AuthResult.Committed or AuthResult.Cancelled or AuthResult.Retarget:
                // Committed is already durable and the fresh resolution below reads it; the other
                // two wrote nothing. Nothing to surface either way.
                break;
        }

        return result;
    }

    // The error window shuts down directly, bypassing normal teardown, so all live services
    // and windows must be released before showing it.
    internal static async Task HandleStartupFailureAsync(
            IClassicDesktopStyleApplicationLifetime desktop, Exception ex, DaemonClientService? service,
            CancellationTokenSource shutdown, IReadOnlyList<IDisposable?> uiDisposables,
            DaemonLifecycleController? lifecycle = null, DaemonMutationLane? lane = null, LocalUpgradeController? upgrade = null,
            LocalSearchController? search = null, AppMenuBar? menuBar = null, MainWindowCoordinator? coordinator = null) {
        menuBar?.SetLaunchApprovalsAction(null);
        if (coordinator is not null) coordinator.QuitInProgress = true;
        shutdown.Cancel();
        desktop.MainWindow?.Close();
        desktop.MainWindow = null;

        // The dependent goes first (it subscribes to service's streams) — same ordering rule as
        // the normal shutdown path below. Its own DisposeAsync cancels its independent lifetime
        // token and waits out any mutation it started; that wait is unbounded here on purpose — a
        // startup failure has no live UI to defer against, so there is nothing to keep responsive.
        if (lifecycle is not null) {
            try {
                await lifecycle.DisposeAsync();
            } catch (Exception disposeEx) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon lifecycle controller during startup-failure cleanup: {disposeEx}");
            }
        }
        if (service is not null) {
            try {
                await service.DisposeAsync();
            } catch (Exception disposeEx) {
                // The ORIGINAL startup exception (ex, already captured and about to be shown
                // below) must never be masked by a secondary dispose failure — append it to the
                // same Console.Error channel instead of letting it propagate.
                Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during startup-failure cleanup: {disposeEx}");
            }
        }
        // The lane goes LAST: both lifecycle and service can still be calling its RunAsync until their own disposal above completes.
        if (lane is not null) {
            try {
                await lane.DisposeAsync();
            } catch (Exception disposeEx) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon mutation lane during startup-failure cleanup: {disposeEx}");
            }
        }
        // The error window's close shuts down directly, so a running upgrade settles here or not at
        // all; the cancel first keeps one whose journal read is still in flight from spawning.
        if (upgrade is not null) {
            shutdown.Cancel();
            await upgrade.SettleAsync();
        }
        if (search is not null) {
            shutdown.Cancel();
            await search.SettleAsync();
        }
        // Same rule, same reason, for whatever the success path had already built when it threw
        // (tray icon first): the error window's close handler force-shuts-down, so this is their
        // only cleanup too. Entries are null when that step was never reached.
        DisposeAll(uiDisposables, "startup-failure cleanup");
        ShowStartupError(desktop, ex);
    }

    // Split out of the catch so a test can drive it against a fake
    // IClassicDesktopStyleApplicationLifetime (no real windowing/desktop lifetime needed) and
    // assert the ShutdownMode pin, the MainWindow assignment, and the deferred Shutdown(1) all
    // happen in the right order.
    internal static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception ex) {
        // Also pinned app-wide, but this path's exit code depends on it: under OnLastWindowClose the
        // CLR Closed handler's Shutdown(1) runs before the routed WindowClosedEvent, whose
        // TryShutdown() then overwrites the exit code with 0.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Showing a window is legal before Avalonia's main loop starts (StartWithClassicDesktopLifetime
        // does the same); calling desktop.Shutdown(1) directly throws when startup faulted
        // synchronously, before the main loop began.
        var errorWindow = BuildStartupErrorWindow(ex);
        if (desktop.MainWindow is null) desktop.MainWindow = errorWindow;
        errorWindow.Closed += (_, _) => desktop.Shutdown(1);
        errorWindow.Show();
    }

    /// The app theme's brush, or null before the theme has loaded, which leaves the default.
    static IBrush? KcapBrush(string key) =>
        Current?.TryGetResource(key, null, out var brush) == true ? brush as IBrush : null;

    // Last-resort UI for a startup failure: Console.Error above is invisible on a normal GUI
    // launch (OutputType=WinExe has no console), so this window is the only channel that
    // actually reaches the user. SelectableTextBlock (not TextBlock) keeps the stack trace
    // copyable for a bug report.
    internal static Window BuildStartupErrorWindow(Exception ex) =>
        new() {
            Title = "kcap — startup failed",
            Icon = ProductIcon.WindowIcon,
            Width = 640,
            Height = 400,
            Background = KcapBrush("KcapCanvasBrush"),
            Foreground = KcapBrush("KcapTextBrush"),
            Content = new ScrollViewer {
                Content = new SelectableTextBlock {
                    Text = $"The app failed to start. Details:\n{ex}",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

    // The MainWindowCoordinator's window factory, split out of StartAsync so a test can drive
    // "build VM+window, assign, and Show()" against a fake service without needing a real
    // daemon/profile. It must call Show() itself: StartWithClassicDesktopLifetime calls
    // ShowMainWindow() exactly once, right after Start, while desktop.MainWindow is still null
    // because startup awaits (the gate's config read, and in wizard-first mode the whole wizard).
    // Show() on an already-visible window is a no-op.
    /// The profile the attached daemon reports, or null when no daemon is attached now or it does not
    /// say. Both streams replay their latest value on subscribe, and a snapshot outlives a detach.
    static string? LatestDaemonProfile(IDaemonClientService service) {
        string? profile = null;
        var attached = false;
        using (service.Status.Subscribe(s => attached = s.State == AttachState.Connected)) { }
        using (service.Snapshots.Subscribe(s => profile = s.Daemon.Profile)) { }
        return attached ? profile : null;
    }

    internal static MainWindow BuildAndShowMainWindow(
            IDaemonClientService service, ConfigRoot config, IAppStateStore appState,
            AgentActionService actions, IAppNotifier notifier, ITicker ticker,
            CancellationToken shutdownToken, ActivityViewModel activity, ILaunchClient launch,
            TimeProvider time,
            Func<CancellationToken, Task>? startAction = null,
            IObservable<string?>? lifecycleStatus = null,
            NavigationGate? navigation = null, Action<Func<Task>>? trackWorkspaceTeardown = null,
            Func<string, WorkspaceViewModel>? workspaceFactory = null, string? tenantName = null,
            IObservable<IReadOnlySet<string>>? agentsWithPending = null,
            IObservable<IReadOnlySet<string>>? agentsAwaitingAnswer = null, Action? requestSignIn = null,
            IObservable<string?>? lifecycleAttention = null, IObservable<string?>? identityReport = null,
            IObservable<IReadOnlyDictionary<string, int>>? agentsRunningCommands = null,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? pullRequestSummaries = null,
            IObserver<Func<AgentRow, bool>>? railShown = null,
            IAgentDirectory? directory = null, IRemoteAgentsService? remoteAgents = null,
            IServerLane? lane = null, IObservable<AccountsSnapshotDto?>? localAccounts = null,
            Func<CancellationToken, Task<string?>>? viewerId = null,
            string? localMachineId = null, IObservable<bool>? restartPending = null,
            Func<string, AgentOrigin?>? originOf = null,
            Func<string, RemoteSessionViewModel?>? remoteWorkspaceFactory = null,
            IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>? modelCatalog = null,
            IAttachmentUploader? uploader = null, string? appServerUrl = null,
            Action<FeedbackCategory>? openFeedback = null, IObservable<Action?>? settingsAction = null,
            IObservable<Func<Task>?>? checkForUpdatesAction = null,
            IObservable<Action?>? uninstallAction = null, (MainWindow Window, AppState State)? adopt = null, bool show = true,
            Action? requestSetup = null, HistoryImportRun? historyImport = null,
            IObservable<bool>? backgroundPriority = null, IObservable<ReloadState?>? reloadState = null,
            IObservable<bool>? isReloading = null, Func<CancellationToken, Task>? reloadDaemon = null,
            IObservable<bool>? movingToWindowsService = null,
            LocalServerRecoveryViewModel? localServer = null, LocalStackViewModel? localStack = null,
            UiScaleSetting? uiScale = null, UsageDataNotice? usageNotice = null,
            WebWorkspaceViewModel? web = null, Action? openLaunchApprovals = null,
            Func<string, RemoteSessionViewModel?>? serverWorkspaceFactory = null, IServerSessionsService? sessions = null,
            RailPreferencesStore? railPreferences = null, AccountReadings? readings = null,
            Func<CancellationToken, Task>? repairDaemon = null, IObservable<string?>? repairOffer = null,
            string? profileName = null, Func<SearchRequest, CancellationToken, Task<SearchFetch>>? search = null) {
        // Notifier is set on the WINDOW (the toast overlay), not the ViewModel — the toast
        // is a View-level concern (WindowNotificationManager lives on MainWindow) independent of
        // the VM's WhenActivated-scoped projections.
        //
        // Home is built here, over the SAME `service` instance MainWindowViewModel itself uses —
        // never a second daemon connection. The composition root passes its held server lane so
        // teardown can dispose it; a caller building its own graph (a test) passes a stub.
        //
        // Home's three navigation callbacks close over `vm`, which cannot exist yet (it takes Home
        // itself) — a captured local, assigned right below, is what ties the knot without a
        // settable hook on either ViewModel. Every callback runs on the UI thread, after both
        // objects exist.

        // A caller with no live remote lane (most existing tests) falls back to the local-only
        // stand-in — NoRemoteAgents/NoServerLane never report a remote agent, so the directory
        // just mirrors `service`. The composition root always supplies its own `directory`.
        var resolvedDirectory = directory ?? new AgentDirectory(
            service, remoteAgents ?? new NoRemoteAgents(), lane ?? new NoServerLane(), new RepoIdentityResolver(),
            GitRepository.ResolveMainRepoRoot, localMachineId, appServerUrl: null, time);

        readings ??= new AccountReadings(
            localAccounts ?? Observable.Return<AccountsSnapshotDto?>(null),
            remoteAgents?.DrawableDaemonAccounts ?? Observable.Return<IReadOnlyList<DaemonAccountsDto>>([]),
            UsageClock.Create(time));

        MainWindowViewModel? vm = null;
        var home = new HomeViewModel(
            service, appState,
            launch, new RepoPathStore(config, time).GetSortedPathsAsync, time, shutdownToken,
            navigationGeneration: () => vm?.NavigationGeneration ?? 0,
            openSessionIfCurrent: (agentId, origin, generation) => vm?.OpenSessionIfCurrent(agentId, origin, generation),
            requestSignIn: requestSignIn,
            daemons: remoteAgents?.Daemons, viewerId: viewerId, laneStatus: lane?.Status,
            localMachineId: localMachineId, launchFailures: lane?.LaunchFailures, directory: resolvedDirectory,
            modelCatalog: modelCatalog, uploader: uploader, appServerUrl: appServerUrl,
            listing: remoteAgents?.Listing, daemonAccounts: remoteAgents?.DaemonAccounts, localAccounts: localAccounts,
            retryListing: remoteAgents is null ? null : remoteAgents.RefreshDaemonsAsync, readings: readings,
            launchFailed: agentId => vm?.CloseFailedLaunch(agentId)) { LocalStack = localStack, UsageNotice = usageNotice };
        if (localServer is not null) home.AttachLocalServerRecovery(localServer);
        if (repairDaemon is not null && repairOffer is not null) home.AttachServiceRepair(repairDaemon, repairOffer, shutdownToken);
        // Same knot as home above, over the SAME `service` instance — its callbacks close over
        // `vm`, not a local, so no two-step forward-declaration is needed.
        // Both rail actions route through the one call, each naming the lane of the row that was
        // clicked: an unproven twin pair keeps a row on each lane under the same id, and the VM's
        // own lookup would open the local one for both.
        // Read once before the rail exists, like the window size, so it opens in the shape of the
        // user this profile last resolved to; the lane's own answer rebinds it below.
        RailPreferences? initialRail = null;
        string? railUser = null;
        Func<string, Task<RailPreferences>>? bindRailUser = null;
        Action<RailPreferences>? persistRail = null;
        if (railPreferences is { } prefs && appServerUrl is { Length: > 0 } url && profileName is { Length: > 0 } name) {
            initialRail = prefs.LoadAsync(url, name).GetAwaiter().GetResult();
            railUser = prefs.LastUserAsync(url, name).GetAwaiter().GetResult();
            bindRailUser = async userId => {
                railUser = userId;
                return await prefs.BindUserAsync(url, name, userId).ConfigureAwait(false);
            };
            persistRail = set => { if (railUser is { } userId) _ = prefs.SaveAsync(url, userId, set); };
        }
        var rail = new SessionRailViewModel(
            resolvedDirectory, openLocalSession: agentId => vm?.OpenSession(agentId, AgentOrigin.Local),
            openRemoteSession: agentId => vm?.OpenSession(agentId, AgentOrigin.Remote), time: time, agentsWithPending: agentsWithPending,
            agentsAwaitingAnswer: agentsAwaitingAnswer, agentsRunningCommands: agentsRunningCommands,
            pullRequests: pullRequestSummaries, openPullRequest: (agentId, origin, subject) => vm?.OpenSessionPullRequest(agentId, origin, subject),
            openServerSession: sessionId => vm?.OpenSession(sessionId, AgentOrigin.Server),
            sessions: sessions, preferences: initialRail, persistPreferences: persistRail,
            scopeLocked: localServer?.IsLocalServer == true, shownRows: railShown, bindUserPreferences: bindRailUser);
        // The page mutates on whatever thread its inputs arrive on, so every input lands on the UI thread.
        Func<Func<string, PinPresentation, CancellationToken, Task<SearchOpenOutcome>>, SessionSearchViewModel>? searchFactory =
            search is null || sessions is null ? null : open => new SessionSearchViewModel(
                search,
                sessions.Identity.ObserveOn(RxSchedulers.MainThreadScheduler),
                (lane?.SessionAccessChanged ?? Observable.Never<string>()).ObserveOn(RxSchedulers.MainThreadScheduler),
                time, RxSchedulers.MainThreadScheduler, open);
        vm = new MainWindowViewModel(
            service, shutdownToken, activity, time, startAction, lifecycleStatus, home: home,
            navigation: navigation, trackWorkspaceTeardown: trackWorkspaceTeardown, workspaceFactory: workspaceFactory,
            rail: rail, tenantName: tenantName, lifecycleAttention: lifecycleAttention, identityReport: identityReport,
            laneStatus: lane?.Status, restartPending: restartPending,
            originOf: originOf, remoteWorkspaceFactory: remoteWorkspaceFactory, directory: resolvedDirectory,
            serverWorkspaceFactory: serverWorkspaceFactory, sessions: sessions,
            searchFactory: searchFactory,
            openFeedback: openFeedback, opener: new ShellUrlOpener(), requestSignIn: requestSignIn,
            settingsAction: settingsAction, checkForUpdatesAction: checkForUpdatesAction,
            uninstallAction: uninstallAction, requestSetup: requestSetup, historyImport: historyImport,
            backgroundPriority: backgroundPriority, reloadState: reloadState, isReloading: isReloading, reloadDaemon: reloadDaemon, openLaunchApprovals: openLaunchApprovals,
            movingToWindowsService: movingToWindowsService) {
            LocalStack = localStack,
            Web = web,
        };
        var window = adopt?.Window ?? new MainWindow();
        window.DataContext = vm;
        window.Closed += (_, _) => vm.Search?.Dispose();
        window.Notifier = notifier;
        window.ContentScale = uiScale;
        window.Onboarding = null;
        var savedState = adopt?.State ?? appState.LoadAsync().GetAwaiter().GetResult();
        WindowSizeMemory.Apply(window, savedState);
        WindowSizeMemory.Attach(window, appState);
        PaneLayoutMemory.Apply(window.Panes, savedState);
        PaneLayoutMemory.Attach(window.Panes, appState);
        if (show) window.Show();
        return window;
    }

    /// Publishes the directory row key of the agent open in the main window while that window is active.
    MainWindow TrackViewedAgent(MainWindow window) {
        if (window.DataContext is not MainWindowViewModel vm) return window;
        var subscription = window.GetObservable(Window.IsActiveProperty)
            .CombineLatest(vm.WhenAnyValue(x => x.CurrentWorkspace), (active, open) => !active ? null : open switch {
                RemoteSessionViewModel { IsSessionOnly: true } session => $"server:{session.AgentId}",
                RemoteSessionViewModel remote => $"remote:{remote.AgentId}",
                { } local => $"local:{local.AgentId}",
                null => null,
            })
            .DistinctUntilChanged()
            .Subscribe(_viewedAgent.OnNext);
        window.Closed += (_, _) => subscription.Dispose();
        return window;
    }

    // Wires the CLI facade and PATH probe (a broken override is "no CLI"); also builds the shim/consent-flip coordinators, sharing rather than re-resolving them.
    (DaemonLifecycleController Lifecycle, ShimOfferCoordinator ShimOffer, ConsentFlipCoordinator ConsentFlip,
            ILifecycleSurface Surface, ILoginShellProbe Probe, IKcapCli Cli) BuildLifecycleController(
            DaemonClientService service, ILocalControlOps ops, bool autoActionsPermanentlyClosed,
            Action<string> setLifecycleStatus, Action<string> setLifecycleAttention, Action<string> withdrawLifecycleAttention,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            ResolvedProfile? profile, Func<bool> requiresAppRestart) {
        var cliPath = CliResolver.ResolvePath(Environment.GetEnvironmentVariable, File.Exists, Program.Host.CliDirectory);
        var runner  = new ProcessRunner(_time);
        var probe   = TerminalPathProbe.Create(runner, Environment.GetEnvironmentVariable);
        var canonicalServer = ServerIdentity.Canonicalize(profile?.ServerUrl);
        // Shared with the probe above (not re-resolved) — the PATH overlay on `install`
        // must reflect the SAME probe outcome that the controller's preconditions/PathDegraded see.
        var cli     = new KcapCli(runner, cliPath, service.DaemonName, profile?.ProfileName ?? "default", probe.TerminalPathAsync,
            canonicalServer: canonicalServer);
        var surface = new LifecycleSurface(setLifecycleStatus, setLifecycleAttention, ConfirmLifecyclePromptAsync, withdrawLifecycleAttention);

        var lifecycle = new DaemonLifecycleController(
            service, cli, probe, surface, () => Task.FromResult(ValidProfileName(profile)), _time,
            canonicalServer, runMutation, autoActionsPermanentlyClosed, requiresAppRestart,
            windowsServiceDeclined: async () => (await _appState.LoadAsync().ConfigureAwait(false)).WindowsServiceDeclined,
            rememberWindowsServiceDeclined: () => _appState.UpdateAsync(s => s with { WindowsServiceDeclined = true }));

        // The shim links to the RESOLVED ABSOLUTE path only — CliResolver's bare "kcap" PATH
        // fallback means there is nothing to link, so the offer and the menu item both stay off
        // for the whole run.
        var shimTarget = ShimTargetFor(cliPath, Program.Host.CliOutlivesApp);
        // autoOfferSuppressed: Start() always runs — Offerable/manual install must keep working in Incomplete mode; only the once-ever auto-offer dialog is skipped.
        var shimOffer = new ShimOfferCoordinator(
            Task.WhenAll(lifecycle.PhaseClosed, _updateHandedOver.Task), probe, CliPathInstallers.Create(probe, _userHome), _appState, surface, shimTarget,
            _shutdown.Token, autoActionsPermanentlyClosed, _wizardPathLink);

        // The delegate below and the claims store must share one root: TryConsume takes the config
        // lock this delegate then reads under.
        var consentFlip = new ConsentFlipCoordinator(
            service, ops, new ConsentFlipClaims(_config),
            () => ResolveConsentFlipIdentity(_config, _localRoot), surface, _appState, _shutdown.Token);

        return (lifecycle, shimOffer, consentFlip, surface, probe, cli);
    }

    /// A link needs an absolute path that is still there once the app exits.
    internal static string? ShimTargetFor(string? cliPath, bool cliOutlivesApp) =>
        cliOutlivesApp && cliPath is not null && Path.IsPathRooted(cliPath) ? cliPath : null;

    internal static bool IsLocalServer(string? serverUrl, LocalRoot local) =>
        LocalEndpoints.Read(local).Classify(serverUrl) is LocalEndpointClass.Local;

    /// A local server takes no credential, so a sign-in dialog there could only fail; null hides
    /// every sign-in affordance and the Start banner speaks instead.
    internal static Action? SignInRequestFor(bool isLocalServer, Action openSignIn) =>
        isLocalServer ? null : openSignIn;

    // Pure read only — TryConsume already holds this same config lock when this delegate runs, so
    // unreadable config fails closed to an identity that matches nothing (the claim stays pending)
    // rather than throwing inside the two-lock section.
    // Deliberately literal ActiveProfile (no KCAP_PROFILE layering) — a divergence there is fail-safe
    // via the daemon's own identity-conditional ack.
    internal static (string Profile, string Server, string DaemonName) ResolveConsentFlipIdentity(ConfigRoot root, LocalRoot local) {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var config)) return ("", "", "");

        var profileName = config.ActiveProfile;
        var profile     = config.Profiles.GetValueOrDefault(profileName);
        var serverUrl   = LocalEndpoints.Read(local).Canonicalize(profile?.ServerUrl);
        var server      = ServerIdentity.Canonicalize(serverUrl) ?? serverUrl ?? "";
        var daemonName  = DaemonNameResolver.Resolve([], profile?.Daemon?.Name);
        return (profileName, server, daemonName);
    }

    /// <summary>
    /// A FRESH, env-aware identity for the wizard's own daemon-facing calls: the same
    /// <see cref="ProfileResolver"/> precedence <see cref="OnboardingGate.EvaluateAsync"/> uses, never
    /// the resolution the gate was evaluated on, and side-effect-free. Null — never
    /// an empty-string sentinel — when nothing resolves, which is what keeps its factories fail-closed.
    /// </summary>
    internal static (string Profile, string Server, string DaemonName)? ResolveWizardIdentity(ConfigRoot root, ProfileOverrides env, LocalRoot local) {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var config)) return null;

        var resolved = new ProfileResolver(
            config, cliServerUrl: null, env.Url, env.Profile,
            repoConfig: null, repoRemoteUrls: [], repoPath: null, LocalEndpoints.Read(local)).Resolve();

        if (resolved.ProfileName is not { Length: > 0 } profileName) return null;

        var canonicalServer = ServerIdentity.Canonicalize(resolved.ServerUrl);
        if (string.IsNullOrEmpty(canonicalServer)) return null;

        var daemonName = DaemonNameResolver.Resolve([], resolved.Profile?.Daemon?.Name);

        return (profileName, canonicalServer, daemonName);
    }

    /// The daemon name the resolved profile carries, or null when it never saved one.
    internal static string? ResolveConfiguredDaemonName(ConfigRoot root, ProfileOverrides env, LocalRoot local) {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var config)) return null;

        var resolved = new ProfileResolver(
            config, cliServerUrl: null, env.Url, env.Profile,
            repoConfig: null, repoRemoteUrls: [], repoPath: null, LocalEndpoints.Read(local)).Resolve();

        return resolved.Profile?.Daemon?.Name is { Length: > 0 } name ? name : null;
    }

    // A throwaway lane over a fresh resolution: the cached ServerLane is bound to the first profile it
    // saw, and the wizard has only just signed in. Null when the profile names no server.
    async Task<(ServiceProvider Lane, ProfileContext Profiles)?> FreshServerLaneAsync(CancellationToken ct) {
        var (_, profiles) = await ResolveAndEvaluateGateAsync(
            _config, _foreignHttp.GetRequiredService<TokenStore>(), _serverEnv, _localRoot, _time, ct).ConfigureAwait(false);
        if (profiles?.Resolution.ServerUrl is not { Length: > 0 } url) return null;

        var lane = new ServiceCollection()
            .AddSingleton(_config)
            .AddSingleton(profiles)
            .AddSingleton(new CapacitorServer(url, _config, profiles, _localRoot))
            .AddCapacitorHttp(_serverEnv, _machineEnv)
            .BuildValidated();

        return (lane, profiles);
    }

    async Task<IReadOnlyDictionary<string, DaemonNameHolding>> HeldDaemonNamesAsync(CancellationToken ct) {
        try {
            if (await FreshServerLaneAsync(ct).ConfigureAwait(false) is not ({ } lane, { } profiles))
                return FrozenDictionary<string, DaemonNameHolding>.Empty;
            await using var _ = lane;

            var (client, status, _, _) = await lane.GetRequiredService<ICapacitorHttpClient>().ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return FrozenDictionary<string, DaemonNameHolding>.Empty;

                return await DaemonNameHolder.HeldElsewhereAsync(client, profiles.Resolution.ServerUrl!, _config, ct).ConfigureAwait(false);
            }
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            Console.Error.WriteLine($"kcap: daemon name lookup failed — offering the configured name: {ex.Message}");
            return FrozenDictionary<string, DaemonNameHolding>.Empty;
        }
    }

    // Sent from the wizard so the dashboard it opens already knows this install. Bounded, so a
    // server that never answers leaves the Done page on a retry rather than a spinner.
    async Task<bool> ReportWizardCliSetupAsync(CancellationToken ct) {
        using var timeout = new CancellationTokenSource(WizardCliSetupReportTimeout, _time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try {
            if (await FreshServerLaneAsync(bounded.Token).ConfigureAwait(false) is not ({ } lane, { } profiles)) return false;
            await using var _ = lane;

            return await ServerCliSetupReport.Sender(lane.GetRequiredService<ICapacitorHttpClient>(), profiles, ReportedCliVersion)(bounded.Token)
                .ConfigureAwait(false);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            Console.Error.WriteLine($"kcap: cli-setup report from setup failed: {ex.Message}");
            return false;
        }
    }

    static readonly TimeSpan WizardCliSetupReportTimeout = TimeSpan.FromSeconds(30);

    // Null offers every answer: a 404 for the route is silent, and any other miss is logged.
    async Task<VisibilityOptionsResponse?> ReadWizardVisibilityOptionsAsync(CancellationToken ct) {
        using var timeout = new CancellationTokenSource(WizardVisibilityOptionsTimeout, _time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try {
            if (await FreshServerLaneAsync(bounded.Token).ConfigureAwait(false) is not ({ } lane, { } profiles)) {
                Console.Error.WriteLine("kcap: visibility options not read (no server resolved) — offering every answer");
                return null;
            }
            await using var _ = lane;

            var (client, status, _, _) = await lane.GetRequiredService<ICapacitorHttpClient>().ForWaitAsync(bounded.Token).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) {
                    Console.Error.WriteLine($"kcap: visibility options not read ({status}) — offering every answer");
                    return null;
                }

                var read = await ServerVisibilityOptions.ReadAsync(client, profiles.Resolution.ServerUrl!, bounded.Token).ConfigureAwait(false);
                if (read.Problem is { } problem) Console.Error.WriteLine($"kcap: visibility options read failed ({problem}) — offering every answer");

                return read.Offer;
            }
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            Console.Error.WriteLine($"kcap: visibility options read failed — offering every answer: {ex.Message}");
            return null;
        }
    }

    static readonly TimeSpan WizardVisibilityOptionsTimeout = TimeSpan.FromSeconds(10);

    static string? ReportedCliVersion => typeof(CapacitorVersion).Assembly.GetName().Version?.ToString();

    // Delegates to the ONE shared validator: must agree with OnboardingGate on what counts as a
    // valid server_url (e.g. both reject file://), or a gate-incomplete machine could still pass
    // this precondition into the normal daemon graph.
    internal static string? ValidProfileName(ResolvedProfile? profile) =>
        OnboardingGate.ValidServerUrl(profile?.ServerUrl)
            ? profile!.ProfileName
            : null;

    // The carve-out switch: Incomplete is the only gate outcome that closes auto-actions.
    internal static bool AutoActionsPermanentlyClosed(GateResult gate) => gate is GateResult.Incomplete;

    // A lane that outran the handoff cap still owns a live child, so the
    // graph comes up degraded whatever the gate said — a second automatic mutation would race it.
    internal static bool AutoActionsPermanentlyClosed(GateResult gate, bool laneQuiesced) =>
        AutoActionsPermanentlyClosed(gate) || !laneQuiesced;

    internal const string LaneStillBusyAttention =
        "A daemon operation is still finishing — automatic actions are disabled.";

    // The other half of the post-cap rule: closing auto-actions silently would leave a degraded app
    // that never says so. Exactly one line, and only when the cap actually fired.
    internal static void AnnounceUnquiescedLane(ILifecycleSurface surface, bool laneQuiesced) {
        if (!laneQuiesced) surface.Attention(LaneStillBusyAttention);
    }

    // A gate-evaluation exception must never brick startup — degrades to Incomplete (fail-safe: the app still launches, with auto-actions closed) instead of throwing.
    internal static async Task<(GateResult Gate, ProfileContext? Profiles)> EvaluateGateSafelyAsync(
            Func<CancellationToken, Task<(GateResult Result, ProfileContext Profiles)>> evaluate, CancellationToken ct) {
        try {
            return await evaluate(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // shutdown mid-evaluation — not a gate failure, let the caller's own catch handle it
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: onboarding gate evaluation failed unexpectedly — degrading to Incomplete: {ex.Message}");
            // Only a throw from the resolve itself reaches here — the gate keeps its resolution
            // across a failed evaluation — so there genuinely is nothing to hand back.
            return (new GateResult.Incomplete(GateReason.EvaluationFailed), null);
        }
    }

    static CliPin ResolvePinnedCli() =>
        ResolvePinnedCliCore(Environment.GetEnvironmentVariable("KCAP_APP_CLI_PATH"), Program.Host.CliDirectory, File.Exists, Path.GetFullPath);

    // Reads KCAP_APP_CLI_PATH directly: CliResolver.ResolvePath answers "kcap" with no override, which reads the same as a real one.
    internal static CliPin ResolvePinnedCliCore(string? overrideEnv, string cliDirectory, Func<string, bool> fileExists, Func<string, string> getFullPath) {
        if (!string.IsNullOrEmpty(overrideEnv))
            return fileExists(overrideEnv) ? getFullPath(overrideEnv) : new CliPin(null, OverrideMissing: true);
        var bundled = Path.Combine(cliDirectory, CliResolver.BundledCliFileName);
        return fileExists(bundled) ? bundled : null;
    }

    // Drains every non-success outcome: pre-presentation failures/cancellations requeue once, post-presentation ones still ack. Owns the per-run (never persisted) Takeover decline memory.
    internal static async Task ConsumeMutationOutcomesAsync(
            OutcomeChannel channel, ILifecycleSurface surface,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<CancellationToken, Task<string?>> terminalPathAsync, Func<string?> cliVersion, CancellationToken ct,
            Func<string, string?>? attentionCopy = null) {
        var declinedTakeoverPairs = new HashSet<(MutationRequest Request, string Token)>();
        try {
            await foreach (var lease in channel.ConsumeAsync(ct)) {
                var presented = false;
                try {
                    await PresentOutcomeAsync(
                            surface, lease.Envelope, runMutation, terminalPathAsync, cliVersion, ct, declinedTakeoverPairs,
                            () => presented = true, attentionCopy)
                        .ConfigureAwait(false);
                    lease.Ack(); // ran to completion — whether or not anything needed showing
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    if (presented) lease.Ack(); else lease.CancelLease(); // requeue-once only for a pre-presentation cancellation
                    throw; // let the outer catch end the whole loop, not just this envelope
                } catch (Exception ex) {
                    Console.Error.WriteLine($"kcap app failed to present a mutation outcome: {ex}");
                    if (presented) lease.Ack(); else lease.CancelLease(); // requeue-once only for a pre-presentation failure
                }
            }
        } catch (OperationCanceledException) {
            // shutdown — draining stops; anything still queued is simply not presented
        }
    }

    // Presents one classified outcome exactly once (success never reaches here). `markPresented` marks the UI boundary reached; `declinedTakeoverPairs` defaults fresh per caller.
    internal static async Task PresentOutcomeAsync(
            ILifecycleSurface surface, OutcomeEnvelope envelope,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<CancellationToken, Task<string?>> terminalPathAsync, Func<string?> cliVersion, CancellationToken ct,
            HashSet<(MutationRequest Request, string Token)>? declinedTakeoverPairs = null, Action? markPresented = null,
            Func<string, string?>? attentionCopy = null) {
        // The controller that started a reload awaits its outcome and renders it as state in the rail;
        // posting it here too would put a withdrawable condition on a lane nothing can withdraw from.
        if (envelope.Request.Verb == MutationVerb.Reload) {
            var (_, reloadToken) = ClassifyForPresentation(envelope.Outcome);
            Console.Error.WriteLine($"kcap: daemon reload outcome {envelope.Outcome.GetType().Name} ({reloadToken ?? "-"}) is presented by the lifecycle controller");
            markPresented?.Invoke();
            return;
        }

        if (envelope.Request.RetireServiceId is not null &&
            envelope.Outcome is not (MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout)) {
            surface.Attention(SettingsRenameMessage.For(envelope.Request, envelope.Outcome));
            markPresented?.Invoke();
            return;
        }
        if (envelope.Outcome is MutationOutcome.UnconfirmedNoAttach) {
            surface.Attention($"The daemon {VerbDisplay(envelope.Request.Verb)} is not yet confirmed — check status.");
            markPresented?.Invoke();
            return;
        }

        var (recoverySurface, token) = ClassifyForPresentation(envelope.Outcome);
        if (recoverySurface == RecoverySurface.None) return; // success cases only — never enqueued anyway

        // Refused/Failed always resolve a non-null token (Failed falls back to the exit-code
        // token) and AttentionSkew/AttentionRepair's own detail is never null either — every
        // branch below that reaches this point has a real token to name.
        var named = token!;
        switch (recoverySurface) {
            case RecoverySurface.Takeover: {
                var declined = declinedTakeoverPairs ?? [];
                var pairKey = (envelope.Request, named);
                if (declined.Contains(pairKey)) {
                    Console.Error.WriteLine($"kcap: daemon replacement still needed ({named}); already declined this run");
                    surface.Attention("Daemon service replacement is still needed. Restart kcap to review it again.");
                    markPresented?.Invoke();
                    break;
                }

                var pathDegraded = await terminalPathAsync(ct).ConfigureAwait(false) is null;
                var prompt = new LifecyclePrompt(
                    LifecyclePrompt.KindTakeover, null, cliVersion(), pathDegraded, DaemonLifecycleController.TakeoverDisclosure);
                var accepted = await surface.TryConfirmAsync(prompt, ct).ConfigureAwait(false);
                if (accepted is null) { ct.ThrowIfCancellationRequested(); throw new OperationCanceledException(ct); } // cancelled before the dialog ever ran — never presented
                markPresented?.Invoke(); // the dialog was shown and answered — everything after this is post-presentation
                if (accepted.Value) {
                    // No app-side evidence revalidation on accept: a stale Accept only fails coded (28/29, under the CLI's transaction lock) and re-arrives as a fresh outcome.
                    _ = await runMutation(envelope.Request with { Verb = MutationVerb.Replace }, ct).ConfigureAwait(false);
                } else {
                    declined.Add(pairKey);
                    Console.Error.WriteLine($"kcap: daemon replacement declined ({named})");
                    surface.Status("Daemon service replacement was skipped. Restart kcap to review it again.");
                }
                break;
            }
            case RecoverySurface.Reinstall:
                // Token stays in the log for support; the banner names the action, not the wire code.
                Console.Error.WriteLine($"kcap: daemon package inconsistent ({named}) — reinstall needed");
                surface.Attention("This kcap install looks broken. Reinstall kcap, then try again.");
                markPresented?.Invoke();
                break;
            case RecoverySurface.Attention:
            case RecoverySurface.Storage: {
                if ((attentionCopy ?? AttentionCopyFor)(named) is { } copy) {
                    surface.Attention(copy);
                } else {
                    // Opaque tokens are for the log — a bare "needs attention (token)" banner helps nobody.
                    Console.Error.WriteLine($"kcap: daemon mutation needs attention ({named}) — not shown in the UI");
                }
                markPresented?.Invoke();
                break;
            }
        }
    }

    // A small display map instead of MutationVerb.ToString() for user-facing copy.
    static string VerbDisplay(MutationVerb verb) => verb switch {
        MutationVerb.Install       => "install",
        MutationVerb.Replace       => "replace",
        MutationVerb.StartVerified => "verified start",
        MutationVerb.DetachedStart => "daemon start",
        MutationVerb.Reload        => "reload",
        MutationVerb.InstallWindowsService => "Windows service move",
        _                          => verb.ToString(),
    };

    /// User-facing line for Attention/Storage outcomes. Null means log-only — never surface a
    /// machine token the operator cannot act on.
    internal static string? AttentionCopyFor(string token) => token switch {
        "daemon_renamed_restart_app" => "The daemon was renamed. Restart this app before managing it.",
        "cli_not_found"            => "kcap CLI not found. Can't manage the daemon from this app.",
        "cli_override_missing"     => "KCAP_APP_CLI_PATH names a file that does not exist. Fix or unset it, then restart this app.",
        // App↔CLI floor — never "for the daemon"; this gate runs before any daemon contact.
        "cli_below_floor"          => "This kcap is too old for this app. Update kcap, then press Start daemon.",
        "cli_version_unknown"      => "kcap did not answer a version check. Run `kcap --version` in a terminal, then try again.",
        "no_server_configured"     => "No server is configured. Sign in or run setup first.",
        "consent_seed_unwritable"  => "Couldn't write consent data. Check permissions on the kcap config directory.",
        "internal_error"           => "Couldn't finish daemon setup. Press Start daemon to try again.",

        "verify_viability" or "verify_hello_validation" or "verify_start_gate" or "verify_start_gate_drift"
            or "daemon_start_gate"
            => "The daemon didn't come up cleanly. Press Start daemon to try again.",

        "verify_readiness_timeout" or "verify_contended" or "verify_rollback_budget"
            or "verify_restore_verification" or "verify_stop_unconfirmed" or "verify_bootout_unknown"
            => "Daemon setup didn't finish in time. Press Start daemon to try again.",

        "stale_txn_marker"
            => "A previous daemon setup didn't finish. Press Start daemon to try again.",

        "running_without_daemon_pid"
            => "The daemon service is loaded but no process is attached. Press Start daemon to repair.",

        "daemon_running_outside_service"
            => "A daemon is running outside the service. Press Start daemon to repair.",

        "ownership_mismatch" or "ownership_unknown" or "instance_pid_mismatch"
            or "instance_changed_during_classification" or "unreachable_with_recorded_owner"
            => "Another daemon may already be running for this name. Stop it, then press Start daemon.",

        "server_or_name_mismatch" or "identity_inconsistent"
            => "This daemon doesn't match the configured server or name. Check setup, then press Start daemon.",

        _ => null,
    };

    /// The launcher sentences name that window's Start daemon button. The wizard's retry control
    /// is whichever action the daemon step is showing, so those sentences keep the fact and drop the button.
    internal static string? WizardAttentionCopyFor(string token) =>
        AttentionCopyFor(token)?
            .Replace("Press Start daemon to try again.", "Try again from this step.", StringComparison.Ordinal)
            .Replace("Press Start daemon to repair.", "Try again from this step.", StringComparison.Ordinal)
            .Replace("then press Start daemon.", "then try again from this step.", StringComparison.Ordinal);

    // Only these AttentionSkew tokens route to Takeover; every other AttentionSkew/AttentionRepair stays Attention.
    static readonly HashSet<string> TakeoverRoutedSkewTokens = ["missing_capability_consent_3", "daemon_below_floor", "pre_slice_evidence"];

    internal static (RecoverySurface Surface, string? Token) ClassifyForPresentation(MutationOutcome outcome) => outcome switch {
        MutationOutcome.Refused(var reason, var surface)              => (surface, reason),
        MutationOutcome.Failed(var exitCode, var reason, var surface) => (surface, reason ?? VerifyExitCodes.Token(exitCode)),
        MutationOutcome.AttentionSkew(var detail) =>
            (TakeoverRoutedSkewTokens.Contains(detail) ? RecoverySurface.Takeover : RecoverySurface.Attention, detail),
        MutationOutcome.AttentionRepair(var detail) => (RecoverySurface.Attention, detail),
        _ => (RecoverySurface.None, null),
    };

    Task<bool> ConfirmLifecyclePromptAsync(LifecyclePrompt prompt, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => {
            // A finished update's window would otherwise sit under the prompt as a second dialog.
            _closeUpdateFinishWindow?.Invoke();
            return ShowLifecyclePromptDialogAsync(DialogOwner(), prompt, ct);
        });

    // Once only: a later rebuild (after a real close) must not reach for a window that is gone.
    (MainWindow Window, AppState State)? TakeAdoptableWindow() {
        var window = _adoptableWindow;
        _adoptableWindow = null;
        return window is { Window.IsVisible: true } ? window : null;
    }

    // The wizard owns dialogs while wizard-first mode is up — no main window exists yet.
    Window? DialogOwner() =>
        _wizardWindow is { IsVisible: true } wizard ? wizard
        : _coordinator?.Window is { IsVisible: true } main ? main
        : null;

    internal static Task<bool> ShowLifecyclePromptDialogAsync(Window? owner, LifecyclePrompt prompt, CancellationToken ct) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new LifecyclePromptWindow { DataContext = new LifecyclePromptViewModel(prompt, tcs) };
        // Closing via the titlebar/Esc without Accept/Decline also resolves false —
        // BuildConfirmForceStopWindow's same rule below. TrySetResult is idempotent, so this is a
        // no-op once AcceptCommand/DeclineCommand already resolved it.
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        WireDialogCancellation(dialog, tcs, ct);

        if (owner is not null) {
            dialog.Show(owner);
        } else {
            dialog.Show();
            dialog.Activate();
        }

        return tcs.Task;
    }

    // ConfirmAndReplaceAsync holds the operation gate across the whole ConfirmAsync await — a
    // dialog left open through a lifetime-cancel (app shutdown or DisposeAsync) must not leave the
    // gate (and therefore QuiescedAsync) blocked on a human who may never come back. Cancellation
    // can arrive on any thread, so the close is posted rather than called inline; the registration
    // is disposed once the dialog resolves on its own so it doesn't outlive the window. Extracted
    // (internal, static) so a test can drive it directly against a real headless window.
    internal static void WireDialogCancellation(Window dialog, TaskCompletionSource<bool> tcs, CancellationToken ct) {
        var registration = ct.Register(() =>
            Dispatcher.UIThread.Post(() => { if (!tcs.Task.IsCompleted) dialog.Close(); }));
        tcs.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static string ActivityStatKey(DaemonStore store, string daemonName) {
        var path = store.ConsentLogPath(daemonName);
        return ActivityStatKey(path + ".1", path);
    }

    // Combines both log files' (LastWriteTimeUtc, Length) into one comparison key for
    // ActivityViewModel's stat poll. Each file gets its OWN try/catch: `.1` is absent
    // on every fresh install until the first 1MB rotation, and a single shared catch around both
    // files would collapse the WHOLE joined key to the "absent" constant whenever `.1` throws —
    // appends to the live file would then never change the key, and the Activity tab would go
    // stale until the tab is reselected. FileInfo.Length throws FileNotFoundException on a
    // missing file (unlike File.GetLastWriteTimeUtc, which returns a sentinel instead) — that
    // throw is what carries a clean per-file absence into that file's own "absent" branch. Takes
    // both paths directly (rather than a daemon name) so a test can point it at a temp directory
    // without redirecting any real daemon-dir resolution.
    internal static string ActivityStatKey(string p1Path, string livePath) => $"{StatOf(p1Path)}|{StatOf(livePath)}";

    static string StatOf(string path) {
        try {
            return $"{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}";
        } catch {
            return "absent";
        }
    }

    // Composed here (not inside AgentActionService): the service only awaits the
    // seam; every UI concern — the dialog itself, choosing an owner, marshaling onto the UI
    // thread — lives at this composition root, same as ShellUrlOpener/LocalControlOps above.
    Task<bool> ConfirmForceStopAsync(string label) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowConfirmForceStopDialogAsync(label));

    // Runs ON the UI thread (guaranteed by the InvokeAsync call above — never call this directly
    // from a background thread). Owner = the main window only while it's actually VISIBLE
    // (IsVisible, which Window.Show()/Hide() toggle) — a hide-to-tray
    // stop must still surface the prompt, so it shows standalone and pulls itself forward instead
    // of silently attaching to a window nobody can see.
    Task<bool> ShowConfirmForceStopDialogAsync(string label) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = BuildConfirmForceStopWindow(label, tcs);

        if (DialogOwner() is { } owner) {
            dialog.Show(owner);
        } else {
            dialog.Show();
            dialog.Activate();
        }

        return tcs.Task;
    }

    // "Stop anyway" is destructive, so Enter does not press it; Esc cancels. Closing via the
    // titlebar without clicking either button also resolves false — TrySetResult is idempotent,
    // so whichever path runs first wins and the other is a no-op.
    internal static Window BuildConfirmForceStopWindow(string label, TaskCompletionSource<bool> tcs) {
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Classes = { "kcapChip" } };
        var stopButton = new Button { Content = "Stop anyway", Classes = { "kcapDangerFill" } };

        var window = new Window {
            Title = "Stop review participant?",
            Icon = ProductIcon.WindowIcon,
            Background = KcapBrush("KcapCanvasBrush"),
            Foreground = KcapBrush("KcapTextBrush"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel {
                Margin = new Thickness(20),
                Spacing = 16,
                Children = {
                    new TextBlock {
                        Text = $"{label} is a review participant. Stopping it will strand its flow. Stop anyway?",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, stopButton },
                    },
                },
            },
        };

        stopButton.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        cancelButton.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(false);

        return window;
    }

    // Async-safe shutdown: ShutdownRequested fires on the UI thread and can be cancelled, so the
    // FIRST pass defers it (e.Cancel = true), cancels the shutdown token (abandoning any
    // in-flight StartDaemonAsync WAIT — never the spawned daemon), and disposes the service in
    // the background (no live socket read/child-process wait may survive app exit).
    // Once that completes, TryShutdown() re-raises this same event; the SECOND pass is let
    // through. This never blocks the UI thread on the async disposal.
    void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) {
        // Nothing to tear down in this mode; a quit mid-run would leave kcap half removed.
        if (_uninstallRun is { } uninstall) {
            e.Cancel = uninstall.IsBusy;
            return;
        }
        // Navigation's twin of BeginShutdownPass rule 1, and for the same reason: applied on EVERY
        // pass, before the confirmed-pass early return, so a window the coordinator builds BETWEEN
        // the passes can neither open a workspace nor keep one attached.
        LatchNavigation();
        if (!BeginShutdownPass(_coordinator, _shutdownConfirmed)) return;

        e.Cancel = true;
        _shutdown.Cancel();
        _ = StartShutdownAsync();
    }

    // The flag is raised BEFORE the disposal runs, and that ordering is what lets the pass close a
    // dialog that cancels its own close while busy (settings, feedback). A second call — a rapid
    // double Cmd+Q — finds disposal already in flight and does nothing.
    internal Task StartShutdownAsync() {
        if (_shutdownStarted) return Task.CompletedTask;
        _shutdownStarted = true;
        return DisposeAndShutdownAsync();
    }

    // Split out of OnShutdownRequested so a test can drive BOTH passes (the event itself needs a
    // live App and a real lifetime, over a composition that needs a real daemon). Two rules, in
    // this order:
    //
    // 1. QuitInProgress is flagged on EVERY pass — including the confirmed one, which is why this
    //    runs before the guard below. A coordinator that only comes into existence BETWEEN the
    //    passes (quit or an OS logout arriving while startup is still resolving — or still showing
    //    the wizard — with StartAsync's continuation then building the window during the deferred
    //    disposal's await)
    //    would otherwise still have hide-on-close armed when the second pass closes the windows:
    //    the window cancels its own close, DoShutdown aborts with windows still open, and every
    //    later quit early-returns on _shutdownConfirmed — an app that can only be force-quit.
    //    Setting it again on a pass that already set it is a no-op.
    // 2. The confirmed (second) pass is let through untouched — no e.Cancel — which is what the
    //    caller's early return preserves.
    internal static bool BeginShutdownPass(MainWindowCoordinator? coordinator, bool shutdownConfirmed) {
        if (coordinator is not null) coordinator.QuitInProgress = true;
        return !shutdownConfirmed;
    }

    // Synchronous, on the ShutdownRequested (UI) thread: the live workspace is unhooked and its
    // teardown REGISTERED here, so the drain below can only ever seal a set that already contains
    // it. The gate is latched even with no window ever built — a window built later still sees it.
    void LatchNavigation() {
        _notificationAccessPrompt?.Dispose();
        _desktopNotifications?.Dispose();
        (_coordinator?.Window?.DataContext as MainWindowViewModel)?.LatchShutdown();
        _navigation.Latch();
    }

    internal Task DisposeAndShutdownAsync() => DisposeAppAsync(shutdown: true);

    async Task DisposeAppAsync(bool shutdown) {
        // FIRST, before quiesce (which can wait a full minute) and before any disposal: a live
        // workspace holds a terminal attach on the daemon socket, and the clamp it implies must be
        // released for every other viewer as early as possible. Bounded at 5s and never throws.
        //
        // Keep the UI context for the dialog closes below. Later quiesce awaits may resume on a
        // worker thread; the final UI disposal explicitly returns to the dispatcher.
        await _workspaceTeardown.DrainAsync();

        // Still on the UI thread (required by Close), and before the quiesce below: the dialog's
        // own close path cancels a live re-auth attempt — or lets a commit already past the
        // boundary finish — and the await stops the process exiting under it. Closed assigns
        // _reauthSettle synchronously, so reading it after Close observes this close's task.
        if (_signInWindow is { } reauthDialog) reauthDialog.Close();
        if (_settingsWindow is { } settingsDialog) settingsDialog.Close();
        _updateCheckSubscription?.Dispose();
        _updateCheckWindow?.Close();
        _launchApprovals?.Dispose();
        _menuBar?.SetLaunchApprovalsAction(null);
        if (_feedbackWindow is { } feedbackDialog) feedbackDialog.Close();
        if (_reauthSettle is { } reauthSettle) await reauthSettle.ConfigureAwait(false);

        // An in-flight sign-in always settles, mutations get a bounded chance
        // to — both while the UI is still up, before teardown.
        if (_wizardAuth is not null || _historyImport is not null || _localInstall is not null || _lifecycle is not null
         || _lane is not null || _localUpgrade is not null || _localSearch is not null)
            await QuiesceAppAsync(_wizardAuth, _historyImport, _localInstall, _lifecycle, _lane, QuiesceShutdownCap, _time, _localUpgrade,
                    _localSearch)
                .ConfigureAwait(false);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            // Quiesce may resume on a worker thread. UI disposal and the lifetime's final pass
            // both belong on the dispatcher; uninstall keeps that same window and process alive.
            await Dispatcher.UIThread.InvokeAsync(async () => {
                IDisposable?[] ui = [
                    _tray, _trayVm, _promptCoordinator, _consent, _desktopNotifications, _notificationSink,
                    _notificationSessions, _notificationSettings, _permissionFeed, _attention, _commands, _permissions, _accounts, _sessionAccess,
                    _pullRequestTones, _launchApprovals, _webWorkspace, _activity, _home, _localServer, _localStack, _rail, _pause, _staleDaemonNudge, _restartPending, _expectedRestart,
                ];
                if (shutdown) {
                    await DisposeUiThenConfirmShutdownAsync(ui,
                        DisposeLifecycleAndServiceAsync, () => _shutdownConfirmed = true, desktop, _exitCode,
                        applyOnExit: () => {
                            LifecycleLog.Write("app_exiting", ("exit_code", _exitCode.ToString(CultureInfo.InvariantCulture)));
                            if (!_setupRestarting && !_uninstalling) _updates?.ApplyPendingOnExit();
                        });
                } else {
                    await DisposeUiForUninstallAsync(ui, DisposeLifecycleAndServiceAsync);
                }
            });
        } else {
            await DisposeLifecycleAndServiceAsync();
            if (shutdown) _shutdownConfirmed = true;
        }
    }

    // Runs after the UI disposables (DisposeUiThenConfirmShutdownAsync), so _home is already gone
    // when its server clients are torn down here. _lifecycle then goes first (guarded, so a throw
    // never skips _service's disposal); the lane goes LAST — its substrate must outlive any caller
    // still awaiting RunAsync.
    async ValueTask DisposeLifecycleAndServiceAsync() {
        // Budgeted inside and never throws: what the app queued is shipped before the process goes.
        await _telemetry.FlushAndClose().ConfigureAwait(false);
        await DisposeServerClientsAsync().ConfigureAwait(false);
        if (_lifecycle is not null) {
            try {
                await _lifecycle.DisposeAsync().ConfigureAwait(false);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon lifecycle controller during shutdown: {ex}");
                if (_uninstallRun is not null) throw;
            }
        }
        if (_service is not null) await _service.DisposeAsync().ConfigureAwait(false);
        if (_lane is not null) {
            try {
                await _lane.DisposeAsync().ConfigureAwait(false);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon mutation lane during shutdown: {ex}");
                if (_uninstallRun is not null) throw;
            }
        }
    }

    // Reached from both teardown paths; the holder memoizes the cleanup, so a second call awaits
    // the first rather than disposing anything again.
    async ValueTask DisposeServerClientsAsync() {
        _directory?.Dispose();
        _sessions?.Dispose();
        _remoteAgents?.Dispose();
        _modelCatalog?.Dispose();
        if (_gh is { } gh) { _gh = null; await gh.DisposeAsync().ConfigureAwait(false); }
        if (_serverClients is null) return;
        await _serverClients.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Quiesces shutdown in two phases: sign-in, import and the local install, upgrade and search change finish
    /// uncapped so an in-progress commit, switch or rollback isn't torn down, then lifecycle/lane quiesce
    /// under the cap.</summary>
    internal static async Task QuiesceAppAsync(
            WizardAuthService? auth, HistoryStepViewModel? import, LocalInstallController? install,
            DaemonLifecycleController? lifecycle, DaemonMutationLane? lane, TimeSpan cap, TimeProvider time,
            LocalUpgradeController? upgrade = null, LocalSearchController? search = null) {
        var authTerminal = CancelAndAwaitAuthAsync(auth);
        if (import is not null) await import.CancelActiveRunAsync().ConfigureAwait(false);
        if (install is not null) await install.SettleAsync().ConfigureAwait(false);
        if (upgrade is not null) await upgrade.SettleAsync().ConfigureAwait(false);
        if (search is not null) await search.SettleAsync().ConfigureAwait(false);
        await authTerminal.ConfigureAwait(false);
        await AwaitQuiescedAsync(() => QuiesceLifecycleAndLaneAsync(lifecycle, lane), cap, time).ConfigureAwait(false);
    }

    // Composes the controller's QuiescedAsync with the lane's (covers main-window Start/Retry too); CancellationToken.None — the bound is AwaitQuiescedAsync's own race against Task.Delay(cap).
    internal static async Task QuiesceLifecycleAndLaneAsync(DaemonLifecycleController? lifecycle, DaemonMutationLane? lane) {
        var waits = new List<Task>(2);
        if (lifecycle is not null) waits.Add(lifecycle.QuiescedAsync());
        if (lane is not null) waits.Add(lane.QuiescedAsync(CancellationToken.None));
        if (waits.Count > 0) await Task.WhenAll(waits).ConfigureAwait(false);
    }

    // The shutdown cap: QuiescedAsync itself is unbounded (it just waits for the gate), so this is what
    // keeps a stuck internal mutation from hanging shutdown forever — DisposeAsync's own eventual
    // lifetime-cancel is still the backstop if the cap is reached. Static + delegate-shaped so a
    // test can drive it without a live controller. Returns which arm won: false = the cap fired
    // with work still live, which the wizard handoff turns into a degraded (auto-actions closed)
    // graph. A dead heat reads as false — never claim quiescence that wasn't observed.
    internal static async Task<bool> AwaitQuiescedAsync(
            Func<Task> quiescedAsync, TimeSpan cap, TimeProvider time) {
        var quiesced = quiescedAsync();

        return await Task.WhenAny(quiesced, Task.Delay(cap, time)).ConfigureAwait(false) == quiesced;
    }

    // Split out of DisposeAndShutdownAsync so a test can pin the ordering with a recording list.
    // The UI-thread-owned disposables go first, synchronously on the UI thread this runs on (the
    // ShutdownRequested thread), so the menu-bar icon is gone before TryShutdown; the deferred
    // pass follows.
    internal static Task DisposeUiThenConfirmShutdownAsync(
            IReadOnlyList<IDisposable?> uiDisposables, Func<ValueTask>? disposeAsync, Action markConfirmed,
            IClassicDesktopStyleApplicationLifetime desktop, int exitCode, Action? applyOnExit = null) {
        DisposeAll(uiDisposables, "shutdown");
        return DisposeAndConfirmShutdownAsync(disposeAsync, markConfirmed, desktop, exitCode, applyOnExit);
    }

    // In-place uninstall cannot rely on process exit to stop a service whose disposal failed.
    // Drain the rest, then refuse destructive cleanup when any UI service failed to close.
    internal static async Task DisposeUiForUninstallAsync(IReadOnlyList<IDisposable?> uiDisposables, Func<ValueTask> disposeAsync) {
        var uiDisposed = DisposeAll(uiDisposables, "uninstall");
        await disposeAsync();
        if (!uiDisposed) throw new InvalidOperationException("Some app services could not close. Close kcap and try uninstall again.");
    }

    // Per-entry guard for the same reason DisposeAndConfirmShutdownAsync wraps its disposeAsync: a
    // throw here must never skip the remaining disposables, markConfirmed or TryShutdown —
    // _shutdownConfirmed would stay false while _shutdownStarted stayed true, cancelling every
    // later quit forever. Null entries are the "that step never ran" case.
    static bool DisposeAll(IReadOnlyList<IDisposable?> disposables, string phase) {
        var succeeded = true;
        foreach (var disposable in disposables) {
            try {
                disposable?.Dispose();
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose a UI service during {phase}: {ex}");
                succeeded = false;
            }
        }
        return succeeded;
    }

    // Split out of DisposeAndShutdownAsync so a test can drive the full deferred-shutdown pass —
    // dispose, THEN mark confirmed, THEN shut down carrying an exit code — against a fake
    // IClassicDesktopStyleApplicationLifetime, without needing a live App instance.
    // `disposeAsync` is a delegate (not the concrete DaemonClientService) so a test can inject a
    // throwing disposal without depending on how DaemonClientService itself might fail.
    // TryShutdown carries `exitCode`: its default of 0 would let Cmd+Q while the startup-error
    // window shows overwrite the failure exit code with success. `markConfirmed` MUST run before
    // `TryShutdown`, because TryShutdown can re-raise ShutdownRequested synchronously and
    // OnShutdownRequested's early-return guard (`if (_shutdownConfirmed) return;`) depends on
    // that happening first.
    internal static async Task DisposeAndConfirmShutdownAsync(
            Func<ValueTask>? disposeAsync, Action markConfirmed, IClassicDesktopStyleApplicationLifetime desktop,
            int exitCode, Action? applyOnExit = null) {
        // A throwing disposeAsync must never skip markConfirmed/TryShutdown — otherwise
        // _shutdownConfirmed is never set while _shutdownStarted stays true, and every later
        // quit is cancelled forever.
        try {
            if (disposeAsync is not null) await disposeAsync();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during shutdown: {ex}");
        } finally {
            markConfirmed();
            // Last, after every disposal: the updater waits at most 60 s for this process to exit,
            // and the quiesce above can use all of that on its own.
            try {
                applyOnExit?.Invoke();
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to hand the pending update to the updater: {ex}");
            }
            desktop.TryShutdown(exitCode);
        }
    }
}
