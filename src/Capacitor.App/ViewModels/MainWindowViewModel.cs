using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Commands;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Which surface owns the window: Home (status block + launcher + cards + Activity) or
/// Sessions (rail | workspace). Orthogonal to CurrentWorkspace, which only means anything in
/// Sessions view.
public enum ShellView { Home, Sessions }

/// Projects IDaemonClientService.Status/Snapshots into display text and drives Start/Reconnect.
/// Display projections are activation-scoped (WhenActivated). StartDaemonCommand/RetryCommand and
/// their canExecute pipelines are built in the constructor so they exist pre-activation.
/// StartVisible/RetryVisible track the same predicates (one primary action: Start when down;
/// Reconnect when connecting or skewed). The service outlives this VM and owns its subjects.
public sealed class MainWindowViewModel : ReactiveObject, IActivatableViewModel {
    const string IncompatibleReason = "daemon_incompatible";
    const string UnreachableReason  = "daemon_unreachable";

    // Neutral wording: incompatibility classification is a broad heuristic — an unexpected frame
    // can equally mean the APP is the older side — so the UI must not prescribe an upgrade direction.
    // User-facing copy lives on HomeViewModel (launcher banner); Reason mirrors it for tests/tray.

    /// User-facing copy when the daemon isn't attached. Never the wire token (daemon_unreachable).
    internal static string UnreachableMessage => HomeViewModel.DaemonDownNotice;

    /// Shown the moment Start daemon is pressed, before the lifecycle/CLI work returns, so a
    /// click is never silent even when the start action itself has nothing further to say.
    internal const string StartingMessage = "Starting the daemon…";

    /// Shown the moment Reconnect is pressed. Cleared on Connected; replaced if attach stays unreachable.
    internal const string ReconnectingMessage = "Reconnecting…";

    internal const string ReconnectFailedMessage =
        "Could not reconnect. If the daemon isn't running, press Start daemon.";

    internal static bool IsInFlightReconnectMessage(string? message) =>
        message == ReconnectingMessage
        || message == DaemonLifecycleController.AlreadyRunningReconnectStatus;

    // StatusColors (shared with TrayIconRenderer's tray-icon overlay) is hex-only
    // constants (plain strings, not Brush instances). A Brush is an AvaloniaObject with UI-thread
    // affinity enforced the moment the renderer references it; caching one as a shared
    // `static readonly` field would tie its affinity to whichever thread happens to trigger this
    // type's static initializer FIRST (e.g. a plain unit test calling a static helper off the UI
    // thread) and then poison every later render that reuses the same cached instance. Paint
    // below constructs a fresh instance per call instead — cheap, and always on whatever thread
    // the caller is on.
    static IBrush Paint(string hex) => new SolidColorBrush(Color.Parse(hex));

    readonly IDaemonClientService _service;

    public ViewModelActivator Activator { get; } = new();

    ObservableAsPropertyHelper<string>? _daemonName;
    public string DaemonName => _daemonName?.Value ?? "";

    ObservableAsPropertyHelper<string>? _daemonVersion;
    public string DaemonVersion => _daemonVersion?.Value ?? "";

    // Compact rail label: the daemon semver alone — build metadata stays off the line.
    ObservableAsPropertyHelper<string>? _versionDisplay;
    public string VersionDisplay => _versionDisplay?.Value ?? "";

    ObservableAsPropertyHelper<string>? _serverUrl;
    public string ServerUrl => _serverUrl?.Value ?? "";

    // The daemon's OWN upstream connection to the Capacitor server (DaemonInfoDto.Connection):
    // connected|connecting|reconnecting|disconnected. Distinct from State/Reason below, which
    // are this app's local attach status to the daemon.
    ObservableAsPropertyHelper<string>? _connectionText;
    public string ConnectionText => _connectionText?.Value ?? "";

    // Single-word presentation of the OVERALL connection situation (local attach State first,
    // falling back to the daemon's own upstream Connection only once State is Connected — see
    // ConnectionDisplayFor). Capitalized, in-progress words get a trailing ellipsis ("Connecting…").
    ObservableAsPropertyHelper<string>? _connectionDisplay;
    public string ConnectionDisplay => _connectionDisplay?.Value ?? "";

