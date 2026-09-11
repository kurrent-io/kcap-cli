using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using Capacitor.App.Services;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteSessionAccess { Connecting, Ready, Denied, Offline, NoSession }

/// The workspace for a row the app has no socket to: the header, the NEEDS YOU cards, Stop and
/// Open in web. Access is the server's explicit signal — Ready only after the watch and the chat
/// join both succeeded — so an empty pane is "no cards", never "not allowed".
public sealed class RemoteSessionViewModel : ReactiveObject, ISessionWorkspace {
    internal const string OriginChangedNote = "This agent is now hosted locally — open it from the rail";

    readonly SessionAccessService _access;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _lease = new();
    // Same reason as _sessionEndedChanges below: never disposed, so a row revision landing after
    // teardown cannot throw. Cards unsubscribes from it when the pane is torn down.
    readonly BehaviorSubject<string?> _sessionIds;
    string? _leasedSession;
    AgentRow _row;

    public string AgentId { get; }
    public PendingCardsViewModel Cards { get; }
    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    string _repoLabelText = "";
    public string RepoLabelText { get => _repoLabelText; private set => this.RaiseAndSetIfChanged(ref _repoLabelText, value); }

    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    IBrush _statusDot = SessionStatusDots.For("");
    public IBrush StatusDot { get => _statusDot; private set => this.RaiseAndSetIfChanged(ref _statusDot, value); }

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
            this.RaisePropertyChanged(nameof(ShowsCards));
            _sessionEndedChanges.OnNext(value);
        }
    }

    // Same reason as _sessionEndedChanges above: Stop's canExecute reads the flag through a subject.
    readonly BehaviorSubject<bool> _originChangedChanges = new(false);

    bool _originChangedToLocal;
    /// The local daemon has proven this row is its twin: the remote row is gone but the agent is
    /// not, so the window replaces this host with the local workspace for the same id.
    public bool OriginChangedToLocal {
        get => _originChangedToLocal;
        private set {
            if (_originChangedToLocal == value) return;
            this.RaiseAndSetIfChanged(ref _originChangedToLocal, value);
            this.RaisePropertyChanged(nameof(AccessNote));
            _originChangedChanges.OnNext(value);
        }
    }

    RemoteSessionAccess _accessState = RemoteSessionAccess.NoSession;
    public RemoteSessionAccess Access {
        get => _accessState;
        private set {
            this.RaiseAndSetIfChanged(ref _accessState, value);
            this.RaisePropertyChanged(nameof(AccessNote));
            this.RaisePropertyChanged(nameof(ShowsCards));
        }
    }

    /// An ended session's last Ready still stands until the lease is released, and its cards are
    /// unanswerable — nobody is waiting on them any more.
    public bool ShowsCards => Access == RemoteSessionAccess.Ready && !SessionEnded;

    public string AccessNote => OriginChangedToLocal ? OriginChangedNote : Access switch {
        RemoteSessionAccess.Connecting => "Connecting to the session…",
        RemoteSessionAccess.Denied => "You no longer have access to this session",
        RemoteSessionAccess.Offline => "Not connected to the server",
        RemoteSessionAccess.NoSession => "Waiting for the session to start",
        _ => "",
    };

    public RemoteSessionViewModel(
            AgentRow row, IAgentDirectory directory, SessionAccessService access, IPermissionService permissions,
            AgentActionService actions) {
        _row = row;
        _access = access;
        AgentId = row.Id;
        _sessionIds = new BehaviorSubject<string?>(row.SessionId);
        Cards = new PendingCardsViewModel(row.Id, AgentOrigin.Remote, _sessionIds, permissions, Observable.Return<string?>(null));
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
                        if (directory.IsProvenLocalTwin(row.Id)
                            && directory.Rows.Lookup($"local:{row.Id}") is { HasValue: true, Value: var twin }
                            && twin.SessionId is { Length: > 0 } && twin.SessionId == _row.SessionId) {
                            OriginChangedToLocal = true;
                            // Nothing is answerable through a released lease, and the agent is not
                            // this host's to stop any more.
                            Access = RemoteSessionAccess.NoSession;
                        } else {
                            SessionEnded = true;
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
        _disposables.Add(OpenInWebCommand);
        _disposables.Add(StopCommand);
        _disposables.Add(_lease);
    }

    void Apply(AgentRow row) {
        _row = row;
        if (_sessionIds.Value != row.SessionId) _sessionIds.OnNext(row.SessionId);
        Title = row.Title ?? row.Vendor;
        RepoLabelText = $"{row.RepoGroupLabel} · on {row.MachineBadge}";
        StatusText = row.Status;
        StatusDot = SessionStatusDots.For(row.Status);
        if (SessionStatusDots.IsTerminal(row.Status)) { SessionEnded = true; Release(); return; }
        // The row can come back: a transient empty registry snapshot removes it and the refresh
        // that follows re-adds the same live session, which must not stay hidden behind the
        // removal's verdict. Release() cleared the leased id, so the lease is re-acquired below.
        SessionEnded = false;
        OriginChangedToLocal = false;

        var sessionId = row.SessionId;
        if (sessionId == _leasedSession) return;
        Release();
        if (sessionId is null) { Access = RemoteSessionAccess.NoSession; return; }

        _leasedSession = sessionId;
        // The lease's first state arrives asynchronously; without this the pane keeps the previous
        // session's verdict until it does.
        Access = RemoteSessionAccess.Connecting;
        var lease = _access.Acquire(sessionId);
        var states = lease.State.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => Access = s switch {
            SessionAccessState.Established => RemoteSessionAccess.Ready,
            SessionAccessState.Denied => RemoteSessionAccess.Denied,
            SessionAccessState.Unavailable => RemoteSessionAccess.Offline,
            _ => RemoteSessionAccess.Connecting,
        });
        _lease.Disposable = new CompositeDisposable(states, lease);
    }

    void Release() {
        _leasedSession = null;
        _lease.Disposable = Disposable.Empty;
    }

    public Task TeardownAsync() {
        _disposables.Dispose();
        Cards.Dispose();
        return Task.CompletedTask;
    }
}
