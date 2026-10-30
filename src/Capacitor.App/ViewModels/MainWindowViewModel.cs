using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Projects IDaemonClientService.Status/Snapshots into display text and drives Start/Reconnect.
/// Display projections are activation-scoped (WhenActivated). StartDaemonCommand/RetryCommand and
/// their canExecute pipelines are built in the constructor so they exist pre-activation.
/// The service outlives this VM and owns its subjects.
public sealed class MainWindowViewModel : ReactiveObject, IActivatableViewModel {
    const string IncompatibleReason = "daemon_incompatible";
    const string UnreachableReason  = "daemon_unreachable";

    /// Shown the moment Start daemon is pressed, before the lifecycle/CLI work returns, so a
    /// click is never silent even when the start action itself has nothing further to say.
    internal const string StartingMessage = "Starting the daemon…";

    /// Shown the moment Reconnect is pressed. Cleared on Connected; replaced if attach stays unreachable.
    internal const string ReconnectingMessage = "Reconnecting…";

    internal const string ReconnectFailedMessage =
        "Couldn't reconnect. If the daemon isn't running, press Start daemon.";

    internal static bool IsInFlightReconnectMessage(string? message) =>
        message == ReconnectingMessage
        || message == DaemonLifecycleController.AlreadyRunningReconnectStatus;

