using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteSessionAccess { Connecting, Ready, Denied, Offline, NoSession }
public enum RemoteTab { Chat, Terminal, PullRequest, Artefacts }

/// The workspace for a row the app has no socket to: the header, the chat pane over the server's
/// stream — its cards included — Stop and Open in web. Access is the server's explicit signal,
/// Ready only after the watch and the chat join both succeeded, so an empty pane is "no rows
/// yet", never "not allowed". An ended session keeps its transcript while the lease's last
/// verdict stands.
public sealed class RemoteSessionViewModel : ReactiveObject, ISessionWorkspace {
    internal const string OriginChangedNote = "This agent is now hosted locally — open it from the rail";
    internal const string MissingNote = "The transcript is not available";

    readonly SessionAccessService _access;
    readonly CompositeDisposable _disposables = new();
    readonly ComposerDrafts? _drafts;
    readonly SerialDisposable _lease = new();
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly IPermissionService _permissions;
    readonly SubagentEventsReader? _readSubagent;
    readonly SessionRuns _runs;
    // Never disposed, so a row revision landing after teardown cannot throw; the chat and its
    // cards unsubscribe from these when the pane is torn down.
    readonly BehaviorSubject<string?> _sessionIds;
    readonly BehaviorSubject<ChatSessionInfo> _session;
    readonly BehaviorSubject<AgentStatusDto?> _presence = new(null);
    /// The current lease's verdict, re-pointed whenever the lease moves; Unavailable in between.
    readonly BehaviorSubject<SessionAccessState> _accessStates = new(SessionAccessState.Unavailable);
    bool _workAccessDenied;
    string? _leasedSession;
    AgentRow _row;

