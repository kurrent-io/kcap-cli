using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteSessionAccess { Connecting, Ready, Denied, Offline, NoSession }
public enum RemoteTab { Chat, Terminal }

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
    readonly SerialDisposable _lease = new();
    // Never disposed, so a row revision landing after teardown cannot throw; the chat and its
    // cards unsubscribe from these when the pane is torn down.
    readonly BehaviorSubject<string?> _sessionIds;
    readonly BehaviorSubject<ChatSessionInfo> _session;
    /// The current lease's verdict, re-pointed whenever the lease moves; Unavailable in between.
    readonly BehaviorSubject<SessionAccessState> _accessStates = new(SessionAccessState.Unavailable);
    string? _leasedSession;
    AgentRow _row;

    public string AgentId { get; }
    public ChatTabViewModel Chat { get; }
    public PendingCardsViewModel Cards => Chat.Cards;
    public IObservable<SessionAccessState> AccessStates => _accessStates.AsObservable();
    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }

    /// Null for a harness with no PTY; the server registry carries no terminal flag, so the
    /// vendor's family decides, exactly as it does for a local dto without one.
    public RemoteTerminalViewModel? Terminal { get; }
    public bool ShowsTerminalTab => Terminal is not null;
    public bool ShowsSurfaceSwitch => ShowsTerminalTab;

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    string _repoLabelText = "";
    public string RepoLabelText { get => _repoLabelText; private set => this.RaiseAndSetIfChanged(ref _repoLabelText, value); }

    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    IBrush _statusDot = SessionStatusDots.For("");
    public IBrush StatusDot { get => _statusDot; private set => this.RaiseAndSetIfChanged(ref _statusDot, value); }

    RemoteTab _activeTab = RemoteTab.Chat;
    public RemoteTab ActiveTab {
        get => _activeTab;
        private set {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            RaiseTabProjections();
        }
    }
    public bool IsChatActive => ActiveTab == RemoteTab.Chat;
    public bool IsTerminalActive => ActiveTab == RemoteTab.Terminal;

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

    public bool ShowsChatPane => ShowsPanes && IsChatActive;
    public bool ShowsTerminalPane => ShowsPanes && IsTerminalActive;

    // What the terminal reports its viewport on. Same reason as _sessionEndedChanges above for a
    // subject rather than this.WhenAnyValue; never disposed, so a projection raised after teardown
    // cannot throw.
    readonly BehaviorSubject<bool> _terminalPaneShown = new(false);

    void RaiseTabProjections() {
        this.RaisePropertyChanged(nameof(IsChatActive));
        this.RaisePropertyChanged(nameof(IsTerminalActive));
        this.RaisePropertyChanged(nameof(ShowsPanes));
        this.RaisePropertyChanged(nameof(ShowsChatPane));
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
            AgentActionService actions, IServerLane lane, SessionDetailReader readDetail, IUrlOpener opener, TimeProvider time,
            Func<ITerminalSurface>? surfaceFactory = null) {
        _row = row;
        _access = access;
        AgentId = row.Id;
        _sessionIds = new BehaviorSubject<string?>(row.SessionId);
        _session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(row, ended: false));

        var input = new ServerChatInput(row.Id, lane, _accessStates, _session, HostedHarnessCatalog.ShowsTerminal(null, row.Vendor));
        Chat = new ChatTabViewModel(
            row.Id, AgentOrigin.Remote, _session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, row.Vendor, _accessStates, readDetail, lane, time, Log),
            opener, time, permissions, new SessionSubagents(time), missingNote: MissingNote, sessionId: _sessionIds,
            serverQueue: _sessionIds
                .Select(sid => sid is null
                    ? Observable.Empty<IReadOnlyList<QueuedInputItem>>()
                    : lane.PendingInputChanged.Where(u => u.SessionId == sid).Select(u => u.Items))
                .Switch());
        Terminal = surfaceFactory is not null && HostedHarnessCatalog.ShowsTerminal(null, row.Vendor)
            ? new RemoteTerminalViewModel(row.Id, lane, _accessStates, _sessionEndedChanges, _terminalPaneShown, surfaceFactory)
            : null;
        Apply(row);

        directory.Rows.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(changes => {
                foreach (var change in changes) {
                    if (change.Key != row.Key) continue;
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
        ShowChatCommand = ReactiveCommand.Create(() => { ActiveTab = RemoteTab.Chat; });
        ShowTerminalCommand = ReactiveCommand.Create(() => { if (ShowsTerminalTab) ActiveTab = RemoteTab.Terminal; });
        _disposables.Add(OpenInWebCommand);
        _disposables.Add(StopCommand);
        _disposables.Add(ShowChatCommand);
        _disposables.Add(ShowTerminalCommand);
        _disposables.Add(_lease);
    }

    void Apply(AgentRow row) {
        _row = row;
        if (_sessionIds.Value != row.SessionId) _sessionIds.OnNext(row.SessionId);
        Title = row.Title ?? row.Vendor;
        RepoLabelText = $"{row.RepoGroupLabel} · on {row.MachineBadge}";
        StatusText = SessionStatusDots.Label(row);
        StatusDot = SessionStatusDots.For(row);
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

    void AcquireLease(string sessionId) {
        _leasedSession = sessionId;
        // The lease's first state arrives asynchronously; without this the pane keeps the previous
        // session's verdict until it does.
        Access = RemoteSessionAccess.Connecting;
        var lease = _access.Acquire(sessionId);
        var states = lease.State.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => {
            _accessStates.OnNext(s);
            Access = s switch {
                SessionAccessState.Established => RemoteSessionAccess.Ready,
                SessionAccessState.Denied => RemoteSessionAccess.Denied,
                SessionAccessState.Unavailable => RemoteSessionAccess.Offline,
                _ => RemoteSessionAccess.Connecting,
            };
        });
        _lease.Disposable = new CompositeDisposable(states, lease);
    }

    void PublishSession(bool ended) => _session.OnNext(ChatSessionInfo.FromRemote(_row, ended));

    void Release() {
        _leasedSession = null;
        _lease.Disposable = Disposable.Empty;
        _accessStates.OnNext(SessionAccessState.Unavailable);
    }

    static void Log(string note) => Console.Error.WriteLine($"kcap: remote chat: {note}");

    public async Task TeardownAsync() {
        _disposables.Dispose();
        await Chat.TeardownAsync();
        if (Terminal is { } terminal) await terminal.TeardownAsync();
    }
}