    // The color ConnectionDisplay is painted in — the word itself carries the status, so this
    // and the text come from parallel switches over one bucketing.
    ObservableAsPropertyHelper<IBrush>? _statusBrush;
    public IBrush StatusBrush => _statusBrush?.Value ?? Paint(StatusColors.Unavailable);

    // "n of m agents" only while Connected — active_agents is a display count, never capacity.
    // Empty otherwise, which is what hides it: the service still retains the last snapshot
    // across disconnects, so a count would go on reading as live.
    ObservableAsPropertyHelper<string>? _agentCountText;
    public string AgentCountText => _agentCountText?.Value ?? "";

    internal const string RestartPendingMessage = "Daemon update pending — it restarts once no agents are running.";

    // The daemon has a restart-after-update queued (DaemonRestartPendingWatcher) and we are
    // attached to it: a passive marker only, the restart is the daemon's own.
    ObservableAsPropertyHelper<bool>? _restartPending;
    public bool RestartPending => _restartPending?.Value ?? false;

    ObservableAsPropertyHelper<string?>? _restartPendingText;
    public string? RestartPendingText => _restartPendingText?.Value;

    ObservableAsPropertyHelper<AttachState>? _state;
    public AttachState State => _state?.Value ?? AttachState.Connecting;

    // Display text for why we're not connected: friendly copy only — never a raw wire token
    // like daemon_unreachable. Null outside Unreachable.
    ObservableAsPropertyHelper<string?>? _reason;
    public string? Reason => _reason?.Value;

    // The server lane's silent-deafness diagnostic — informational only, never blocking; null
    // while the lane is healthy or absent. Two-line hovers put extra info on the lighter line
    // and the fragment's name (AttachStatusTip, ServerUrlTip, …) on the darker caption; a
    // fragment with nothing extra keeps the name as a one-line tip.
    ObservableAsPropertyHelper<string?>? _serverLaneTip;
    public string? ServerLaneTip => _serverLaneTip?.Value;

    ObservableAsPropertyHelper<bool>? _connectionHasDetail;
    public bool ConnectionHasDetail => _connectionHasDetail?.Value ?? false;

    ObservableAsPropertyHelper<string>? _connectionTip;
    public string ConnectionTip => _connectionTip?.Value ?? AttachStatusTip;

    public const string AttachStatusTip = "Attach status to the daemon on this machine";
    public const string DaemonNameTip = "Name of the daemon on this machine";

    /// Caption under the URL in the org label's hover — the org is what you point at, the
    /// server it talks to is what the hover tells you.
    public const string ServerUrlTip = "Capacitor server for this organization";
    public const string VersionIdentityTip = "Version of the kcap daemon on this machine";
    public const string AgentCountTip = "Agents running on this daemon";

    readonly BehaviorSubject<string?> _startMessageChanges = new(null);

    /// Constructed once at the composition root; the prompt window's onConcluded callback nudges
    /// the same instance.
    public ActivityViewModel Activity { get; }

    /// The Home surface's launcher and cards — constructed at the composition root over the SAME
    /// IDaemonClientService instance this window uses, never a second daemon connection. Null
    /// only for a caller that doesn't supply one (most existing tests predate Home); HomeView
    /// tolerates a null DataContext, same as any other unbound view.
    public HomeViewModel? Home { get; }

    readonly NavigationGate _navigation;
    readonly Action<Func<Task>> _trackTeardown;
    readonly Func<string, WorkspaceViewModel>? _workspaceFactory;
    readonly Func<string, AgentOrigin?> _originOf;
    readonly Func<string, RemoteSessionViewModel?>? _remoteFactory;
    readonly IAgentDirectory? _directory;
    readonly SerialDisposable _rebind = new();

    ISessionWorkspace? _currentWorkspace;
    /// null = the Sessions surface shows its placeholder pane; non-null = that session's workspace,
    /// local or remote. Exactly one at a time, and this VM owns it: every swap starts the outgoing
    /// one's tracked teardown.
    public ISessionWorkspace? CurrentWorkspace {
        get => _currentWorkspace;
        private set => this.RaiseAndSetIfChanged(ref _currentWorkspace, value);
    }

