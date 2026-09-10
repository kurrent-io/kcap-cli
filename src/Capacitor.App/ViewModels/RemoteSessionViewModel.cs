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
    readonly SessionAccessService _access;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _lease = new();
    string? _leasedSession;
    AgentRow _row;

    public string AgentId { get; }
    public string? MachineBadge { get; }
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
            this.RaiseAndSetIfChanged(ref _sessionEnded, value);
            _sessionEndedChanges.OnNext(value);
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

    public bool ShowsCards => Access == RemoteSessionAccess.Ready;

    public string AccessNote => Access switch {
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
        MachineBadge = row.MachineBadge;
        Cards = new PendingCardsViewModel(row.Id, permissions, Observable.Return<string?>(null));
        Apply(row);

        directory.Rows.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(changes => {
                foreach (var change in changes) {
                    if (change.Key != row.Key) continue;
                    if (change.Reason == ChangeReason.Remove) { SessionEnded = true; Release(); continue; }
                    Apply(change.Current);
                }
            })
            .DisposeWith(_disposables);

        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWebRemote(row.Id));
        var canStop = _sessionEndedChanges
            .CombineLatest(actions.StopsInFlight, (ended, inFlight) => !ended && !inFlight.Contains(row.Id));
        StopCommand = ReactiveCommand.Create(
            () => actions.RequestStop(row.Id, $"{_row.Vendor} · {_row.RepoGroupLabel}", _row.Kind, AgentOrigin.Remote),
            canStop);
        _disposables.Add(OpenInWebCommand);
        _disposables.Add(StopCommand);
        _disposables.Add(_lease);
    }

    void Apply(AgentRow row) {
        _row = row;
        Title = row.Title ?? row.Vendor;
        RepoLabelText = $"{row.RepoGroupLabel} · on {row.MachineBadge}";
        StatusText = row.Status;
        StatusDot = SessionStatusDots.For(row.Status);
        if (SessionStatusDots.IsTerminal(row.Status)) { SessionEnded = true; Release(); return; }

        var sessionId = row.SessionId;
        if (sessionId == _leasedSession) return;
        Release();
        if (sessionId is null) { Access = RemoteSessionAccess.NoSession; return; }

        _leasedSession = sessionId;
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
