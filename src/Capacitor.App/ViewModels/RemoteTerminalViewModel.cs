using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteTerminalPhase { Waiting, Connecting, Live, Offline, Ended, Refused }

/// Each subscribe replays onto a fresh surface. Buffer dimensions follow the source;
/// the measured viewport is reported separately while visible, since the server clamps
/// the PTY to the smallest viewer. Subscribe only on first showing or when access can change.
public sealed class RemoteTerminalViewModel : ReactiveObject {
    /// The hub's own bounds, and (0,0) is its clear sentinel — a client never sends one.
    const int MaxCols = 500, MaxRows = 200;
    static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    readonly string _agentId;
    readonly IServerLane _lane;
    readonly TimeProvider _time;
    readonly Func<ITerminalSurface> _surfaceFactory;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly CancellationToken _token;
    // The key command's canExecute input. Not this.WhenAnyValue: that call routes through
    // ReactiveUI's ObservableForProperty/RxAppBuilder global init, which only some other code
    // having built the app primes.
    readonly BehaviorSubject<RemoteTerminalPhase> _phases = new(RemoteTerminalPhase.Waiting);
    int _generation;
    Utf8StreamDecoder? _decoder;
    bool _subscribed;
    // The hub delivers the replay (TerminalDimensions and buffered TerminalOutput) INSIDE the
    // SubscribeToTerminal invocation, before it returns -- so frames arrive before _subscribed is
    // ever set. This tracks "attached and expecting output" instead, true from the moment Attach
    // starts the subscribe call, not from the moment it resolves.
    bool _receiving;
    bool _ended;
    bool _visible;
    bool _everShown;
    /// A superseded subscribe that the hub took while a newer one was in flight: membership is the
    /// connection's, so it is given back only if that newer one is refused.
    bool _strayMembership;
    /// Whether a viewport of ours stands in the server's aggregate: it holds a viewer's size until
    /// told otherwise, so a reported one must always be given back and an unreported one never.
    bool _reported;
    (int Cols, int Rows)? _sourceSize;
    (int Cols, int Rows)? _viewport;
    public int SourceColumns => _sourceSize?.Cols ?? 80;
    public int SourceRows => _sourceSize?.Rows ?? 24;
    SessionAccessState? _access;
    /// Admitted before session access was established: the session starting is no reason to ask again.
    bool _carriesOn;
    ITimer? _retry;
    int _failures;

    ITerminalSurface? _surface;
    public ITerminalSurface? Surface { get => _surface; private set => this.RaiseAndSetIfChanged(ref _surface, value); }

    RemoteTerminalPhase _phase = RemoteTerminalPhase.Waiting;
    public RemoteTerminalPhase Phase {
        get => _phase;
        private set {
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
            this.RaisePropertyChanged(nameof(ShowsBanner));
            _phases.OnNext(value);
        }
    }

    public string PhaseNote => Phase switch {
        RemoteTerminalPhase.Waiting    => "Waiting for the session…",
        RemoteTerminalPhase.Connecting => "Connecting…",
        RemoteTerminalPhase.Offline    => "Not connected to the session",
        RemoteTerminalPhase.Ended      => "This session has ended.",
        RemoteTerminalPhase.Refused    => RefusedNote,
        _                              => "",
    };
    internal const string RefusedNote = "You don't have access to this session's terminal.";

    public bool ShowsBanner => Phase != RemoteTerminalPhase.Live;
    public string SizeNote => _sourceSize is { } s ? $"{s.Cols}×{s.Rows} · sized by the source" : "";
    public IReadOnlyList<SpecialKeyChoice> Keys => SpecialKeyMapper.Choices;
    public ReactiveCommand<string, Unit> SendKeyCommand { get; }