    public string AgentId { get; }
    public ChatTabViewModel Chat { get; }
    public WorkContextViewModel WorkContext { get; }
    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }

    /// Null for a harness with no PTY; the server registry carries no terminal flag, so the
    /// vendor's family decides, exactly as it does for a local dto without one.
    public RemoteTerminalViewModel? Terminal { get; }
    public bool ShowsTerminalTab => Terminal is not null;
    public PullRequestContextViewModel? PullRequests { get; }
    public ReactiveCommand<Unit, Unit> ShowPullRequestCommand { get; }
    bool _showsPullRequestTab;
    public bool ShowsPullRequestTab {
        get => _showsPullRequestTab || IsPullRequestActive;
        private set {
            this.RaiseAndSetIfChanged(ref _showsPullRequestTab, value);
            this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch));
        }
    }
    public void OpenPullRequest(PullRequestSubjectDto? subject = null) {
        if (PullRequests is null) return;
        if (subject is not null) PullRequests.SelectSubject(subject);
        ActiveTab = RemoteTab.PullRequest;
        if (!PullRequests.IsReading) PullRequests.Refresh();
    }

    public ArtefactsTabViewModel Artefacts { get; }
    public ReactiveCommand<Unit, Unit> ShowArtefactsCommand { get; }
    bool _showsArtefactsTab;
    public bool ShowsArtefactsTab {
        get => _showsArtefactsTab;
        private set {
            this.RaiseAndSetIfChanged(ref _showsArtefactsTab, value);
            this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch));
        }
    }
    public bool ShowsSurfaceSwitch => ShowsTerminalTab || ShowsPullRequestTab || ShowsArtefactsTab;

    void ShowArtefacts() { if (ShowsArtefactsTab) ActiveTab = RemoteTab.Artefacts; }

    /// A chat card's Open: a declared document selects in the tab, a page goes to the browser.
    internal void OpenCard(ToolCard card) {
        if (card.DocumentPath is { } path) {
            if (Artefacts.OpenDocument(path)) return;
            if (Artefacts.HasAny) { Artefacts.RequestOpen(); return; }
        }
        if (card.Url is { } url) LinkPolicy.Open(_opener, url);
    }

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    string _repoLabelText = "";
    public string RepoLabelText { get => _repoLabelText; private set => this.RaiseAndSetIfChanged(ref _repoLabelText, value); }

    RemoteTab _activeTab = RemoteTab.Chat;
    public RemoteTab ActiveTab {
        get => _activeTab;
        private set {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            RaiseTabProjections();
            Artefacts.IsShown = IsArtefactsActive;
            PullRequests?.SetReaderVisible(IsPullRequestActive);
        }
    }
    public bool IsChatActive => ActiveTab == RemoteTab.Chat;
    public bool IsTerminalActive => ActiveTab == RemoteTab.Terminal;
    public bool IsPullRequestActive => ActiveTab == RemoteTab.PullRequest;
    public bool IsArtefactsActive => ActiveTab == RemoteTab.Artefacts;

    // Stop's canExecute reads the ended flag here, not through this.WhenAnyValue: that call routes
    // through ReactiveUI's ObservableForProperty/RxAppBuilder global init, which only some other
    // code having built the app primes. Never disposed, so a set after teardown cannot throw.
    readonly BehaviorSubject<bool> _sessionEndedChanges = new(false);

    bool _sessionEnded;
    public bool SessionEnded {
        get => _sessionEnded;
        private set {
            if (_sessionEnded == value) return;
            this.RaiseAndSetIfChanged(ref _sessionEnded, value);
            _sessionEndedChanges.OnNext(value);
        }
    }

    // Same reason as _sessionEndedChanges above: Stop's canExecute reads the flag through a subject.
    readonly BehaviorSubject<bool> _originChangedChanges = new(false);
    public IObservable<bool> OriginChangedChanges => _originChangedChanges.AsObservable();

    bool _originChangedToLocal;
    /// The local daemon has proven this row is its twin: the remote row is gone but the agent is
    /// not, so the window replaces this host with the local workspace for the same id.
    public bool OriginChangedToLocal {
        get => _originChangedToLocal;
        private set {
            if (_originChangedToLocal == value) return;
            this.RaiseAndSetIfChanged(ref _originChangedToLocal, value);
            this.RaisePropertyChanged(nameof(AccessNote));
            RaiseTabProjections();
            _originChangedChanges.OnNext(value);
        }
    }

    RemoteSessionAccess _accessState = RemoteSessionAccess.NoSession;
    public RemoteSessionAccess Access {
        get => _accessState;
        private set {
            this.RaiseAndSetIfChanged(ref _accessState, value);
            this.RaisePropertyChanged(nameof(AccessNote));
            RaiseTabProjections();
        }
    }

    /// The tabs' content lives while the lease's last verdict stands; an ended session keeps its
    /// transcript, and so does a dropped lane — the access banner has its own row above the panes,
    /// so it reads over the retained rows. A refusal is different: Denied, a session that has not
    /// started and an agent that moved to this machine show the note instead of a pane.
    public bool ShowsPanes =>
        Access is RemoteSessionAccess.Ready or RemoteSessionAccess.Offline && !OriginChangedToLocal;

    public bool ShowsChatPane => ShowsPanes && IsChatActive && Subagent is null;
    public bool ShowsSubagentPane => ShowsPanes && IsChatActive && Subagent is not null;

    SubagentPaneViewModel? _subagent;
    /// A subagent's own transcript, shown in place of the chat until the user goes back.
    public SubagentPaneViewModel? Subagent {
        get => _subagent;
        private set {
            this.RaiseAndSetIfChanged(ref _subagent, value);
            this.RaisePropertyChanged(nameof(ShowsChatPane));
            this.RaisePropertyChanged(nameof(ShowsSubagentPane));
        }
    }
    public bool ShowsTerminalPane => ShowsPanes && IsTerminalActive;
    public bool ShowsPullRequestPane => ShowsPanes && IsPullRequestActive;
    public bool ShowsArtefactsPane => ShowsPanes && IsArtefactsActive;

    // What the terminal reports its viewport on. Same reason as _sessionEndedChanges above for a
    // subject rather than this.WhenAnyValue; never disposed, so a projection raised after teardown
    // cannot throw.
    readonly BehaviorSubject<bool> _terminalPaneShown = new(false);

    void RaiseTabProjections() {
        this.RaisePropertyChanged(nameof(IsChatActive));
        this.RaisePropertyChanged(nameof(IsTerminalActive));
        this.RaisePropertyChanged(nameof(IsArtefactsActive));
        this.RaisePropertyChanged(nameof(IsPullRequestActive));
        this.RaisePropertyChanged(nameof(ShowsPullRequestTab));
        this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch));
        this.RaisePropertyChanged(nameof(ShowsPullRequestPane));
        this.RaisePropertyChanged(nameof(ShowsArtefactsPane));
        this.RaisePropertyChanged(nameof(ShowsPanes));
        this.RaisePropertyChanged(nameof(ShowsChatPane));
        this.RaisePropertyChanged(nameof(ShowsSubagentPane));
        this.RaisePropertyChanged(nameof(ShowsTerminalPane));
        _terminalPaneShown.OnNext(ShowsTerminalPane);
    }

    public string AccessNote => OriginChangedToLocal ? OriginChangedNote : Access switch {
        RemoteSessionAccess.Connecting => "Connecting to the session…",
        RemoteSessionAccess.Denied => "You no longer have access to this session",
        RemoteSessionAccess.Offline => "Not connected to the server",
        RemoteSessionAccess.NoSession => "Waiting for the session to start",
        _ => "",
    };

    public RemoteSessionViewModel(
            AgentRow row, IAgentDirectory directory, SessionAccessService access, IPermissionService permissions,
            AgentActionService actions, IServerLane lane, SessionDetailReader readDetail, IUrlOpener opener, TimeProvider time, IWorkContextSource workContext,
            Func<ITerminalSurface>? surfaceFactory = null, IPlanArtifactSource? planArtifacts = null,
            BackgroundCommandActivity? commands = null, IPullRequestSource? pullRequests = null,
            Action? requestSignIn = null, Action? linkGitHub = null, IObservable<Unit>? signInCompleted = null, IPlanSource? plans = null,
            ComposerDrafts? drafts = null, Func<string, Action>? inputSending = null, Action<string>? pullRequestLinksChanged = null,
            SubagentEventsReader? readSubagent = null) {
        _drafts = drafts;
        _time = time;
        _permissions = permissions;
        _readSubagent = readSubagent;
        _row = row;
        _opener = opener;
        _access = access;
        AgentId = row.Id;
        _sessionIds = new BehaviorSubject<string?>(row.SessionId);
        _session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(row, ended: false));

        var planActivity = new PlanActivity();
        Artefacts = new ArtefactsTabViewModel(planArtifacts, planActivity, time, opener: opener);
        Artefacts.OpenRequested += ShowArtefacts;
        Artefacts.WhenAnyValue(a => a.HasAny, a => a.IsLoading, (has, loading) => has || (loading && ShowsArtefactsTab))
            .Subscribe(show => {
                ShowsArtefactsTab = show;
                if (!show && IsArtefactsActive) ActiveTab = RemoteTab.Chat;
            }).DisposeWith(_disposables);
        var input = new ServerChatInput(row.Id, lane, _accessStates, _session, HostedHarnessCatalog.ShowsTerminal(null, row.Vendor));
        var runs = _runs = new SessionRuns(time);
        commands?.Track($"remote:{row.Id}", runs).DisposeWith(_disposables);
        WorkContext = new WorkContextViewModel(_presence, workContext, time, opener, runs,
            requestSignIn, signInCompleted, actions.OpenWorkItemInWeb, plans, planActivity,
            artefacts: Artefacts, isRemote: true);
        WorkContext.RunOpened += OpenSubagent;
        _disposables.Add(Disposable.Create(() => WorkContext.RunOpened -= OpenSubagent));
        // An open subagent belongs to the session it was opened from.
        _sessionIds.DistinctUntilChanged().Skip(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => CloseSubagent()).DisposeWith(_disposables);
        PullRequests = pullRequests is null ? null : new PullRequestContextViewModel(
            _presence.Select(dto => dto is null ? null : dto with { RepoPath = null, WorktreePath = null, BorrowedFrom = null }),
            pullRequests, time, opener, () => ActiveTab = RemoteTab.PullRequest,
            requestSignIn, linkGitHub, signInCompleted, () => WorkContext.PrimaryRepository,
            linksChanged: pullRequestLinksChanged);
        if (PullRequests is { } context)
            context.HasPullRequestChanges.CombineLatest(context.WhenAnyValueUnsafe(pr => pr.HasListed), (has, listed) => (has, listed))
                .Subscribe(state => {
                    ShowsPullRequestTab = state.has;
                    if (!state.has && IsPullRequestActive && state.listed) ActiveTab = RemoteTab.Chat;
                }).DisposeWith(_disposables);
        lane.Status.Select(s => s.State).DistinctUntilChanged().Skip(1)
            .Where(state => state == ServerLaneState.Connected).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => { PullRequests?.Reconnected(); WorkContext.Reconnected(); }).DisposeWith(_disposables);

        WorkContext.PullRequests = PullRequests;
        _accessStates.Where(s => s == SessionAccessState.Established)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => Artefacts.Refresh()).DisposeWith(_disposables);
        Chat = new ChatTabViewModel(
            row.Id, AgentOrigin.Remote, _session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, row.Vendor, _accessStates, readDetail, lane, time, Log),
            opener, time, permissions, runs, missingNote: MissingNote, sessionId: _sessionIds,
            serverQueue: Observable.Switch(_sessionIds
                .Select(sid => sid is null
                    ? Observable.Empty<IReadOnlyList<QueuedInputItem>>()
                    : lane.PendingInputChanged.Where(u => u.SessionId == sid).Select(u => u.Items))),
            planActivity: planActivity, openCard: OpenCard, drafts: drafts) { InputSending = inputSending };
        Terminal = surfaceFactory is not null && HostedHarnessCatalog.ShowsTerminal(null, row.Vendor)
            ? new RemoteTerminalViewModel(row.Id, lane, _accessStates, _sessionEndedChanges, _terminalPaneShown, surfaceFactory)
            : null;
        Apply(row);

        directory.Rows.Connect()
            .Filter(r => r.Key == row.Key)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(changes => {
                foreach (var change in changes) {
                    if (change.Reason == ChangeReason.Remove) {
                        // The local daemon took the agent over: the row ended, the session did not.
                        // A shared agent id is not evidence of that on its own — the dedup fails
                        // open, so two unrelated agents can carry one id. The proof is the
                        // directory's: this agent runs on the daemon it proved is the local one's
                        // twin, and both rows carry the same session id. The directory publishes
                        // the local add and this removal in one edit, so that row is already in the
                        // cache here; a later one would read as an ended session.
                        // Never once a terminal status has ended this session: that verdict came
                        // from the authority that was winning, and the local row reappearing behind
                        // the retired remote one is retained history rather than a takeover.
                        if (!SessionEnded
                            && directory.IsProvenLocalTwin(row.Id)
                            && directory.Rows.Lookup($"local:{row.Id}") is { HasValue: true, Value: var twin }
                            && twin.SessionId is { Length: > 0 } && twin.SessionId == _row.SessionId) {
                            OriginChangedToLocal = true;
                            // Nothing is answerable through a released lease, and the agent is not
                            // this host's to stop any more.
                            Access = RemoteSessionAccess.NoSession;
                        } else {
                            SessionEnded = true;
                            PublishSession(ended: true);
                        }
                        Release();
                        continue;
                    }
                    Apply(change.Current);
                }
            })
            .DisposeWith(_disposables);

        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWebRemote(row.Id));
        var stopKey = AgentActionService.StopKey(AgentOrigin.Remote, row.Id);
        var canStop = _sessionEndedChanges
            .CombineLatest(_originChangedChanges, actions.StopsInFlight,
                (ended, movedLocal, inFlight) => !ended && !movedLocal && !inFlight.Contains(stopKey));
        StopCommand = ReactiveCommand.Create(
            () => actions.RequestStop(row.Id, $"{_row.Vendor} · {_row.RepoGroupLabel}", _row.Kind, AgentOrigin.Remote),
            canStop);
        ShowPullRequestCommand = ReactiveCommand.Create(() => { if (ShowsPullRequestTab) ActiveTab = RemoteTab.PullRequest; });
        _disposables.Add(ShowPullRequestCommand);
        ShowChatCommand = ReactiveCommand.Create(() => { ActiveTab = RemoteTab.Chat; });
        ShowTerminalCommand = ReactiveCommand.Create(() => { if (ShowsTerminalTab) ActiveTab = RemoteTab.Terminal; });
        _disposables.Add(OpenInWebCommand);
        _disposables.Add(StopCommand);
        _disposables.Add(ShowChatCommand);
        _disposables.Add(ShowTerminalCommand);
        ShowArtefactsCommand = ReactiveCommand.Create(ShowArtefacts);
        _disposables.Add(ShowArtefactsCommand);
        _disposables.Add(_lease);
    }

    void Apply(AgentRow row) {
        _row = row;
        PublishWorkPresence();
        if (_sessionIds.Value != row.SessionId) _sessionIds.OnNext(row.SessionId);
        Title = row.Title ?? row.Vendor;
        RepoLabelText = $"{row.RepoGroupLabel} · on {row.MachineBadge}";
        if (SessionStatusDots.IsTerminal(row.Status)) {
            SessionEnded = true;
            PublishSession(ended: true);
            Release();
            return;
        }
        // The row can come back: a transient empty registry snapshot removes it and the refresh
        // that follows re-adds the same live session, which must not stay hidden behind the
        // removal's verdict. Release() cleared the leased id, so the lease is re-acquired below.
        SessionEnded = false;
        OriginChangedToLocal = false;

        var sessionId = row.SessionId;
        if (sessionId != _leasedSession) {
            Release();
            if (sessionId is null) Access = RemoteSessionAccess.NoSession;
            else AcquireLease(sessionId);
        }
        // After the lease moved: a new session id rebuilds the chat's feed, and one constructed
        // while the previous lease's Established still stood would seed and tail under a lease this
        // row no longer holds.
        PublishSession(ended: false);
    }

    void PublishWorkPresence() {
        var sessionId = _workAccessDenied ? null : _row.SessionId;
        _presence.OnNext(new AgentStatusDto(_row.Id, _row.Kind, _row.Vendor, _row.RepoPath, _row.Status,
            null, null, null, _row.CreatedAt, _row.Model, _row.RequesterDisplay,
            Title: _row.Title, SessionId: sessionId));
        if (string.IsNullOrEmpty(sessionId)) PullRequests?.ClearSession();
    }

    void AcquireLease(string sessionId) {
        _leasedSession = sessionId;
        // The lease's first state arrives asynchronously; without this the pane keeps the previous
        // session's verdict until it does.
        Access = RemoteSessionAccess.Connecting;
        var lease = _access.Acquire(sessionId);
        var states = lease.State.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => {
            _accessStates.OnNext(s);
            if (s == SessionAccessState.Denied) {
                _workAccessDenied = true;
                PublishWorkPresence();
            } else if (s == SessionAccessState.Established && _workAccessDenied) {
                _workAccessDenied = false;
                PublishWorkPresence();
            }
            Access = s switch {
                SessionAccessState.Established => RemoteSessionAccess.Ready,
                SessionAccessState.Denied => RemoteSessionAccess.Denied,
                SessionAccessState.Unavailable => RemoteSessionAccess.Offline,
                _ => RemoteSessionAccess.Connecting,
            };
        });
        _lease.Disposable = new CompositeDisposable(states, lease);
    }

    /// An unchanged row republishes nothing, so the pane's session handling runs only on a change.
    void PublishSession(bool ended) {
        var info = ChatSessionInfo.FromRemote(_row, ended);
        if (info != _session.Value) _session.OnNext(info);
    }

    void Release() {
        _leasedSession = null;
        _lease.Disposable = Disposable.Empty;
        _accessStates.OnNext(SessionAccessState.Unavailable);
    }

    static void Log(string note) => Console.Error.WriteLine($"kcap: remote chat: {note}");

    public void HandDraftTo(AgentOrigin origin) => _drafts?.Move(AgentOrigin.Remote, origin, AgentId);

    /// The server keeps a subagent's events on a stream of its own, which only its events route serves.
    void OpenSubagent(RunRow row) {
        if (row.AgentId is not { } subagentId || _sessionIds.Value is not { } sessionId || _readSubagent is not { } read) return;
        var vendor = _row.Vendor;
        SubagentPaneViewModel? pane = null;
        pane = new SubagentPaneViewModel(
            _runs, row, Title, AgentOrigin.Remote, AgentId, vendor, $"{sessionId}/{subagentId}",
            _ => new RemoteSubagentFeed(sessionId, subagentId, vendor, read, () => pane?.IsRunning ?? false, _time, Log),
            _opener, _time, _permissions, CloseSubagent);
        ReplaceSubagent(pane);
        ActiveTab = RemoteTab.Chat;
    }

    void CloseSubagent() => ReplaceSubagent(null);

    void ReplaceSubagent(SubagentPaneViewModel? next) {
        var previous = Subagent;
        Subagent = next;
        if (previous is not null) _ = previous.TeardownAsync();
    }

    public async Task TeardownAsync() {
        _disposables.Dispose();
        Artefacts.OpenRequested -= ShowArtefacts;
        await Chat.TeardownAsync();
        if (Subagent is { } subagent) await subagent.TeardownAsync();
        if (Terminal is { } terminal) await terminal.TeardownAsync();
        await WorkContext.TeardownAsync();
        await Artefacts.TeardownAsync();
        if (PullRequests is { } pullRequests) await pullRequests.TeardownAsync();
    }
}