    // Sessions is the app's home for now: the right pane's empty state IS the launcher, and the
    // Home surface stays in the tree but hidden (nothing navigates to it) until it earns its keep.
    ShellView _currentView = ShellView.Sessions;
    public ShellView CurrentView {
        get => _currentView;
        private set {
            this.RaiseAndSetIfChanged(ref _currentView, value);
            this.RaisePropertyChanged(nameof(IsHomeView));
            this.RaisePropertyChanged(nameof(IsSessionsView));
        }
    }
    public bool IsHomeView => CurrentView == ShellView.Home;
    public bool IsSessionsView => CurrentView == ShellView.Sessions;

    public ReactiveCommand<Unit, Unit> ShowHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSessionsCommand { get; }

    /// The Sessions rail (repo → worktree → session over daemon.Agents) — null for any caller
    /// that predates it, same nullable-seam shape as Home/workspaceFactory above.
    public SessionRailViewModel? Rail { get; }

    /// The active profile's name — the tenant slug (profiles are named after it at sign-in).
    /// "" when absent or when the name is the literal built-in "default" (hiding that segment
    /// so the rail footer does not read as a second "Default" next to connection state).
    public string TenantName { get; }

    /// The launch auto-open's staleness token — see NavigationGate. Read from the SHARED gate, not
    /// a per-window counter, so a window built after shutdown began sees the latch too.
    public int NavigationGeneration => _navigation.Generation;

    /// Clears the open workspace back to the Sessions surface's placeholder pane — the same command
    /// the coordinator's close paths route through.
    public ReactiveCommand<Unit, Unit> CloseWorkspaceCommand { get; }

    /// Opens the product documentation. Enabled whatever the server says — a user who cannot reach
    /// a tenant is exactly the one who needs the docs.
    public ReactiveCommand<Unit, Unit> OpenDocsCommand { get; }

    /// Opens the bug/feedback window for one category; inert without an action to route it to.
    public ReactiveCommand<FeedbackCategory, Unit> OpenFeedbackCommand { get; }

    /// Whether the two report items have somewhere to go — the same oracle the menu bar's items use.
    public bool CanOpenFeedback { get; }

    /// Opens the re-auth sign-in surface. Inert without an action to route it to.
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    ObservableAsPropertyHelper<bool>? _signInVisible;
    /// True while the footer reads Signed out and a sign-in action exists — the launcher's
    /// Sign in lives on the other pane and is hidden once a workspace is open.
    public bool SignInVisible => _signInVisible?.Value ?? false;

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

    // Visibility tracks "action is meaningful", not CanExecute — CanExecute also ANDs "not
    // executing", which would hide the button mid-attempt instead of only disabling it.
    readonly ObservableAsPropertyHelper<bool> _startVisible;
    public bool StartVisible => _startVisible.Value;

    readonly ObservableAsPropertyHelper<bool> _retryVisible;
    public bool RetryVisible => _retryVisible.Value;