    static readonly IBrush ConnectedBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(StatusColors.Connected));
    static readonly IBrush InProgressBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(StatusColors.InProgress));
    static readonly IBrush PendingBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(StatusColors.Pending));
    static readonly IBrush DisruptedBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(StatusColors.Disrupted));
    static readonly IBrush UnavailableBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(StatusColors.Unavailable));

    static IBrush Paint(string hex) => hex switch {
        StatusColors.Connected => ConnectedBrush,
        StatusColors.InProgress => InProgressBrush,
        StatusColors.Pending => PendingBrush,
        StatusColors.Disrupted => DisruptedBrush,
        _ => UnavailableBrush,
    };

    readonly IDaemonClientService _service;

    public ViewModelActivator Activator { get; } = new();

    ObservableAsPropertyHelper<string>? _daemonName;
    public string DaemonName => _daemonName?.Value ?? "";

    // Compact rail label: the daemon semver alone — build metadata stays off the line.
    ObservableAsPropertyHelper<string>? _versionDisplay;
    public string VersionDisplay => _versionDisplay?.Value ?? "";

    ObservableAsPropertyHelper<string>? _serverUrl;
    public string ServerUrl => _serverUrl?.Value ?? "";

    // Single-word presentation of the OVERALL connection situation (local attach State first,
    // falling back to the daemon's own upstream Connection only once State is Connected — see
    // ConnectionDisplayFor). Capitalized, in-progress words get a trailing ellipsis ("Connecting…").
    ObservableAsPropertyHelper<string>? _connectionDisplay;
    public string ConnectionDisplay => _connectionDisplay?.Value ?? "";

    // The color ConnectionDisplay is painted in — the word itself carries the status, so this
    // and the text come from parallel switches over one bucketing.
    ObservableAsPropertyHelper<IBrush>? _statusBrush;
    public IBrush StatusBrush => _statusBrush?.Value ?? Paint(StatusColors.Unavailable);

    ObservableAsPropertyHelper<bool>? _showsUpdateContext;
    public bool ShowsUpdateContext => _showsUpdateContext?.Value ?? false;
    public string UpdateContextName => LocalStack is not null ? "kcap local" : TenantName.Length > 0 ? TenantName : "kcap";

    // "n of m agents" only while Connected — active_agents is a display count, never capacity.
    // Empty otherwise, which is what hides it: the service still retains the last snapshot
    // across disconnects, so a count would go on reading as live.
    ObservableAsPropertyHelper<string>? _agentCountText;
    public string AgentCountText => _agentCountText?.Value ?? "";

    // False until a snapshot has a daemon name or a displayable version. The help chip
    // then shares the status line; a second row with nothing in it would leave the chip
    // sitting under that line.
    ObservableAsPropertyHelper<bool>? _showsDaemonLine;
    public bool ShowsDaemonLine => _showsDaemonLine?.Value ?? false;

    internal const string RestartPendingMessage = "The daemon is waiting for active turns. It restarts when the configured wait ends. Long turns may be interrupted. Eligible agents resume if auto-resume is on. Other sessions may need attention.";

    // The daemon has a restart-after-update queued (DaemonRestartPendingWatcher) and we are
    // attached to it: a passive marker only, the restart is the daemon's own.
    ObservableAsPropertyHelper<bool>? _restartPending;
    public bool RestartPending => _restartPending?.Value ?? false;

    ObservableAsPropertyHelper<string?>? _restartPendingText;
    public string? RestartPendingText => _restartPendingText?.Value;

    internal const string BackgroundPriorityMessage = "Daemon runs at background priority — agent terminals lag under load.";

    ObservableAsPropertyHelper<bool>? _backgroundPriority;
    /// The attached daemon runs in launchd's background band. Connected only: it describes the daemon on screen.
    public bool BackgroundPriority => _backgroundPriority?.Value ?? false;

    ObservableAsPropertyHelper<string?>? _backgroundPriorityText;
    public string? BackgroundPriorityText => _backgroundPriorityText?.Value;

    ObservableAsPropertyHelper<string?>? _reloadFailureText;
    /// The standing reload outcome's line. Not gated on the attach: a reload that left the daemon
    /// unreachable is exactly the outcome this line exists to show.
    public string? ReloadFailureText => _reloadFailureText?.Value;

    ObservableAsPropertyHelper<bool>? _isReloading;
    public bool IsReloading => _isReloading?.Value ?? false;

    ObservableAsPropertyHelper<bool>? _showsReloadBlock;
    public bool ShowsReloadBlock => _showsReloadBlock?.Value ?? false;

    public ReactiveCommand<Unit, Unit> ReloadDaemonCommand { get; }

    ObservableAsPropertyHelper<AttachState>? _state;
    public AttachState State => _state?.Value ?? AttachState.Connecting;
    ObservableAsPropertyHelper<bool>? _loadingSessions;
    public bool LoadingSessions => _loadingSessions?.Value ?? true;
    ObservableAsPropertyHelper<bool>? _showsNoSessions;
    public bool ShowsNoSessions => _showsNoSessions?.Value ?? false;

    // Display text for why we're not connected: friendly copy only — never a raw wire token
    // like daemon_unreachable. Null outside Unreachable.
    ObservableAsPropertyHelper<string?>? _reason;
    public string? Reason => _reason?.Value;

    // The server lane's silent-deafness diagnostic — informational only, never blocking; null
    // while the lane is healthy or absent.
    ObservableAsPropertyHelper<string?>? _serverLaneTip;
    public string? ServerLaneTip => _serverLaneTip?.Value;

    ObservableAsPropertyHelper<bool>? _connectionHasDetail;
    public bool ConnectionHasDetail => _connectionHasDetail?.Value ?? false;

    public const string ConnectionStatusTip = "Connection to the daemon and server";
    public const string DaemonNameTip = "Name of the daemon on this machine";

    /// Caption under the URL in the org label's hover — the org is what you point at, the
    /// server it talks to is what the hover tells you.
    public const string ServerUrlTip = "kcap server for this organization";
    public const string VersionIdentityTip = "Version of the kcap daemon on this machine";
    public const string AgentCountTip = "Agents running on this daemon";

    readonly BehaviorSubject<string?> _startMessageChanges = new(null);

    /// Constructed once at the composition root; the prompt window's onConcluded callback nudges
    /// the same instance.
    public ActivityViewModel Activity { get; }
    public WebWorkspaceViewModel? Web { get; init; }
    public ReactiveCommand<Unit, Unit> OpenLaunchApprovalsCommand { get; }

    /// The launcher pane's view model, built at the composition root over the SAME
    /// IDaemonClientService instance this window uses, never a second daemon connection. Null
    /// only for a caller that doesn't supply one; LauncherPaneView tolerates a null DataContext.
    public HomeViewModel? Home { get; }

    readonly NavigationGate _navigation;
    bool _shutdownLatched;
    readonly Action<Func<Task>> _trackTeardown;
    readonly Func<string, WorkspaceViewModel>? _workspaceFactory;
    readonly Func<string, AgentOrigin?> _originOf;
    readonly Func<string, RemoteSessionViewModel?>? _remoteFactory;
    readonly Func<string, RemoteSessionViewModel?>? _serverFactory;
    readonly IAgentDirectory? _directory;
    readonly IServerSessionsService? _sessions;
    /// The session of the last pin search asked for; it may still be reading while nothing is held.
    string? _searchPin;
    PinPresentation _searchPinPresentation = new(null, null, null);
    readonly IDisposable _onAppServer;
    /// A session id names a session only within one server: a hand-off to the server's row is
    /// sound only while the local daemon reports the app's server.
    bool _localOnAppServer = true;
    readonly SerialDisposable _rebind = new();

    ISessionWorkspace? _currentWorkspace;
    /// null = the launcher pane shows; non-null = that session's workspace,
    /// local or remote. Exactly one at a time, and this VM owns it: every swap starts the outgoing
    /// one's tracked teardown.
    public ISessionWorkspace? CurrentWorkspace {
        get => _currentWorkspace;
        private set => this.RaiseAndSetIfChanged(ref _currentWorkspace, value);
    }

    /// The Sessions rail (repo → worktree → session over daemon.Agents) — null for a caller
    /// without one, same nullable-seam shape as Home/workspaceFactory above.
    public SessionRailViewModel? Rail { get; }

    /// The active profile's name — the tenant slug (profiles are named after it at sign-in).
    /// "" when absent or when the name is the literal built-in "default" (hiding that segment
    /// so the rail footer does not read as a second "Default" next to connection state).
    public string TenantName { get; }

    /// The local stack's rail row beside the daemon's; null unless the server is this machine's local installation.
    public LocalStackViewModel? LocalStack { get; init; }

    /// The launch auto-open's staleness token — see NavigationGate. Read from the SHARED gate, not
    /// a per-window counter, so a window built after shutdown began sees the latch too.
    public int NavigationGeneration => _navigation.Generation;

    /// Clears the open workspace back to the launcher pane — the same command
    /// the coordinator's close paths route through.
    public ReactiveCommand<Unit, Unit> CloseWorkspaceCommand { get; }

    /// Back to the launcher as the user's own navigation: it also dismisses search and releases the pin.
    public ReactiveCommand<Unit, Unit> NewSessionCommand { get; }

    /// Null without a server sessions service.
    public SessionSearchViewModel? Search { get; }

    /// The search page covers the main area; CurrentWorkspace and its watches carry on beneath it.
    public bool IsSearchOpen {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> OpenSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissSearchCommand { get; }

    readonly Subject<Unit> _searchFocusRequests = new();
    /// The shortcut or the rail field while the page is already open: focus the query, keep its state.
    public IObservable<Unit> SearchFocusRequests => _searchFocusRequests;

    /// The header refresh on the open workspace. Disabled with no workspace, no session
    /// id, or a refresh the user already asked for still running — the same intervals the button is.
    public ReactiveCommand<Unit, Unit> RefreshWorkCommand { get; }

    /// Opens the product documentation. Enabled whatever the server says — a user who cannot reach
    /// a tenant is exactly the one who needs the docs.
    public ReactiveCommand<Unit, Unit> OpenDocsCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenChangelogCommand { get; }

    /// Enabled once the app has a Settings window to open — the same action the app menu and tray
    /// use.
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    volatile Action? _openSettings;

    /// Off macOS, the in-window menu's way to the uninstall; shown only where the app can remove itself.
    public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand { get; }
    volatile Func<Task>? _checkForUpdates;

    public bool ShowsCheckForUpdates {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> UninstallCommand { get; }
    volatile Action? _uninstall;

    public bool ShowsUninstall {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// Off macOS no native app menu is drawn, so Settings, the changelog and the version ride the
    /// rail's help flyout instead.
    public bool AppMenuInWindow { get; }

    public string AppVersionLabel { get; } = $"Desktop app {AppVersion.Display}";

    /// macOS opens its About panel from the native app menu; Windows has none, so the rail's help
    /// flyout opens the app's own.
    public bool ShowsAbout { get; } = OperatingSystem.IsWindows();

    /// Opens the bug/feedback window for one category; inert without an action to route it to.
    public ReactiveCommand<FeedbackCategory, Unit> OpenFeedbackCommand { get; }

    /// Whether the two report items have somewhere to go — the same oracle the menu bar's items use.
    public bool CanOpenFeedback { get; }

    /// Opens the re-auth sign-in surface. Inert without an action to route it to.
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }
    public ReactiveCommand<Unit, Unit> SetupCommand { get; }
    public bool CanOpenSetup { get; }
    public HistoryImportRun? HistoryImport { get; }
    public bool HistoryImportVisible => HistoryImport is not null;
    public bool HistoryImportRunning => HistoryImport is { Running: true };
    /// Counts against the same total setup showed, so the figure reads as one run across both windows.
    public string HistoryImportStatus => HistoryImport is not { } run ? ""
        : run.Running ? $"Importing history · {run.ProcessedOfExpected} sessions processed"
        : run.State is ImportRunState.Failed or ImportRunState.Cancelled
            ? $"History import didn't finish · {run.ProcessedOfExpected} sessions processed. Reopen setup to retry"
        : $"History imported · {run.Processed:N0} sessions processed";
    public double HistoryImportProgress => HistoryImport?.Progress ?? 0;

    ObservableAsPropertyHelper<bool>? _signInVisible;
    /// True while the rail reads Signed out and a sign-in action exists — the help flyout
    /// offers it then; the launcher's Sign in is on the other pane and hidden once a workspace
    /// is open.
    public bool SignInVisible => _signInVisible?.Value ?? false;

    /// No server to ask, or the window reads signed out: the page takes no query.
    public bool SearchSignedOut => Search is null || _searchSignedOut;

    /// The page shows the sign-in prompt in place of the list: signed out, or the server answered
    /// 401 to the page, where the query stays editable and the next edit asks again.
    public bool SearchShowsSignIn => SearchSignedOut || Search?.ListState == SearchListState.SignInRequired;
    bool _searchSignedOut;

    string? _startMessage;
    // Cleared on every new start attempt and on Connected; set when a start attempt fails.
    public string? StartMessage {
        get => _startMessage;
        private set {
            this.RaiseAndSetIfChanged(ref _startMessage, value);
            _startMessageChanges.OnNext(value);
        }
    }

    public ReactiveCommand<Unit, Unit> StartDaemonCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    public TimeProvider Time { get; }

    /// <param name="shutdownToken">
    /// Abandons StartDaemonAsync's WAIT (never the spawned daemon) on app shutdown. Must be linked
    /// to the app lifetime — never CancellationToken.None (an unbounded wait would survive exit).
    /// </param>
    /// <param name="startAction">
    /// Service-aware Start (DaemonLifecycleController.StartActionAsync). Null falls back to plain
    /// StartDaemonAsync — for callers without a live controller (most unit tests).
    /// </param>
    /// <param name="lifecycleStatus">
    /// ILifecycleSurface.Status one-liners ride the same StartMessage lane as start failures.
    /// Null means this lane never receives anything.
    /// </param>
    /// <param name="lifecycleAttention">
    /// ILifecycleSurface.Attention lines on the same StartMessage lane — otherwise a mutation-lane
    /// Start failure only updates the tray and the banner stays mute.
    /// </param>
    /// <param name="navigation">
    /// The composition root's app-lifetime NavigationGate. Null builds a private one, so
    /// a caller with no navigation of its own still gets a working VM — but
    /// only a SHARED gate makes the shutdown latch reach a window built after shutdown began.
    /// </param>
    /// <param name="trackWorkspaceTeardown">
    /// WorkspaceTeardownTracker.Track, as a delegate: this VM only ever registers a teardown, never
    /// drains, and the delegate keeps the drain (a composition-root concern) off its surface. Null
    /// falls back to running the teardown untracked — never to skipping it, or a swap would strand
    /// a live attach.
    /// </param>
    /// <param name="workspaceFactory">
    /// Builds the workspace for an agent id (the production one wires the daemon socket's attach
    /// client and the xterm surface). Null means this window cannot navigate to a workspace at all
    /// — such a caller stays on the Home surface.
    /// </param>
    /// <param name="rail">
    /// The Sessions rail. Null means this window has no rail to keep in sync.
    /// </param>
    /// <param name="laneStatus">
    /// The app's own server lane (IServerLane.Status), for the footer's ServerLaneTip diagnostic
    /// and the connection row's hover. Null means the hover stays on ConnectionStatusTip.
    /// </param>
    /// <param name="restartPending">
    /// DaemonRestartPendingWatcher.Pending. Null means the indicator never shows.
    /// </param>
    /// <param name="originOf">
    /// Which lane an agent id belongs to, for routing a click at the right workspace. Null reads
    /// every id as local — the only answer a caller with no merged directory can give. A null
    /// ANSWER means neither lane holds the id, which opens nothing.
    /// </param>
    /// <param name="remoteWorkspaceFactory">
    /// Builds the card host for a remote row, or returns null when the row is gone by the time it
    /// runs. A null factory means a remote id has no host to open, so it falls back to the local
    /// factory.
    /// </param>
    /// <param name="directory">
    /// The merged rows, watched so an open local workspace can follow its agent to the server lane.
    /// Null means a dropped local row is only ever an ended session — the only reading a caller with
    /// no directory can give it.
    /// </param>
    /// <param name="requestSignIn">
    /// Opens the re-auth sign-in surface (App owns the window). Null hides Sign in in the help
    /// flyout — a window with no dialog to open.
    /// </param>
    public MainWindowViewModel(
            IDaemonClientService service,
            CancellationToken shutdownToken, ActivityViewModel activity, TimeProvider time,
            Func<CancellationToken, Task>? startAction = null,
            IObservable<string?>? lifecycleStatus = null, HomeViewModel? home = null,
            NavigationGate? navigation = null, Action<Func<Task>>? trackWorkspaceTeardown = null,
            Func<string, WorkspaceViewModel>? workspaceFactory = null, SessionRailViewModel? rail = null,
            string? tenantName = null, IObservable<string?>? lifecycleAttention = null,
            IObservable<string?>? identityReport = null,
            IObservable<ServerLaneStatus>? laneStatus = null, IObservable<bool>? restartPending = null,
            Func<string, AgentOrigin?>? originOf = null, Func<string, RemoteSessionViewModel?>? remoteWorkspaceFactory = null,
            IAgentDirectory? directory = null,
            Action<FeedbackCategory>? openFeedback = null, IUrlOpener? opener = null,
            Action? requestSignIn = null, IObservable<Action?>? settingsAction = null,
            IObservable<Func<Task>?>? checkForUpdatesAction = null,
            IObservable<Action?>? uninstallAction = null, bool? appMenuInWindow = null, Action? requestSetup = null,
            HistoryImportRun? historyImport = null,
            IObservable<bool>? backgroundPriority = null, IObservable<ReloadState?>? reloadState = null,
            IObservable<bool>? isReloading = null, Func<CancellationToken, Task>? reloadDaemon = null, Action? openLaunchApprovals = null,
            IObservable<bool>? movingToWindowsService = null,
            Func<string, RemoteSessionViewModel?>? serverWorkspaceFactory = null,
            Func<Func<string, PinPresentation, CancellationToken, Task<SearchOpenOutcome>>, SessionSearchViewModel>? searchFactory = null,
            IServerSessionsService? sessions = null) {
        _service = service;
        _serverFactory = serverWorkspaceFactory;
        Time = time;
        Activity = activity;
        OpenLaunchApprovalsCommand = ReactiveCommand.Create(openLaunchApprovals ?? (() => { }),
            Observable.Return(openLaunchApprovals is not null));
        Home = home;
        _navigation = navigation ?? new NavigationGate();
        _trackTeardown = trackWorkspaceTeardown ?? RunUntracked;
        _workspaceFactory = workspaceFactory;
        _originOf = originOf ?? (_ => AgentOrigin.Local);
        _remoteFactory = remoteWorkspaceFactory;
        _directory = directory;
        _sessions = sessions;
        _onAppServer = directory?.LocalDaemonOnAppServer.Subscribe(on => _localOnAppServer = on) ?? Disposable.Empty;
        Rail = rail;
        TenantName = ProfileLabelForRail(tenantName);
        CloseWorkspaceCommand = ReactiveCommand.Create(CloseWorkspace);
        NewSessionCommand = ReactiveCommand.Create(NewSession);
        Search = sessions is null ? null : searchFactory?.Invoke(OpenSearchHitAsync);
        Search?.Closed.Subscribe(_ => IsSearchOpen = false);
        if (Search is { } page) page.WhenAnyValue(x => x.ListState).Subscribe(_ => this.RaisePropertyChanged(nameof(SearchShowsSignIn)));
        OpenSearchCommand = ReactiveCommand.Create(OpenSearch);
        DismissSearchCommand = ReactiveCommand.Create(CloseSearch);
        var canRefreshWork = Observable.Switch(this.WhenAnyValue(x => x.CurrentWorkspace)
            .Select(CanRefreshOpenWork))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        RefreshWorkCommand = ReactiveCommand.Create(RefreshOpenWork, canRefreshWork);
        CanOpenFeedback     = openFeedback is not null;
        OpenFeedbackCommand = ReactiveCommand.Create<FeedbackCategory>(c => openFeedback?.Invoke(c), Observable.Return(CanOpenFeedback));
        OpenDocsCommand     = ReactiveCommand.Create(() => LinkPolicy.Open(opener ?? new ShellUrlOpener(), AppMenuBar.DocsUrl));
        OpenChangelogCommand = ReactiveCommand.Create(() => LinkPolicy.Open(opener ?? new ShellUrlOpener(), AppMenuBar.ChangelogUrl));
        AppMenuInWindow     = appMenuInWindow ?? !OperatingSystem.IsMacOS();
        var settings        = settingsAction ?? Observable.Return<Action?>(null);
        settings.Subscribe(open => _openSettings = open);
        OpenSettingsCommand = ReactiveCommand.Create(
            () => _openSettings?.Invoke(),
            settings.Select(open => open is not null).ObserveOn(RxSchedulers.MainThreadScheduler));
        var checkForUpdates = (checkForUpdatesAction ?? Observable.Return<Func<Task>?>(null)).ObserveOn(RxSchedulers.MainThreadScheduler);
        checkForUpdates.Subscribe(check => {
            _checkForUpdates     = check;
            ShowsCheckForUpdates = check is not null;
        });
        CheckForUpdatesCommand = ReactiveCommand.Create(() => { _ = _checkForUpdates?.Invoke(); }, checkForUpdates.Select(check => check is not null));
        var uninstall       = (uninstallAction ?? Observable.Return<Action?>(null)).ObserveOn(RxSchedulers.MainThreadScheduler);
        uninstall.Subscribe(run => {
            _uninstall     = run;
            ShowsUninstall = run is not null;
        });
        UninstallCommand    = ReactiveCommand.Create(() => _uninstall?.Invoke(), uninstall.Select(run => run is not null));
        SignInCommand       = ReactiveCommand.Create(() => { requestSignIn?.Invoke(); });
        CanOpenSetup = requestSetup is not null;
        SetupCommand = ReactiveCommand.Create(() => requestSetup?.Invoke());
        HistoryImport = historyImport;
        var offersSignIn    = requestSignIn is not null;

        // ReactiveCommand's own CanExecute observable already ANDs the supplied canExecute with
        // "not currently executing" — no separate in-flight flag is needed to satisfy "Start also disabled while a
        // start is in flight".
        //
        // ReactiveCommand does NOT reschedule the SUPPLIED canExecute onto outputScheduler (only
        // IsExecuting/ThrownExceptions ride it) — without
        // an explicit ObserveOn here, a Status event arriving on a background thread (the
        // service's pump thread) would carry CanExecuteChanged, and therefore a bound Button's
        // IsEnabled write, onto that same background thread, tripping Avalonia's dispatcher
        // thread-affinity check. These stay constructor-scoped (not inside WhenActivated) since
        // commands must exist and be assertable pre-activation — see the class doc comment.
        // One primary action at a time: Start when nothing is listening (spawn/reattach via the
        // lifecycle); Reconnect when Start is not the right next step (skew, or still connecting).
        // The move to the Windows service takes the daemon down and brings it back itself: a Start offered
        // in that gap reads as a failure the user must fix.
        var canStart = service.Status
            .CombineLatest(movingToWindowsService ?? Observable.Return(false),
                (s, moving) => s.State == AttachState.Unreachable && s.Reason == UnreachableReason && !moving)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        var canRetry = service.Status
            .Select(s =>
                s.State == AttachState.Connecting
                || (s.State == AttachState.Unreachable && s.Reason is not (UnreachableReason or ExpectedDaemonRestart.Reason)))
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var start = startAction ?? RunStartAsync;
        StartDaemonCommand = ReactiveCommand.CreateFromTask(
            () => InvokeStartAsync(start, shutdownToken), canStart);
        RetryCommand = ReactiveCommand.CreateFromTask(InvokeRetryAsync, canRetry);

        // The launcher banner owns the Start/Reconnect buttons. It takes the same commands and the
        // raw canStart/canRetry predicates, not CanExecute: CanExecute also ANDs "not executing",
        // which would hide a button mid-attempt instead of only disabling it. service.Status is
        // hot/multicast, so this second subscriber replays the current value like the first.
        home?.AttachDaemonRecovery(
            StartDaemonCommand, RetryCommand, canStart, canRetry, _startMessageChanges);

        var reloadAction = reloadDaemon ?? (_ => Task.CompletedTask);
        ReloadDaemonCommand = ReactiveCommand.CreateFromTask(
            (CancellationToken ct) => reloadAction(ct),
            service.Status
                .CombineLatest(isReloading ?? Observable.Return(false), (st, reloading) => st.State == AttachState.Connected && !reloading)
                .DistinctUntilChanged()
                .ObserveOn(RxSchedulers.MainThreadScheduler));

        this.WhenActivated(disposables => {
            if (historyImport is not null)
                historyImport.WhenAnyValue(x => x.State, x => x.Processed, x => x.PassesDone, (state, processed, passes) => (state, processed, passes))
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(_ => {
                        this.RaisePropertyChanged(nameof(HistoryImportStatus));
                        this.RaisePropertyChanged(nameof(HistoryImportRunning));
                        this.RaisePropertyChanged(nameof(HistoryImportProgress));
                    })
                    .DisposeWith(disposables);
            var status    = service.Status.ObserveOn(RxSchedulers.MainThreadScheduler);
            var snapshots = service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler);

            _daemonName = snapshots.Select(s => s.Daemon.Name)
                .ToProperty(this, x => x.DaemonName, "")
                .DisposeWith(disposables);

            _showsDaemonLine = snapshots
                .Select(s => !string.IsNullOrEmpty(s.Daemon.Name) || StripBuildMetadata(s.Daemon.Version).Length > 0)
                .ToProperty(this, x => x.ShowsDaemonLine, false)
                .DisposeWith(disposables);

            _versionDisplay = snapshots.Select(s => StripBuildMetadata(s.Daemon.Version))
                .ToProperty(this, x => x.VersionDisplay, "")
                .DisposeWith(disposables);

            _serverUrl = snapshots.Select(s => s.Daemon.ServerUrl)
                .ToProperty(this, x => x.ServerUrl, "")
                .DisposeWith(disposables);

            // Seeded with "" so this fires even before the FIRST snapshot ever arrives (a daemon
            // never previously connected has nothing in Snapshots yet) — ConnectionDisplayFor/
            // StatusDotFor only read the daemon-connection word once status.State is Connected,
            // where DaemonClientService's ordering guarantee (snapshot applied before the Connected
            // transition, see its own comment) means a real value is always already there by then.
            var daemonConnection = snapshots.Select(s => s.Daemon.Connection).StartWith("");

            // Expired sign-in is a separate diagnosis from the daemon hub word: auto-reconnect
            // will not restore a session, so the footer must not say Reconnecting. The lane
            // covers ParkSignedOut; Home's stream also covers a 401 launch that has not parked.
            var fromLane = (laneStatus ?? Observable.Return(new ServerLaneStatus(ServerLaneState.Dormant)))
                .Select(s => s.State == ServerLaneState.SignedOut);
            var fromHome = Home?.SignInExpired ?? Observable.Return(false);
            var signInExpired = fromLane.CombineLatest(fromHome, (lane, notice) => lane || notice)
                .DistinctUntilChanged()
                .ObserveOn(RxSchedulers.MainThreadScheduler);

            // The daemon's own sign-in explains a hub that will not connect, so the footer says
            // Signed out instead of reconnecting forever. The app's Sign in repairs it only when the
            // daemon runs on the app's own profile.
            var daemonSignIn = status
                .CombineLatest(snapshots.Select(s => (DaemonInfoDto?)s.Daemon).StartWith((DaemonInfoDto?)null),
                    (st, daemon) => DaemonSignInFor(st, daemon, tenantName))
                .DistinctUntilChanged()
                .ObserveOn(RxSchedulers.MainThreadScheduler);
            var signedOut = signInExpired.CombineLatest(daemonSignIn, (app, daemon) => app || daemon.SignedOut)
                .DistinctUntilChanged();

            var signInVisible = signInExpired
                .CombineLatest(daemonSignIn, (app, daemon) => (app || daemon is { SignedOut: true, OursToRepair: true }) && offersSignIn);
            _signInVisible = signInVisible
                .ToProperty(this, x => x.SignInVisible, false)
                .DisposeWith(disposables);
            // Its own copy: an OAPH notifies before its new value is readable.
            signInVisible.Subscribe(visible => {
                var signedBackIn = _searchSignedOut && !visible;
                _searchSignedOut = visible;
                this.RaisePropertyChanged(nameof(SearchSignedOut));
                this.RaisePropertyChanged(nameof(SearchShowsSignIn));
                if (signedBackIn) Search?.RetrySignIn();
            }).DisposeWith(disposables);

            // The banner owns update progress; the rail keeps identity while services restart.
            var localUpgrade = Home?.LocalStack is { } stack
                ? stack.WhenAnyValue(x => x.Upgrading, x => x.IsStarting, (upgrading, starting) => upgrading || starting)
                : Observable.Return(false);
            var updating = (Home?.UpdateFinishing ?? Observable.Return(false))
                .CombineLatest(localUpgrade, (app, local) => app || local).DistinctUntilChanged();
            _showsUpdateContext = updating.CombineLatest(signedOut, (active, expired) => active && !expired)
                .ToProperty(this, x => x.ShowsUpdateContext, false)
                .DisposeWith(disposables);
            _connectionDisplay = status.CombineLatest(daemonConnection, signedOut, ConnectionDisplayFor)
                .ToProperty(this, x => x.ConnectionDisplay, "")
                .DisposeWith(disposables);

            // The rail describes the local daemon even while the launcher targets a remote machine.
            // Keep its lifecycle failure independent of the selected machine's banner.
            var localFailed = status.CombineLatest(_startMessageChanges,
                (st, message) => st.State == AttachState.Unreachable
                    && HomeViewModel.BannerFailedFor((HomeViewModel.DaemonDownNotice, message)));
            var failed = localFailed.CombineLatest(
                    Home is { } home ? home.WhenAnyValue(x => x.BannerFailed) : Observable.Return(false),
                    (local, banner) => local || banner)
                .DistinctUntilChanged();
            _statusBrush = status.CombineLatest(daemonConnection, signedOut, failed, StatusBrushFor)
                .ToProperty(this, x => x.StatusBrush, Paint(StatusColors.Unavailable))
                .DisposeWith(disposables);

            _agentCountText = status.CombineLatest(snapshots, (st, snap) => (st, snap))
                .Select(t => t.st.State != AttachState.Connected ? ""
                    : t.snap.Daemon.MaxAgents == 0
                        ? $"{t.snap.Daemon.ActiveAgents} agents (unlimited)"
                        : $"{t.snap.Daemon.ActiveAgents} of {t.snap.Daemon.MaxAgents} agents")
                .ToProperty(this, x => x.AgentCountText, "")
                .DisposeWith(disposables);

            // Only while attached: a marker left by a daemon we cannot reach says nothing about
            // what the user is looking at. The watcher publishes from its poll thread, so it is
            // marshalled like status/snapshots above before it touches a bound property.
            var pendingWhileConnected = status
                .CombineLatest(
                    (restartPending ?? Observable.Return(false)).ObserveOn(RxSchedulers.MainThreadScheduler),
                    (st, pending) => pending && st.State == AttachState.Connected)
                .DistinctUntilChanged();

            _restartPending = pendingWhileConnected
                .ToProperty(this, x => x.RestartPending, false)
                .DisposeWith(disposables);

            _restartPendingText = pendingWhileConnected.Select(p => p ? RestartPendingMessage : null)
                .ToProperty(this, x => x.RestartPendingText, (string?)null)
                .DisposeWith(disposables);

            var backgroundSource = (backgroundPriority ?? Observable.Return(false)).ObserveOn(RxSchedulers.MainThreadScheduler);
            var reloadingSource  = (isReloading ?? Observable.Return(false)).ObserveOn(RxSchedulers.MainThreadScheduler);
            var reloadSource     = (reloadState ?? Observable.Return<ReloadState?>(null)).ObserveOn(RxSchedulers.MainThreadScheduler);

            var backgroundWhileConnected = status
                .CombineLatest(backgroundSource, (st, bg) => bg && st.State == AttachState.Connected)
                .DistinctUntilChanged();
            _backgroundPriority = backgroundWhileConnected
                .ToProperty(this, x => x.BackgroundPriority, false)
                .DisposeWith(disposables);
            _backgroundPriorityText = backgroundWhileConnected.Select(b => b ? BackgroundPriorityMessage : null)
                .ToProperty(this, x => x.BackgroundPriorityText, (string?)null)
                .DisposeWith(disposables);

            _isReloading = reloadingSource
                .ToProperty(this, x => x.IsReloading, false)
                .DisposeWith(disposables);

            var failureText = reloadSource.Select(state => state is null ? null : ReloadCopy.For(state));
            _reloadFailureText = failureText
                .ToProperty(this, x => x.ReloadFailureText, (string?)null)
                .DisposeWith(disposables);
            _showsReloadBlock = backgroundWhileConnected
                .CombineLatest(failureText, (bg, text) => bg || text is not null)
                .DistinctUntilChanged()
                .ToProperty(this, x => x.ShowsReloadBlock, false)
                .DisposeWith(disposables);

            _state = status.Select(s => s.State)
                .ToProperty(this, x => x.State, AttachState.Connecting)
                .DisposeWith(disposables);

            var loadingSessions = status.Select(s => s.State == AttachState.Connecting).DistinctUntilChanged();
            _loadingSessions = loadingSessions
                .ToProperty(this, x => x.LoadingSessions, true)
                .DisposeWith(disposables);
            var railEmpty = Rail?.WhenAnyValueUnsafe(r => r.IsEmpty) ?? Observable.Return(false);
            _showsNoSessions = loadingSessions.CombineLatest(railEmpty, (loading, empty) => !loading && empty)
                .DistinctUntilChanged()
                .ToProperty(this, x => x.ShowsNoSessions, false)
                .DisposeWith(disposables);

            _reason = status.Select(ReasonText)
                .ToProperty(this, x => x.Reason, (string?)null)
                .DisposeWith(disposables);

            var lane = (laneStatus ?? Observable.Empty<ServerLaneStatus>())
                .ObserveOn(RxSchedulers.MainThreadScheduler);

            _serverLaneTip = lane
                .Select(s => s.Diagnostic)
                .ToProperty(this, x => x.ServerLaneTip, (string?)null)
                .DisposeWith(disposables);

            _connectionHasDetail = lane
                .Select(s => !string.IsNullOrWhiteSpace(s.Diagnostic))
                .ToProperty(this, x => x.ConnectionHasDetail, false)
                .DisposeWith(disposables);

            // A connect clears the progress lines back to the standing identity report, whichever of the two the
            // main thread happens to see first.
            string? identity = null;
            // Each lifecycle lane's line until it is withdrawn, or a connect or identity report replaces it.
            var laneLines = new string?[2];
            identityReport?.ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(report => {
                    var shown = identity;
                    identity = report;
                    if (report is not null) {
                        laneLines.AsSpan().Clear();
                        StartMessage = report;
                    }
                    else if (shown is not null && ReferenceEquals(StartMessage, shown)) StartMessage = null;
                })
                .DisposeWith(disposables);
            status.Where(s => s.State == AttachState.Connected)
                .Subscribe(_ => {
                    laneLines.AsSpan().Clear();
                    StartMessage = identity;
                })
                .DisposeWith(disposables);

            // Reconnect / already-running kicks only reattach. If we land Unreachable again while
            // still showing an in-flight reconnect copy, replace it so the banner does not claim
            // reconnect forever.
            status.Where(s => s.State == AttachState.Unreachable)
                .Subscribe(_ => {
                    if (IsInFlightReconnectMessage(StartMessage))
                        StartMessage = ReconnectFailedMessage;
                })
                .DisposeWith(disposables);

            // Status (start-action one-liners) and Attention (mutation-outcome presentation) both
            // land on StartMessage so the launcher banner is never mute after a Start daemon click.
            // A null takes back that lane's own line, if nothing has replaced it since, and shows
            // the other lane's line again when it is still standing.
            void BindStartMessage(IObservable<string?>? source, int lane) =>
                source?.ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(msg => {
                        var posted = laneLines[lane];
                        laneLines[lane] = msg;
                        if (msg is not null) StartMessage = msg;
                        else if (posted is not null && ReferenceEquals(StartMessage, posted)) StartMessage = laneLines[1 - lane] ?? identity;
                    })
                    .DisposeWith(disposables);

            BindStartMessage(lifecycleStatus, 0);
            BindStartMessage(lifecycleAttention, 1);
        });
    }

    /// Rail click: swaps to this session's workspace — the local one, or the remote card
    /// host when the id belongs to another machine. A caller that knows which row was clicked says
    /// so; without an origin the id is resolved, and a same-id pair on both lanes resolves local.
    /// Refused once shutdown has latched — a new workspace is a new attach, and quiesce is already
    /// running. True when the session is the open workspace afterwards.
    public bool OpenSession(string agentId, AgentOrigin? origin = null) => Navigate(agentId, origin, sessionId: null);

    /// `sessionId` is the session the caller asked for; without one it is read off what opened.
    bool Navigate(string agentId, AgentOrigin? origin, string? sessionId) {
        if (!OpenSessionCore(agentId, origin, revealInRail: true)) return false;
        CloseSearch();
        ReleasePinUnless(sessionId ?? SessionOfOpen());
        return true;
    }

    void NewSession() {
        CloseSearch();
        CloseWorkspace();
    }

    void OpenSearch() {
        if (IsSearchOpen) {
            _searchFocusRequests.OnNext(Unit.Default);
            return;
        }
        Search?.Reset();
        IsSearchOpen = true;
        _navigation.Bump();
    }

    /// A closed page keeps no query, so a reconnect or an access change has nothing to ask again.
    void CloseSearch() {
        IsSearchOpen = false;
        Search?.Reset();
        if (CurrentWorkspace is null) ReleasePinUnless(null);
    }

    /// Unpin also supersedes a pin still reading, so it runs when only the request is outstanding.
    void ReleasePinUnless(string? sessionId) {
        if (_sessions is null || (_sessions.PinnedSessionId ?? _searchPin) is not { } pinned) return;
        if (sessionId is not null && SessionIds.SameSession(pinned, sessionId)) return;
        _searchPin = null;
        _sessions.Unpin();
    }

    string? SessionOfOpen() {
        var open = CurrentWorkspace;
        if (KeyOf(open) is { } key && _directory?.Rows.Lookup(key) is { HasValue: true, Value.SessionId: { Length: > 0 } listed })
            return listed;
        return open switch {
            RemoteSessionViewModel { IsSessionOnly: true } session => session.AgentId,
            RemoteSessionViewModel remote => SessionOf(remote),
            WorkspaceViewModel { SessionId: { Length: > 0 } id } => id,
            _ => null,
        };
    }

    /// Opens what a rail click on the hit's session would, and pins the session. A listed session
    /// opens at once and pins beside it; one the directory has no row for is pinned first, and opens
    /// only if the page is still up and the intent still current when the pin lands.
    public async Task<SearchOpenOutcome> OpenSearchHitAsync(string sessionId, PinPresentation presentation, CancellationToken ct) {
        if (_directory is null) return SearchOpenOutcome.Failed;
        if (_directory.RowForSession(sessionId) is { } listed) {
            // Set after Navigate, whose release of an earlier pin would otherwise clear it.
            if (!Navigate(listed.Id, listed.Origin, sessionId)) return SearchOpenOutcome.Failed;
            _searchPin = sessionId;
            _searchPinPresentation = presentation;
            // A failed pin only costs the session its exemption from the rail's filters later.
            _ = _sessions?.PinAsync(sessionId, presentation, CancellationToken.None);
            return SearchOpenOutcome.Opened;
        }
        if (_sessions is null) return SearchOpenOutcome.Failed;
        // A pin that lands replaces the selected session's; abandoning the open puts that one back.
        (string Id, PinPresentation Presentation)? restore =
            _sessions.PinnedSessionId is { } held ? (held, _searchPinPresentation) : null;
        _searchPin = sessionId;
        var pinned = await _sessions.PinAsync(sessionId, presentation, ct);
        switch (pinned) {
            case PinOutcome.Unauthorized: return SearchOpenOutcome.SignInRequired;
            case PinOutcome.Unavailable: return SearchOpenOutcome.Unavailable;
            case PinOutcome.Failed: return SearchOpenOutcome.Failed;
            case PinOutcome.Superseded: return SearchOpenOutcome.Superseded;
        }
        _searchPinPresentation = presentation;
        var current = !ct.IsCancellationRequested && IsSearchOpen;
        if (!current || _directory.RowForSession(sessionId) is not { } found) {
            ReleaseUnopenedPin(sessionId, restore);
            return current ? SearchOpenOutcome.Failed : SearchOpenOutcome.Superseded;
        }
        if (Navigate(found.Id, found.Origin, sessionId)) return SearchOpenOutcome.Opened;
        ReleaseUnopenedPin(sessionId, restore);
        return SearchOpenOutcome.Failed;
    }

    /// A pin that landed for a session nothing opened would keep a rail row for it.
    void ReleaseUnopenedPin(string sessionId, (string Id, PinPresentation Presentation)? restore) {
        if (_sessions?.PinnedSessionId is not { } pinned || !SessionIds.SameSession(pinned, sessionId)) return;
        var open = SessionOfOpen();
        if (open is not null && SessionIds.SameSession(pinned, open)) return;
        _searchPin = null;
        _sessions.Unpin();
        if (restore is not { } previous || open is null || !SessionIds.SameSession(previous.Id, open)) return;
        _searchPin = previous.Id;
        _searchPinPresentation = previous.Presentation;
        _ = _sessions.PinAsync(previous.Id, previous.Presentation, CancellationToken.None);
    }

    bool OpenSessionCore(string agentId, AgentOrigin? origin, bool revealInRail) {
        if (_navigation.ShutdownLatched) return false;

        // Neither lane holds the id: opening the local workspace for it would attach a terminal to
        // an agent this machine never ran.
        if ((origin ?? _originOf(agentId)) is not { } lane) return false;

        // Re-clicking the open session must not tear down and rebuild a live attach. The other
        // lane's same-id agent is a different agent, and a remote host the local daemon has taken
        // over no longer owns the id at all: both are a real swap.
        if (CurrentWorkspace is { } open && open.AgentId == agentId && !Superseded(open, lane)) {
            if (revealInRail) Rail?.NotifySessionOpened(agentId, lane);
            return true;
        }

        ISessionWorkspace? next = lane switch {
            AgentOrigin.Remote => _remoteFactory?.Invoke(agentId),
            AgentOrigin.Server => _serverFactory?.Invoke(agentId),
            _ => _workspaceFactory?.Invoke(agentId),
        };
        if (next is null) return false;

        SwapTo(next);
        if (revealInRail) Rail?.NotifySessionOpened(agentId, lane);
        return true;
    }

    public void OpenSessionPullRequest(string agentId, AgentOrigin origin, PullRequestSubjectDto? subject = null) {
        if (_navigation.ShutdownLatched) return;
        OpenSession(agentId, origin);
        if (CurrentWorkspace?.AgentId != agentId || Superseded(CurrentWorkspace, origin)) return;
        CurrentWorkspace.OpenPullRequest(subject);
    }

    static bool Superseded(ISessionWorkspace open, AgentOrigin lane) =>
        open is RemoteSessionViewModel remote
            ? remote.IsSessionOnly ? lane != AgentOrigin.Server : lane != AgentOrigin.Remote || remote.OriginChangedToLocal
            : lane != AgentOrigin.Local;

    /// The launch auto-open. `generation` is what the launch captured BEFORE its call: a success
    /// arriving after any navigation (closing the workspace, another session, close-to-hide, the
    /// shutdown latch) opens nothing, rather than attaching an invisible terminal or replacing what
    /// the user opened while the launch was in flight.
    public void OpenSessionIfCurrent(string agentId, AgentOrigin origin, int generation) {
        if (generation != _navigation.Generation) return;
        OpenSession(agentId, origin);
    }

    /// The launcher words a launch failure, and an open workspace covers the launcher. Only a
    /// workspace the daemon never filled is closed: a failure report can trail the agent it names.
    public void CloseFailedLaunch(string agentId) {
        if (CurrentWorkspace is WorkspaceViewModel { HasAgent: false } open
            && AgentIds.Normalize(open.AgentId) == AgentIds.Normalize(agentId))
            CloseWorkspace();
    }

    /// The coordinator's close paths. Before shutdown, even a close-to-hide with no workspace
    /// open must retire an in-flight launch's captured generation. Shutdown already released the
    /// workspace; its final window-close notification must not touch disposed dependencies.
    public void CloseWorkspace() {
        if (_shutdownLatched || _navigation.ShutdownLatched) return;
        SwapTo(null);
        // Nothing is selected, so nothing keeps a pin; an open page may still be pinning its hit.
        if (!IsSearchOpen) ReleasePinUnless(null);
    }

    static IObservable<bool> CanRefreshOpenWork(ISessionWorkspace? workspace) =>
        workspace is { WorkContext: var work }
            ? work.WhenAnyValue(w => w.HasSession, w => w.IsRefreshing, (hasSession, refreshing) => hasSession && !refreshing)
            : Observable.Never<bool>().StartWith(false);

    // The menu gesture and the key binding can both deliver one press. IsRefreshing flips
    // inside the work command, before this returns, so the second delivery no-ops.
    void RefreshOpenWork() {
        if (CurrentWorkspace is not { WorkContext: { HasSession: true, IsRefreshing: false } work }) return;
        work.RefreshCommand.Execute().Subscribe();
    }

    /// First shutdown pass: unhook the live workspace and register its teardown before the drain
    /// seals the tracker, then latch so no later window opens another. A workspace that never
    /// closed would otherwise register teardown after drain against already-disposed deps.
    public void LatchShutdown() {
        // The confirmed shutdown pass runs after the shared rail has been disposed.
        // Each window releases its workspace once, even though the app latches on every pass.
        if (_shutdownLatched) return;
        _shutdownLatched = true;
        var live = CurrentWorkspace;
        CurrentWorkspace = null;
        _rebind.Disposable = Disposable.Empty;
        _handoff.Disposable = Disposable.Empty;
        if (Rail is not null) Rail.SelectedSessionKey = null;
        _navigation.Latch();
        if (live is not null) _trackTeardown(live.TeardownAsync);
    }

    void SwapTo(ISessionWorkspace? next) {
        var outgoing = CurrentWorkspace;
        CurrentWorkspace = next;
        if (Rail is not null) Rail.SelectedSessionKey = KeyOf(next);
        _navigation.Bump();
        if (outgoing is not null) _trackTeardown(outgoing.TeardownAsync);
        _handoff.Disposable = Disposable.Empty;
        // Last, so a watch that fires synchronously on its first element cannot have the swap it
        // caused overwritten by the arming that is still returning.
        WatchOpen(next);
    }

    static string? KeyOf(ISessionWorkspace? workspace) => workspace switch {
        RemoteSessionViewModel { IsSessionOnly: true } session => $"server:{session.AgentId}",
        RemoteSessionViewModel remote => $"remote:{remote.AgentId}",
        null => null,
        _ => $"local:{workspace.AgentId}",
    };

    /// An open workspace follows its row across lanes, both directions, carrying the tab in use.
    /// Once its session ends it hands off to the server's row for that session, which opens the
    /// ended session read-only; a session the server never lists closes as the rail has dropped it,
    /// and a failed local agent closes at once so its reason stays where the rail shows it. A
    /// session-only workspace closes on the server's own word that the session is gone, and follows
    /// the session onto a daemon row that takes it up; a row removed by a page cut or a scope switch
    /// is neither, and the workspace outlives it.
    void WatchOpen(ISessionWorkspace? workspace) {
        var moves = workspace switch {
            RemoteSessionViewModel { IsSessionOnly: false } remote => remote.OriginChangedChanges
                .Where(moved => moved)
                .Take(1)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => Rebind(remote, AgentOrigin.Local, remote.AgentId, remote.IsTerminalActive, remote.IsPullRequestActive)),
            // Not a one-shot: a swap the host cannot complete leaves the watch armed for the next
            // proof, and one that completes retires it through the swap's own re-arming.
            WorkspaceViewModel local when _directory is { } directory => LocalRowMoves(directory, local.AgentId)
                .Subscribe(_ => Rebind(local, AgentOrigin.Remote, local.AgentId, local.IsTerminalActive, local.IsPullRequestActive)),
            _ => Disposable.Empty,
        };
        var ended = workspace switch {
            WorkspaceViewModel local => local.Ended
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => {
                    if (!ReferenceEquals(CurrentWorkspace, workspace)) return;
                    if (local.LastStatus == "Completed" && _localOnAppServer && local.SessionId is { Length: > 0 } sessionId && _directory is { } directory)
                        ArmHandoff(local, directory, sessionId, $"local:{local.AgentId}", closeWithout: true);
                    else CloseWorkspace();
                }),
            RemoteSessionViewModel { IsSessionOnly: true } session when _directory is { } directory => new CompositeDisposable(
                (_sessions?.Dropped ?? Observable.Empty<string>())
                    .Where(id => id == session.AgentId)
                    .Take(1)
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(_ => { if (ReferenceEquals(CurrentWorkspace, workspace)) CloseWorkspace(); }),
                directory.Rows.Connect()
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .SelectMany(changes => changes)
                    .Where(change => change.Key == $"server:{session.AgentId}" && change.Reason == ChangeReason.Remove)
                    .Subscribe(_ => {
                        if (!ReferenceEquals(CurrentWorkspace, workspace)) return;
                        if (DaemonRowFor(directory, session.AgentId) is { } row)
                            Rebind(session, row.Origin, row.Id, terminal: false, pullRequest: session.IsPullRequestActive);
                    })),
            RemoteSessionViewModel { IsSessionOnly: false } remote => remote.SessionEndedChanges
                .DistinctUntilChanged()
                .Where(ended => ended)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => {
                    if (!ReferenceEquals(CurrentWorkspace, workspace) || remote.OriginChangedToLocal) return;
                    if (_directory is { } directory && _directory.Rows.Lookup($"remote:{remote.AgentId}") is { HasValue: false }
                        && SessionOf(remote) is { Length: > 0 } sessionId)
                        ArmHandoff(remote, directory, sessionId, $"remote:{remote.AgentId}", closeWithout: false);
                }),
            _ => Disposable.Empty,
        };
        var watch = new CompositeDisposable(moves, ended);
        // A watch that fired while it was still being armed has already swapped the workspace and
        // armed the next one: keeping this subscription would retire that one unwatched.
        if (ReferenceEquals(CurrentWorkspace, workspace)) _rebind.Disposable = watch;
        else watch.Dispose();
    }

    /// Fires once a dropped local row's own session stands live on the server lane. The registry
    /// can list the twin before it knows its session id, so the dropped row's id is kept and the
    /// twin's later revisions are held against it until the local row returns.
    static IObservable<bool> LocalRowMoves(IAgentDirectory directory, string agentId) {
        var localKey = $"local:{agentId}";
        var remoteKey = $"remote:{agentId}";
        string? droppedSession = null;
        return directory.Rows.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SelectMany(changes => changes)
            .Where(change => {
                if (change.Key == localKey) droppedSession = change.Reason == ChangeReason.Remove ? change.Current.SessionId : null;
                else if (change.Key != remoteKey) return false;
                // Judged on the directory as it stands, never on the change: a queued revision can
                // describe a twin the directory has since dropped, with the local row back.
                return droppedSession is { Length: > 0 }
                    && !directory.Rows.Lookup(localKey).HasValue
                    && directory.Rows.Lookup(remoteKey) is { HasValue: true, Value: var twin }
                    && MovedToServer(twin, droppedSession);
            })
            .Select(_ => true);
    }

    /// The dropped row's own session, still live on the server lane. A shared agent id proves
    /// nothing on its own — the directory's dedup fails open, so two unrelated agents can carry one
    /// id — which is why the session ids must match, the same proof the remote host demands of a
    /// local twin before it hands the id over.
    static bool MovedToServer(AgentRow twin, string? droppedSession) =>
        droppedSession is { Length: > 0 }
        && !SessionStatusDots.IsTerminal(twin.Status)
        && twin.SessionId == droppedSession;

    static string? SessionOf(RemoteSessionViewModel remote) => remote.SessionId is { Length: > 0 } id ? id : null;

    /// The daemon row that has taken up a session the server lists under its own spelling of the id:
    /// one its lane vouches for now. A completed row, or a cached one whose socket or registry is
    /// gone, is history the server row's removal exposes again, not a takeover.
    static AgentRow? DaemonRowFor(IAgentDirectory directory, string serverSessionId) =>
        directory.Rows.Items.FirstOrDefault(r => directory.IsHostedNow(r) && SessionIds.SameSession(serverSessionId, r.SessionId));

    enum HandoffOutcome { ServerRow, Returned, Lapsed }

    /// Waits for the server's row for the session, within the grace, unless the daemon row comes
    /// back live first. `closeWithout` is the local workspace's rule: nothing arrived, so close as
    /// the rail already dropped it; a hosted remote workspace keeps its retained transcript instead.
    void ArmHandoff(ISessionWorkspace open, IAgentDirectory directory, string sessionId, string daemonKey, bool closeWithout) {
        // The server spells the id its own way, so its row is found by identity rather than by key.
        AgentRow? EndedServerRow() => directory.Rows.Items.FirstOrDefault(r => r.IsEndedSession && SessionIds.SameSession(r.SessionId, sessionId));
        // The daemon row has returned only when a change to that row after arming shows it live and
        // the directory still says so when the change is read: the directory can carry the row as
        // running at the instant the workspace learns it completed, another row's revision proves
        // nothing about it, and a burst can queue a live revision ahead of the end.
        var outcomes = directory.Rows.Connect(suppressEmptyChangeSets: false)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Skip(1)
            .Select(changes => changes.Any(change => change.Key == daemonKey
                    && change.Reason is ChangeReason.Add or ChangeReason.Update or ChangeReason.Refresh
                    && !SessionStatusDots.IsTerminal(change.Current.Status))
                && directory.Rows.Lookup(daemonKey) is { HasValue: true, Value: var back } && !SessionStatusDots.IsTerminal(back.Status))
            .StartWith(false)
            .Select(returned => returned
                ? HandoffOutcome.Returned
                : EndedServerRow() is not null
                    ? HandoffOutcome.ServerRow
                    : (HandoffOutcome?)null)
            .Where(outcome => outcome is not null)
            .Select(outcome => outcome!.Value)
            .Merge(After(Time, AgentDirectory.HandoverGrace).Select(_ => HandoffOutcome.Lapsed))
            .Take(1);
        _handoff.Disposable = outcomes.Subscribe(outcome => {
            if (!ReferenceEquals(CurrentWorkspace, open)) return;
            switch (outcome) {
                case HandoffOutcome.ServerRow when EndedServerRow() is { } row: Rebind(open, AgentOrigin.Server, row.Id, terminal: false, pullRequest: false); break;
                case HandoffOutcome.Lapsed when closeWithout: CloseWorkspace(); break;
            }
        });
    }

    readonly SerialDisposable _handoff = new();

    static IObservable<Unit> After(TimeProvider time, TimeSpan due) => Observable.Create<Unit>(observer => {
        var timer = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(() => observer.OnNext(Unit.Default)), null, due, Timeout.InfiniteTimeSpan);
        return timer;
    });

    void Rebind(ISessionWorkspace open, AgentOrigin origin, string id, bool terminal, bool pullRequest) {
        if (!ReferenceEquals(CurrentWorkspace, open)) return;
        var subject = open.PullRequests?.Selected?.Subject;
        OpenSessionCore(id, origin, revealInRail: false);
        if (ReferenceEquals(CurrentWorkspace, open)) return;
        if (origin != AgentOrigin.Server) open.HandDraftTo(origin);
        if (terminal) {
            switch (CurrentWorkspace) {
                case WorkspaceViewModel local: local.ShowTerminalCommand.Execute().Subscribe(); break;
                case RemoteSessionViewModel remote: remote.ShowTerminalCommand.Execute().Subscribe(); break;
            }
        } else if (pullRequest) CurrentWorkspace?.OpenPullRequest(subject);
    }

    // A VM built without a tracker (a test, or any caller predating workspaces) must still not
    // strand a live attach: run the teardown and observe its fault exactly like the tracker's own
    // wrapper does. The teardown is bounded by TerminalTabViewModel's own budget, so this cannot
    // run away.
    static void RunUntracked(Func<Task> teardown) {
        try {
            _ = teardown().ContinueWith(
                t => Console.Error.WriteLine($"kcap app: untracked workspace teardown failed: {t.Exception}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: untracked workspace teardown failed: {ex}");
        }
    }

    static string? ReasonText(AttachStatus status) => status.State switch {
        AttachState.Unreachable when status.Reason == IncompatibleReason => HomeViewModel.DaemonIncompatibleNotice,
        AttachState.Unreachable when status.Reason == ExpectedDaemonRestart.Reason => HomeViewModel.DaemonRestartingNotice,
        AttachState.Unreachable => HomeViewModel.DaemonDownNotice,
        _ => null,
    };

    // SEMVER-only: everything from the first '+' is build metadata (e.g. "1.2.3+a1b2c3"), never
    // meaningful on a compact status line. Null/empty-safe — returns the input unchanged.
    internal static string StripBuildMetadata(string? version) {
        if (string.IsNullOrEmpty(version)) return version ?? "";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// Named profiles stay visible beside Connected; the built-in "default" profile does not —
    /// that word collides with the model/effort "Default" elsewhere in the chrome.
    internal static string ProfileLabelForRail(string? profileName) =>
        string.IsNullOrWhiteSpace(profileName)
        || string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : profileName;

    internal const string SignedOutDisplay = "Signed out";

    /// Whether the daemon reports its own sign-in as what keeps its hub from connecting, and whether
    /// signing the app in repairs that: only with a resolved app profile, and the daemon on it or not
    /// saying which.
    internal static (bool SignedOut, bool OursToRepair) DaemonSignInFor(
            AttachStatus status, DaemonInfoDto? daemon, string? appProfile) =>
        status.State == AttachState.Connected && daemon is { Credential: not null } && daemon.Connection != "connected"
            ? (true, appProfile is not null
                     && (daemon.Profile is null || string.Equals(daemon.Profile, appProfile, StringComparison.Ordinal)))
            : (false, false);

    // Single word for the merged status line. Expired sign-in wins: waiting on the daemon hub
    // will not restore a session. Otherwise local attach State is checked FIRST — the daemon's
    // own upstream Connection word is only meaningful once State is Connected (see the
    // daemonConnection seam comment above); an Unreachable/Connecting attach state always wins
    // regardless of whatever Connection word a stale retained snapshot might carry.
    internal static string ConnectionDisplayFor(
            AttachStatus status, string daemonConnection, bool signInExpired = false) {
        if (signInExpired) return SignedOutDisplay;
        if (status.State == AttachState.Connecting) return "Connecting…";
        if (status.State == AttachState.Unreachable)
            return status.Reason switch {
                IncompatibleReason => "Incompatible",
                ExpectedDaemonRestart.Reason => "Restarting…",
                _ => "Unreachable",
            };

        var word = Capitalize(daemonConnection);
        return daemonConnection is "connecting" or "reconnecting" ? word + "…" : word;
    }

    /// The launcher's tones: red when something broke, amber when it needs the user, the spinner's grey while
    /// work is in progress. Kept as its own switch, not derived from the text,
    /// so a wording change there cannot retune it.
    /// <param name="failed">The launcher's card reports a failure: versions that cannot work together, a local
    /// server that refused the app, or a daemon still down after a start that did not work.</param>
    internal static IBrush StatusBrushFor(
            AttachStatus status, string daemonConnection, bool signInExpired = false, bool failed = false) {
        if (signInExpired) return Paint(failed ? StatusColors.Disrupted : StatusColors.InProgress);
        if (status.State == AttachState.Connecting) return Paint(StatusColors.Pending);
        if (status.State == AttachState.Unreachable)
            return status.Reason switch {
                IncompatibleReason => Paint(StatusColors.Disrupted),
                ExpectedDaemonRestart.Reason => Paint(StatusColors.Pending),
                _ => Paint(failed ? StatusColors.Disrupted : StatusColors.InProgress),
            };

        return daemonConnection switch {
            "connected" => Paint(StatusColors.Connected),
            "connecting" or "reconnecting" => Paint(StatusColors.Pending),
            "disconnected" => Paint(StatusColors.InProgress),
            _ => Paint(StatusColors.Unavailable),
        };
    }

    async Task InvokeStartAsync(Func<CancellationToken, Task> start, CancellationToken ct) {
        StartMessage = StartingMessage;
        await start(ct);
    }

    async Task InvokeRetryAsync() {
        StartMessage = ReconnectingMessage;
        await _service.RestartLoopAsync();
    }

    async Task RunStartAsync(CancellationToken ct) {
        try {
            var result = await _service.StartDaemonAsync(ct);
            if (!result.Ok) StartMessage = result.Message;
            else StartMessage = "Starting the daemon. Waiting for it to connect…";
        } catch (OperationCanceledException) {
            // App is quitting: OnShutdownRequested cancelled `ct` while this start was still in
            // flight, and StartDaemonAsync deliberately rethrows OCE for exactly that case —
            // ct abandons the WAIT, not the started daemon. Nothing subscribes to
            // StartDaemonCommand.ThrownExceptions, so letting this escape would have ReactiveUI's
            // default handler reschedule an UnhandledErrorException onto the still-alive
            // dispatcher. The app is exiting — there is nothing left to render.
        }
    }
}