    public RemoteTerminalViewModel(
            string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<bool> sessionEnded,
            IObservable<bool> visible, TimeProvider time, Func<ITerminalSurface> surfaceFactory) {
        _agentId = agentId;
        _lane = lane;
        _time = time;
        _surfaceFactory = surfaceFactory;
        _token = _lifetime.Token;
        SendKeyCommand = ReactiveCommand.CreateFromTask<string>(
            SendKeyAsync, _phases.Select(phase => phase == RemoteTerminalPhase.Live));
        _disposables.Add(SendKeyCommand);

        visible.DistinctUntilChanged().ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(shown => {
            _visible = shown;
            if (!shown) { _ = ReleaseViewportAsync(); return; }
            if (!_everShown) {
                _everShown = true;
                Ask();
                return;
            }
            if (_viewport is { } size) ReportViewport(size);
        }).DisposeWith(_disposables);
        // The host's verdict can be withdrawn: a transient registry snapshot drops the row and the
        // next refresh restores the same live session, so the pane follows the lease again then.
        sessionEnded.DistinctUntilChanged().ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(ended => {
            if (ended) {
                _ended = true;
                Detach(RemoteTerminalPhase.Ended);
                return;
            }
            if (!_ended) return;
            _ended = false;
            Ask();
        }).DisposeWith(_disposables);
        // The server ends a revoked viewer's stream without a word, so a denial asks again, but only
        // for a terminal it could end. Any establishment asks again too — a new session can come with
        // a replacement terminal — except the session starting under a terminal already live.
        access.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(state => {
            _access = state;
            if (state == SessionAccessState.Denied) {
                if (Phase is RemoteTerminalPhase.Live or RemoteTerminalPhase.Connecting) Ask();
                return;
            }
            if (state != SessionAccessState.Established) return;
            if (Phase == RemoteTerminalPhase.Live && _carriesOn) { _carriesOn = false; return; }
            Ask();
        }).DisposeWith(_disposables);
        // The server forgets a connection's subscriptions when it drops.
        lane.Status.Select(s => s.State == ServerLaneState.Connected).DistinctUntilChanged().Skip(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(connected => {
                if (connected) Ask();
                else if (!_ended) Detach(RemoteTerminalPhase.Offline);
            }).DisposeWith(_disposables);
        lane.TerminalOutput.Where(f => f.AgentId == agentId).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(f => {
            if (!_receiving || Surface is not { } surface || _decoder is not { } decoder) return;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(f.Base64); } catch (FormatException) { return; }
            surface.Feed(decoder.Decode(bytes));
        }).DisposeWith(_disposables);
        lane.TerminalDimensions.Where(d => d.AgentId == agentId).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(d => {
            _sourceSize = (d.Cols, d.Rows);
            Surface?.Resize(d.Cols, d.Rows);
            this.RaisePropertyChanged(nameof(SizeNote));
            this.RaisePropertyChanged(nameof(SourceColumns));
            this.RaisePropertyChanged(nameof(SourceRows));
        }).DisposeWith(_disposables);
    }

    void Ask() {
        StopRetry();
        if (!_everShown || _ended) return;
        if (_subscribed || _strayMembership) Detach(RemoteTerminalPhase.Connecting);
        Attach();
    }

    void Attach() {
        var generation = ++_generation;
        var surface = _surfaceFactory();
        var decoder = new Utf8StreamDecoder();
        surface.InputProduced += bytes => {
            if (generation != _generation || SpecialKeyMapper.Map(bytes) is not { } key) return;
            _ = SendKeyAsync(key);
        };
        if (_sourceSize is { } size) surface.Resize(size.Cols, size.Rows);
        _decoder = decoder;
        Surface = surface;
        Phase = RemoteTerminalPhase.Connecting;
        _receiving = true;
        _ = SubscribeAsync(generation);
    }