    readonly TimeProvider _time;

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
    /// a caller with no navigation of its own (most existing tests) still gets a working VM — but
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
    /// — every existing caller that predates workspaces stays on the Home surface.
    /// </param>
    /// <param name="rail">
    /// The Sessions rail. Null means this window has no rail to keep in sync — every existing
    /// caller that predates it keeps working the way it always has.
    /// </param>
    /// <param name="laneStatus">
    /// The app's own server lane (IServerLane.Status), for the footer's ServerLaneTip diagnostic
    /// and the connection row's hover. Null means the hover stays on AttachStatusTip.
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
    /// Opens the re-auth sign-in surface (App owns the window). Null hides the rail Sign in —
    /// a window with no dialog to open.
    /// </param>
    public MainWindowViewModel(
            IDaemonClientService service,
            CancellationToken shutdownToken, ActivityViewModel activity, TimeProvider time,
            Func<CancellationToken, Task>? startAction = null,
            IObservable<string?>? lifecycleStatus = null, HomeViewModel? home = null,
            NavigationGate? navigation = null, Action<Func<Task>>? trackWorkspaceTeardown = null,
            Func<string, WorkspaceViewModel>? workspaceFactory = null, SessionRailViewModel? rail = null,
            string? tenantName = null, IObservable<string?>? lifecycleAttention = null,
            IObservable<ServerLaneStatus>? laneStatus = null, IObservable<bool>? restartPending = null,
            Func<string, AgentOrigin?>? originOf = null, Func<string, RemoteSessionViewModel?>? remoteWorkspaceFactory = null,
            IAgentDirectory? directory = null,
            Action<FeedbackCategory>? openFeedback = null, IUrlOpener? opener = null,
            Action? requestSignIn = null) {
        _service = service;
        _time = time;
        Activity = activity;
        Home = home;
        _navigation = navigation ?? new NavigationGate();
        _trackTeardown = trackWorkspaceTeardown ?? RunUntracked;
        _workspaceFactory = workspaceFactory;
        _originOf = originOf ?? (_ => AgentOrigin.Local);
        _remoteFactory = remoteWorkspaceFactory;
        _directory = directory;
        Rail = rail;
        TenantName = ProfileLabelForRail(tenantName);
        CloseWorkspaceCommand = ReactiveCommand.Create(CloseWorkspace);
        ShowHomeCommand = ReactiveCommand.Create(() => { CurrentView = ShellView.Home; });
        ShowSessionsCommand = ReactiveCommand.Create(() => { CurrentView = ShellView.Sessions; });
        CanOpenFeedback     = openFeedback is not null;
        OpenFeedbackCommand = ReactiveCommand.Create<FeedbackCategory>(c => openFeedback?.Invoke(c), Observable.Return(CanOpenFeedback));
        OpenDocsCommand     = ReactiveCommand.Create(() => LinkPolicy.Open(opener ?? new ShellUrlOpener(), AppMenuBar.DocsUrl));
        SignInCommand       = ReactiveCommand.Create(() => { requestSignIn?.Invoke(); });
        var offersSignIn    = requestSignIn is not null;

        // ReactiveCommand's own CanExecute observable already ANDs the supplied canExecute with
        // "not currently executing" (confirmed against the installed ReactiveUI 23.2.28 API
        // docs) — no separate in-flight flag is needed to satisfy "Start also disabled while a
        // start is in flight".
        //
        // ReactiveCommand does NOT reschedule the SUPPLIED canExecute onto outputScheduler
        // (decompile-verified: only IsExecuting/ThrownExceptions ride outputScheduler) — without
        // an explicit ObserveOn here, a Status event arriving on a background thread (the
        // service's pump thread) would carry CanExecuteChanged, and therefore a bound Button's
        // IsEnabled write, onto that same background thread, tripping Avalonia's dispatcher
        // thread-affinity check. These stay constructor-scoped (not inside WhenActivated) since
        // commands must exist and be assertable pre-activation — see the class doc comment.
        // One primary action at a time: Start when nothing is listening (spawn/reattach via the
        // lifecycle); Reconnect when Start is not the right next step (skew, or still connecting).
        var canStart = service.Status
            .Select(s => s.State == AttachState.Unreachable && s.Reason == UnreachableReason)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        var canRetry = service.Status
            .Select(s =>
                s.State == AttachState.Connecting
                || (s.State == AttachState.Unreachable && s.Reason != UnreachableReason))
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var start = startAction ?? RunStartAsync;
        StartDaemonCommand = ReactiveCommand.CreateFromTask(
            () => InvokeStartAsync(start, shutdownToken), canStart);
        RetryCommand = ReactiveCommand.CreateFromTask(InvokeRetryAsync, canRetry);

        // Independent subscriptions to the SAME canStart/canRetry state predicates the commands
        // above were built from (service.Status is hot/multicast, so a second subscriber replays
        // the current value same as the first) — visibility that never disagrees with why a button
        // is enabled, without inheriting CanExecute's "not currently executing" hide-while-running
        // behavior. Ctor-scoped for the same reason as the commands themselves.
        _startVisible = canStart.ToProperty(this, x => x.StartVisible, initialValue: false);
        _retryVisible = canRetry.ToProperty(this, x => x.RetryVisible, initialValue: false);

        // Launcher banner owns the chrome; share the same Start/Reconnect commands and start-message
        // lane so the pane never drifts from what MainWindow already drives.
        home?.AttachDaemonRecovery(
            StartDaemonCommand, RetryCommand, canStart, canRetry, _startMessageChanges);

        this.WhenActivated(disposables => {
            var status    = service.Status.ObserveOn(RxSchedulers.MainThreadScheduler);
            var snapshots = service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler);

            _daemonName = snapshots.Select(s => s.Daemon.Name)
                .ToProperty(this, x => x.DaemonName, "")
                .DisposeWith(disposables);

            _daemonVersion = snapshots.Select(s => s.Daemon.Version)
                .ToProperty(this, x => x.DaemonVersion, "")
                .DisposeWith(disposables);

            _versionDisplay = snapshots.Select(s => StripBuildMetadata(s.Daemon.Version))
                .ToProperty(this, x => x.VersionDisplay, "")
                .DisposeWith(disposables);

            _serverUrl = snapshots.Select(s => s.Daemon.ServerUrl)
                .ToProperty(this, x => x.ServerUrl, "")
                .DisposeWith(disposables);

            _connectionText = snapshots.Select(s => s.Daemon.Connection)
                .ToProperty(this, x => x.ConnectionText, "")
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

            _signInVisible = signInExpired
                .Select(expired => expired && offersSignIn)
                .ToProperty(this, x => x.SignInVisible, false)
                .DisposeWith(disposables);

            _connectionDisplay = status.CombineLatest(daemonConnection, signInExpired, ConnectionDisplayFor)
                .ToProperty(this, x => x.ConnectionDisplay, "")
                .DisposeWith(disposables);

            _statusBrush = status.CombineLatest(daemonConnection, signInExpired, StatusBrushFor)
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

            _state = status.Select(s => s.State)
                .ToProperty(this, x => x.State, AttachState.Connecting)
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

            _connectionTip = lane
                .Select(s => string.IsNullOrWhiteSpace(s.Diagnostic) ? AttachStatusTip : s.Diagnostic)
                .ToProperty(this, x => x.ConnectionTip, AttachStatusTip)
                .DisposeWith(disposables);

            status.Where(s => s.State == AttachState.Connected)
                .Subscribe(_ => StartMessage = null)
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
            void BindStartMessage(IObservable<string?>? source) =>
                source?.ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Where(msg => msg is not null)
                    .Subscribe(msg => StartMessage = msg)
                    .DisposeWith(disposables);

            BindStartMessage(lifecycleStatus);
            BindStartMessage(lifecycleAttention);
        });
    }

    /// Card and rail click: swaps to this session's workspace — the local one, or the remote card
    /// host when the id belongs to another machine. A caller that knows which row was clicked says
    /// so; without an origin the id is resolved, and a same-id pair on both lanes resolves local.
    /// Refused once shutdown has latched — a new workspace is a new attach, and quiesce is already
    /// running.
    public void OpenSession(string agentId, AgentOrigin? origin = null) {
        if (_navigation.ShutdownLatched) return;
        CurrentView = ShellView.Sessions;

        // Neither lane holds the id: opening the local workspace for it would attach a terminal to
        // an agent this machine never ran.
        if ((origin ?? _originOf(agentId)) is not { } lane) return;

        // Re-clicking the open session must not tear down and rebuild a live attach. The other
        // lane's same-id agent is a different agent, and a remote host the local daemon has taken
        // over no longer owns the id at all: both are a real swap.
        if (CurrentWorkspace is { } open && open.AgentId == agentId && !Superseded(open, lane)) return;

        ISessionWorkspace? next = lane == AgentOrigin.Remote && _remoteFactory is { } remote
            ? remote(agentId)
            : _workspaceFactory is { } local ? local(agentId) : null;
        if (next is null) return;

        SwapTo(next);
        Rail?.NotifySessionOpened(agentId);
    }

    static bool Superseded(ISessionWorkspace open, AgentOrigin lane) =>
        open is RemoteSessionViewModel remote
            ? lane != AgentOrigin.Remote || remote.OriginChangedToLocal
            : lane != AgentOrigin.Local;

    /// The launch auto-open. `generation` is what the launch captured BEFORE its call: a success
    /// arriving after any navigation (closing the workspace, another session, close-to-hide, the
    /// shutdown latch) opens nothing, rather than attaching an invisible terminal or replacing what
    /// the user opened while the launch was in flight.
    public void OpenSessionIfCurrent(string agentId, int generation) {
        if (generation != _navigation.Generation) return;
        OpenSession(agentId);
    }

    /// The coordinator's close paths. Bumps unconditionally — a close-to-hide with no workspace
    /// open must still retire an in-flight launch's captured generation.
    public void CloseWorkspace() => SwapTo(null);

    /// First shutdown pass: unhook the live workspace and register its teardown before the drain
    /// seals the tracker, then latch so no later window opens another. A workspace that never
    /// closed would otherwise register teardown after drain against already-disposed deps.
    public void LatchShutdown() {
        var live = CurrentWorkspace;
        CurrentWorkspace = null;
        _rebind.Disposable = Disposable.Empty;
        if (Rail is not null) Rail.SelectedAgentId = null;
        _navigation.Latch();
        if (live is not null) _trackTeardown(live.TeardownAsync);
    }

    void SwapTo(ISessionWorkspace? next) {
        var outgoing = CurrentWorkspace;
        CurrentWorkspace = next;
        if (Rail is not null) Rail.SelectedAgentId = next?.AgentId;
        _navigation.Bump();
        if (outgoing is not null) _trackTeardown(outgoing.TeardownAsync);
        // Last, so a watch that fires synchronously on its first element cannot have the swap it
        // caused overwritten by the arming that is still returning.
        WatchOrigin(next);
    }

    /// An open workspace follows its row across lanes, both directions, carrying the tab in use.
    /// Nothing here ends a session — the row that wins says whether it did.
    void WatchOrigin(ISessionWorkspace? workspace) {
        var watch = workspace switch {
            RemoteSessionViewModel remote => remote.OriginChangedChanges
                .Where(moved => moved)
                .Take(1)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => Rebind(remote, AgentOrigin.Local, remote.IsTerminalActive)),
            // Not a one-shot: a swap the host cannot complete leaves the watch armed for the next
            // proof, and one that completes retires it through the swap's own re-arming.
            WorkspaceViewModel local when _directory is { } directory => LocalRowMoves(directory, local.AgentId)
                .Subscribe(_ => Rebind(local, AgentOrigin.Remote, local.IsTerminalActive)),
            _ => Disposable.Empty,
        };
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

    void Rebind(ISessionWorkspace open, AgentOrigin origin, bool terminal) {
        if (!ReferenceEquals(CurrentWorkspace, open)) return;
        // The row moved machines on its own; only a click of the user's own navigates, so the
        // surface they are reading survives the swap underneath it.
        var view = CurrentView;
        OpenSession(open.AgentId, origin);
        CurrentView = view;
        if (!terminal || ReferenceEquals(CurrentWorkspace, open)) return;
        switch (CurrentWorkspace) {
            case WorkspaceViewModel local: local.ShowTerminalCommand.Execute().Subscribe(); break;
            case RemoteSessionViewModel remote: remote.ShowTerminalCommand.Execute().Subscribe(); break;
        }
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
            return status.Reason == IncompatibleReason ? "Incompatible" : "Unreachable";

        var word = Capitalize(daemonConnection);
        return daemonConnection is "connecting" or "reconnecting" ? word + "…" : word;
    }

    // Same bucketing as ConnectionDisplayFor, kept as a parallel switch (not derived from the text)
    // so a future wording tweak there can never silently detune the color.
    internal static IBrush StatusBrushFor(
            AttachStatus status, string daemonConnection, bool signInExpired = false) {
        if (signInExpired) return Paint(StatusColors.Disrupted);
        if (status.State == AttachState.Connecting) return Paint(StatusColors.InProgress);
        if (status.State == AttachState.Unreachable)
            return status.Reason == IncompatibleReason ? Paint(StatusColors.Disrupted) : Paint(StatusColors.Unavailable);

        return daemonConnection switch {
            "connected" => Paint(StatusColors.Connected),
            "connecting" or "reconnecting" => Paint(StatusColors.InProgress),
            "disconnected" => Paint(StatusColors.Disrupted),
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
            else StartMessage = "Daemon start requested. Waiting to connect…";
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
