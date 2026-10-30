using System.Reactive;
using System.Reactive.Concurrency;
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
    /// The chat's read-only line for a live session no daemon hosts.
    public const string TerminalNote = "Running in a terminal · read-only here: answer it where it runs";
    /// The chat's read-only line for a session the server lists as ended.
    public const string EndedNote = "This session has ended";

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
    /// The chat keeps a session's rows when the row loses its session id, so only an agent that never
    /// had one shows its chat before a session.
    bool _hadSession;
    AgentRow _row;
    readonly BehaviorSubject<AgentRow> _rows;
    readonly BehaviorSubject<string> _titles;
    string? _viewerSubject;

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

    void PrefillComposer(string prompt) {
        Chat.ComposerText = PullRequestContextViewModel.WithFixPrompt(Chat.ComposerText, prompt);
        // The prompt is in the session's own composer, which an open subagent pane would hide.
        CloseSubagent();
        ActiveTab = RemoteTab.Chat;
    }

    bool CanPrefillComposer() => Chat.ShowsComposer;

    void ShowArtefacts() { if (ShowsArtefactsTab) ActiveTab = RemoteTab.Artefacts; }

    /// A chat card's Open: a declared document selects in the tab, a page link goes where the tab sends it.
    internal void OpenCard(ToolCard card) {
        if (card.DocumentPath is { } path) {
            if (Artefacts.OpenDocument(path)) return;
            if (Artefacts.HasAnyArtefacts) { Artefacts.RequestOpen(); return; }
        }
        if (card.Url is { } url) Artefacts.OpenLink(url);
    }

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    public SessionTitleEditorViewModel TitleEditor { get; }

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
            this.RaisePropertyChanged(nameof(AccessNote));
            _sessionEndedChanges.OnNext(value);
        }
    }

    /// A row the server lists with no daemon behind it: the lease follows the session, not an
    /// agent's liveness, and there is nothing to stop, attach to or message.
    public bool IsSessionOnly => _row.Origin == AgentOrigin.Server;
    public bool ShowsStop => !IsSessionOnly;

    readonly ReplaySubject<Unit> _ended = new(1);
    public IObservable<Unit> Ended => _ended.Take(1);
    /// Every flip of SessionEnded: a hosted row's removal or terminal status, and a session-only
    /// row's end, which fires nothing on Ended.
    public IObservable<bool> SessionEndedChanges => _sessionEndedChanges.AsObservable();
    /// The session this workspace is on, as the row last named it.
    public string? SessionId => _sessionIds.Value;
    // A transient empty registry snapshot also removes a live row, so only a removal after an
    // accepted stop counts as an end. Either may arrive first.
    bool _stopAccepted;
    bool _seenLive;

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
    /// The chat and the terminal are the exceptions. Before a session the chat shows under the note,
    /// empty, and offers the terminal; whether the terminal may show is the server's answer to its own
    /// subscribe, which it gives before a session too, so its pane shows whenever its tab is selected.
    public bool ShowsPanes =>
        Access is RemoteSessionAccess.Ready or RemoteSessionAccess.Offline && !OriginChangedToLocal;

    public bool ShowsChatPane =>
        (ShowsPanes || Access == RemoteSessionAccess.NoSession && !_hadSession && !OriginChangedToLocal) && IsChatActive && Subagent is null;
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
    public bool ShowsTerminalPane => IsTerminalActive && !OriginChangedToLocal;
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
        RemoteSessionAccess.Denied when IsSessionOnly && _row.AccessLevel is { Length: > 0 } level && level != "full" =>
            $"Shared with you at {level} access only · open it in web",
        RemoteSessionAccess.Denied => "You no longer have access to this session",
        RemoteSessionAccess.Offline => "Not connected to the server",
        RemoteSessionAccess.NoSession => !SessionEnded ? "Waiting for the session to start"
            : _hadSession ? ""
            : "The agent ended before its session started",
        _ => "",
    };

    /// Still on the launch's stand-in row: the registry has not listed the agent yet.
    public bool IsPlaceholder => _row.IsPlaceholder;

    public RemoteSessionViewModel(
            AgentRow row, IAgentDirectory directory, SessionAccessService access, IPermissionService permissions,
            AgentActionService actions, IServerLane lane, SessionDetailReader readDetail, IUrlOpener opener, TimeProvider time, IWorkContextSource workContext,
            Func<ITerminalSurface>? surfaceFactory = null, IPlanArtifactSource? planArtifacts = null,
            BackgroundCommandActivity? commands = null, IPullRequestSource? pullRequests = null,
            Action? requestSignIn = null, Action? linkGitHub = null, IObservable<Unit>? signInCompleted = null, IPlanSource? plans = null,
            ComposerDrafts? drafts = null, Func<string, Action>? inputSending = null,
            LastReads<PullRequestSnapshot>? pullRequestSnapshots = null, Action<string>? pullRequestLinksChanged = null,
            SubagentEventsReader? readSubagent = null, AccountReadings? readings = null, IMessageSenderSource? senders = null,
            IRemoteAgentsService? remoteAgents = null, IAppNotifier? notifier = null, IObservable<string?>? appSubject = null,
            IPageSource? pages = null, IServerSessionsService? serverSessions = null,
            PageLinkRouter? pageLinks = null, IWebViewAvailability? webView = null) {
        _drafts = drafts;
        _time = time;
        _permissions = permissions;
        _readSubagent = readSubagent;
        _row = row;
        _rows = new BehaviorSubject<AgentRow>(row);
        _titles = new BehaviorSubject<string>(row.Title ?? row.Vendor);
        _opener = opener;
        _access = access;
        AgentId = row.Id;
        _sessionIds = new BehaviorSubject<string?>(row.SessionId);
        _session = new BehaviorSubject<ChatSessionInfo>(SessionInfo(row, ended: row.IsEndedSession));

        var planActivity = new PlanActivity();
        Artefacts = new ArtefactsTabViewModel(planArtifacts, planActivity, time, pages: pages, opener: opener, pageLinks: pageLinks, webView: webView);
        Artefacts.OpenRequested += ShowArtefacts;
        Artefacts.WhenAnyValue(a => a.HasAnyArtefacts, a => a.IsLoading, (has, loading) => has || (loading && ShowsArtefactsTab))
            .Subscribe(show => {
                ShowsArtefactsTab = show;
                if (!show && IsArtefactsActive) ActiveTab = RemoteTab.Chat;
            }).DisposeWith(_disposables);
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
            requestSignIn, linkGitHub, signInCompleted, () => WorkContext.PrimaryRepository, snapshots: pullRequestSnapshots,
            linksChanged: pullRequestLinksChanged, requestFix: PrefillComposer, canRequestFix: CanPrefillComposer);
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
        ChatInput input = row.Origin == AgentOrigin.Server
            ? new ReadOnlyChatInput()
            : new ServerChatInput(row.Id, lane, _accessStates, _session, HostedHarnessCatalog.ShowsTerminal(null, row.Vendor),
                WorkContext.WhenAnyValue(context => context.ViewerIdentity));
        Chat = new ChatTabViewModel(
            row.Id, AgentOrigin.Remote, _session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, row.Vendor, _accessStates, readDetail, lane, time, Log),
            // A session-only chat has no agent here to answer for it: no session, no cards, no send.
            opener, time, permissions, runs, missingNote: MissingNote, sessionId: IsSessionOnly ? Observable.Return<string?>(null) : _sessionIds,
            serverQueue: Observable.Switch(_sessionIds
                .Select(sid => sid is null
                    ? Observable.Empty<IReadOnlyList<QueuedInputItem>>()
                    : lane.PendingInputChanged.Where(u => u.SessionId == sid).Select(u => u.Items))),
            planActivity: planActivity, openCard: OpenCard, drafts: drafts, senders: senders,
            accountReadings: row.MachineBadge is { } daemon ? readings?.Remote(daemon) : null, usageClock: readings?.Clock,
            signInCompleted: signInCompleted, openLink: Artefacts.OpenLink) { InputSending = inputSending };
        lane.Status.Select(s => s.Subject).DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(subject => {
                _viewerSubject = subject;
                PublishSession(SessionEnded);
            }).DisposeWith(_disposables);
        if (PullRequests is { } fixHost)
            Chat.WhenAnyValue(chat => chat.ShowsComposer).Subscribe(_ => fixHost.FixAvailabilityChanged()).DisposeWith(_disposables);
        Terminal = surfaceFactory is not null && row.Origin != AgentOrigin.Server && HostedHarnessCatalog.ShowsTerminal(null, row.Vendor)
            ? new RemoteTerminalViewModel(row.Id, lane, _accessStates, _sessionEndedChanges, _terminalPaneShown, time, surfaceFactory)
            : null;
        if (Terminal is not null) Chat.ShowTerminal = () => ActiveTab = RemoteTab.Terminal;
        Apply(row);

        // The server re-checks ownership on every write; this only avoids offering a rename that
        // would be refused. A listed session is the server's own record, so it stays renamable
        // after it ends; the listing folds the owner through the account's aliases.
        var canRename = remoteAgents is null
            ? Observable.Return(false)
            : _rows.CombineLatest(_sessionEndedChanges, _originChangedChanges, appSubject ?? SignedInSubject.From(lane.Status),
                (r, ended, movedLocal, viewer) => (!ended || r.Origin == AgentOrigin.Server) && !movedLocal
                    && r.SessionId is { Length: > 0 } && viewer is not null
                    && (r.OwnerUserId == viewer || (r.Origin == AgentOrigin.Server && r.OwnerIsViewer == true)))
                .ObserveOn(RxSchedulers.MainThreadScheduler);
        TitleEditor = new SessionTitleEditorViewModel(_titles, canRename, async (title, ct) => {
                if (remoteAgents is null || _row.SessionId is not { Length: > 0 } sessionId)
                    return new SessionRenameResult(false, null, SessionRenameReasons.NotFound);
                var result = await remoteAgents.RenameAsync(row.Id, sessionId, title, ct);
                if (result.Applied) serverSessions?.ApplyRename(sessionId, result.Title is { Length: > 0 } shown ? shown : title);
                return result;
            },
            notifier ?? new AppNotifier());
        _disposables.Add(TitleEditor);

        directory.Rows.Connect()
            .Filter(r => r.Key == row.Key)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(changes => {
                foreach (var change in changes) {
                    // A server row leaving the loaded set is a page cut or a scope change, not the
                    // session ending: the workspace stays bound to the session it holds.
                    if (change.Reason == ChangeReason.Remove && IsSessionOnly) continue;
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
                            if (_stopAccepted) _ended.OnNext(Unit.Default);
                        }
                        Release();
                        continue;
                    }
                    Apply(change.Current);
                }
            })
            .DisposeWith(_disposables);

        OpenInWebCommand = ReactiveCommand.Create(() => {
            if (IsSessionOnly) actions.OpenSessionInWeb(row.Id);
            else actions.OpenInWebRemote(row.Id);
        });
        var stopKey = AgentActionService.StopKey(AgentOrigin.Remote, row.Id);
        var canStop = _sessionEndedChanges
            .CombineLatest(_originChangedChanges, actions.StopsInFlight,
                (ended, movedLocal, inFlight) => !IsSessionOnly && !ended && !movedLocal && !inFlight.Contains(stopKey));
        StopCommand = ReactiveCommand.Create(
            () => actions.RequestStop(row.Id, $"{_row.Vendor} · {_row.RepoGroupLabel}", _row.Kind, AgentOrigin.Remote,
                accepted: () => RxSchedulers.MainThreadScheduler.Schedule(() => {
                    _stopAccepted = true;
                    if (SessionEnded) _ended.OnNext(Unit.Default);
                })),
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
        var sessionId = row.SessionId;
        if (!_hadSession && sessionId is not null) {
            _hadSession = true;
            this.RaisePropertyChanged(nameof(AccessNote));
            this.RaisePropertyChanged(nameof(ShowsChatPane));
        }
        if (_sessionIds.Value != sessionId) _sessionIds.OnNext(sessionId);
        Title = row.Title ?? row.Vendor;
        _titles.OnNext(Title);
        _rows.OnNext(row);
        RepoLabelText = row.Origin == AgentOrigin.Server
            ? row.OwnerName is { Length: > 0 } owner ? $"{row.RepoGroupLabel} · {owner}" : row.RepoGroupLabel
            : $"{row.RepoGroupLabel} · on {row.MachineBadge}";
        if (row.Origin == AgentOrigin.Server) {
            // The lease follows the session: acquired whatever its status, never released on an end,
            // and the end itself is the server's word on the row, not an agent's verdict.
            SessionEnded = row.IsEndedSession;
            _seenLive |= !SessionEnded;
            // Below full access the server grants no chat; the note names the level instead of a loss.
            if (row.AccessLevel is { Length: > 0 } level && level != "full") {
                if (_leasedSession is not null) Release();
                Access = RemoteSessionAccess.Denied;
            } else if (sessionId is { Length: > 0 } && sessionId != _leasedSession) AcquireLease(sessionId);
            PublishSession(ended: SessionEnded);
            this.RaisePropertyChanged(nameof(IsSessionOnly));
            this.RaisePropertyChanged(nameof(ShowsStop));
            return;
        }
        if (SessionStatusDots.IsTerminal(row.Status)) {
            SessionEnded = true;
            PublishSession(ended: true);
            if (_seenLive) _ended.OnNext(Unit.Default);
            Release();
            return;
        }
        // The row can come back: a transient empty registry snapshot removes it and the refresh
        // that follows re-adds the same live session, which must not stay hidden behind the
        // removal's verdict. Release() cleared the leased id, so the lease is re-acquired below.
        SessionEnded = false;
        OriginChangedToLocal = false;
        _seenLive = true;

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
            Title: _row.Title, SessionId: sessionId, Branch: _row.Branch));
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
        var info = SessionInfo(_row, ended) with {
            // The directory folds the owner through the account's aliases where the listing names
            // the session; the raw subject compare covers a registry row the listing has not.
            IsOwnedByCurrentUser = _row.OwnerIsViewer ?? (!string.IsNullOrWhiteSpace(_viewerSubject)
                && string.Equals(_row.OwnerUserId, _viewerSubject, StringComparison.Ordinal)),
        };
        if (info != _session.Value) _session.OnNext(info);
    }

    /// A session-only row's chat is never messageable: the notice says why, ended or in a terminal.
    static ChatSessionInfo SessionInfo(AgentRow row, bool ended) =>
        ChatSessionInfo.FromRemote(row, ended, row.Origin != AgentOrigin.Server ? "" : ended ? EndedNote : TerminalNote);

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
        // The chat opens its feed while the pane is still being built; until then the row it opened on answers.
        pane = new SubagentPaneViewModel(
            _runs, row, Title, AgentOrigin.Remote, AgentId, vendor, $"{sessionId}/{subagentId}",
            _ => new RemoteSubagentFeed(sessionId, subagentId, vendor, read, () => pane?.IsRunning ?? row.IsRunning, _time, Log),
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