    async Task SubscribeAsync(int generation) {
        HubCallOutcome outcome;
        try {
            outcome = await _lane.SubscribeToTerminalAsync(_agentId, _token);
        } catch (OperationCanceledException) {
            // Teardown cancelled _token while the hub may already have taken the subscribe —
            // ReleaseAsync's own calls carry CancellationToken.None.
            Dispatcher.UIThread.Post(() => { if (!_receiving) _ = ReleaseAsync(); });
            return;
        } catch (Exception ex) {
            outcome = HubCallOutcome.Failed(ex.Message);
        }
        await Dispatcher.UIThread.InvokeAsync(() => {
            // Group membership belongs to the connection, not to this attempt: unsubscribing for a
            // superseded one would deafen the pane a newer attach is already receiving on.
            if (generation != _generation) {
                if (outcome.Result != HubCallResult.Ok) return;
                if (!_receiving) _ = ReleaseAsync();
                else _strayMembership = true;
                return;
            }
            // A refused subscribe must not render frames meant for another viewer on the connection.
            if (outcome.Result != HubCallResult.Ok) {
                _receiving = false;
                Phase = outcome.Result == HubCallResult.Denied ? RemoteTerminalPhase.Refused : RemoteTerminalPhase.Offline;
                if (_strayMembership) {
                    _strayMembership = false;
                    _ = ReleaseAsync();
                }
                // Before a session nothing else would ask again, and a lane that is down asks on reconnect.
                if (outcome.Result == HubCallResult.Failed) ScheduleRetry(generation);
                else if (outcome.Result == HubCallResult.Denied) _failures = 0;
                return;
            }
            _failures = 0;
            _carriesOn = _access != SessionAccessState.Established;
            _strayMembership = false;
            _subscribed = true;
            Phase = RemoteTerminalPhase.Live;
            if (_viewport is { } size) ReportViewport(size);
        });
    }

    void ScheduleRetry(int generation) {
        var delay = Retry[Math.Min(_failures++, Retry.Length - 1)];
        _retry = _time.CreateTimer(_ => Dispatcher.UIThread.Post(() => {
            if (generation == _generation && Phase == RemoteTerminalPhase.Offline) Ask();
        }), null, delay, Timeout.InfiniteTimeSpan);
    }

    void StopRetry() {
        _retry?.Dispose();
        _retry = null;
    }

    public void SetViewport(int cols, int rows) {
        if (cols is not (> 0 and <= MaxCols) || rows is not (> 0 and <= MaxRows)) return;
        if (_viewport == (cols, rows)) return;
        _viewport = (cols, rows);
        ReportViewport((cols, rows));
    }

    // A hidden pane must not clamp the source to its last measured viewport.
    void ReportViewport((int Cols, int Rows) size) {
        if (!_visible || !_subscribed) return;
        var (cols, rows) = size;
        if (cols is not (> 0 and <= MaxCols) || rows is not (> 0 and <= MaxRows)) return;
        _reported = true;
        _ = Report(_lane.RequestResizeTerminalAsync(_agentId, cols, rows, _token), "resize");
    }

    Task ReleaseViewportAsync() {
        if (!_reported) return Task.CompletedTask;
        _reported = false;
        return Report(_lane.ReleaseResizeTerminalAsync(_agentId, CancellationToken.None), "release");
    }

    void Detach(RemoteTerminalPhase phase) {
        StopRetry();
        _generation++;
        _receiving = false;
        Phase = phase;
        if (!_subscribed && !_strayMembership) return;
        _subscribed = false;
        _strayMembership = false;
        _ = ReleaseAsync();
    }

    /// Unsubscribe and give back whatever viewport this viewer holds — the server keeps a viewer's
    /// size until told otherwise. Both go out now, ahead of anything a newer attach sends, and
    /// whether a viewport is owed is settled synchronously: deciding after the unsubscribe returned
    /// could take back one the next attach had reported meanwhile.
    Task ReleaseAsync() => Task.WhenAll(
        Report(_lane.UnsubscribeFromTerminalAsync(_agentId, CancellationToken.None), "unsubscribe"),
        ReleaseViewportAsync());

    async Task SendKeyAsync(string key) {
        if (!_subscribed || Phase != RemoteTerminalPhase.Live) return;
        await Report(_lane.SendSpecialKeyAsync(_agentId, key, _token), $"key {key}");
    }

    static async Task Report(Task<HubCallOutcome> call, string what) {
        try {
            var outcome = await call;
            if (outcome.Result is HubCallResult.Denied or HubCallResult.Failed)
                Console.Error.WriteLine($"kcap: remote terminal {what}: {outcome.Reason}");
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: remote terminal {what}: {ex.Message}");
        }
    }

    public async Task TeardownAsync() {
        StopRetry();
        _generation++;
        _receiving = false;
        _disposables.Dispose();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        if (_subscribed || _strayMembership) {
            _subscribed = false;
            _strayMembership = false;
            await ReleaseAsync();
        }
        Surface = null;
        _decoder = null;
        _lifetime.Dispose();
    }
}
