using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteTerminalPhase { Waiting, Connecting, Live, Offline, Ended }

/// A read-only view of a terminal on another machine: the server replays its buffer, then
/// streams. The pane takes the source's size, and reports a viewport back only while it is on
/// screen — the server takes the minimum across viewers, so a hidden pane would clamp the live
/// agent's PTY for everyone. Every access establishment subscribes again onto a fresh surface, so
/// a replay never stacks on scrollback. Subscribing is attempted only once access stands: the
/// server refuses with silence, so an empty pane after that is "no output yet", never "not
/// allowed".
public sealed class RemoteTerminalViewModel : ReactiveObject {
    /// The hub's own bounds, and (0,0) is its clear sentinel — a client never sends one.
    const int MaxCols = 500, MaxRows = 200;

    readonly string _agentId;
    readonly IServerLane _lane;
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
    SessionAccessState? _access;
    bool _visible;
    /// Whether a viewport of ours stands in the server's aggregate: it holds a viewer's size until
    /// told otherwise, so a reported one must always be given back and an unreported one never.
    bool _reported;
    (int Cols, int Rows)? _sourceSize;

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
        _                              => "",
    };
    public bool ShowsBanner => Phase != RemoteTerminalPhase.Live;
    public string SizeNote => _sourceSize is { } s ? $"{s.Cols}×{s.Rows} · sized by the source" : "";
    public IReadOnlyList<SpecialKeyChoice> Keys => SpecialKeyMapper.Choices;
    public ReactiveCommand<string, Unit> SendKeyCommand { get; }

    public RemoteTerminalViewModel(
            string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<bool> sessionEnded,
            IObservable<bool> visible, Func<ITerminalSurface> surfaceFactory) {
        _agentId = agentId;
        _lane = lane;
        _surfaceFactory = surfaceFactory;
        _token = _lifetime.Token;
        SendKeyCommand = ReactiveCommand.CreateFromTask<string>(
            SendKeyAsync, _phases.Select(phase => phase == RemoteTerminalPhase.Live));
        _disposables.Add(SendKeyCommand);

        visible.DistinctUntilChanged().ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(shown => {
            _visible = shown;
            if (!shown) { _ = ReleaseViewportAsync(); return; }
            if (Surface is { } surface) ReportViewport(surface.CurrentSize);
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
            Follow(_access);
        }).DisposeWith(_disposables);
        access.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(state => {
            _access = state;
            if (!_ended) Follow(state);
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
        }).DisposeWith(_disposables);
    }

    /// Establishing is the pre-verdict handshake, not a refusal.
    void Follow(SessionAccessState? state) {
        if (state == SessionAccessState.Established) { Attach(); return; }
        Detach(state is null or SessionAccessState.Establishing ? RemoteTerminalPhase.Waiting : RemoteTerminalPhase.Offline);
    }

    void Attach() {
        var generation = ++_generation;
        var surface = _surfaceFactory();
        var decoder = new Utf8StreamDecoder();
        surface.InputProduced += bytes => {
            if (generation != _generation || SpecialKeyMapper.Map(bytes) is not { } key) return;
            _ = SendKeyAsync(key);
        };
        surface.Resized += (cols, rows) => {
            if (generation != _generation) return;
            ReportViewport((cols, rows));
        };
        if (_sourceSize is { } size) surface.Resize(size.Cols, size.Rows);
        _decoder = decoder;
        Surface = surface;
        Phase = RemoteTerminalPhase.Connecting;
        _receiving = true;
        _ = SubscribeAsync(generation, surface);
    }

    async Task SubscribeAsync(int generation, ITerminalSurface surface) {
        HubCallOutcome outcome;
        try {
            outcome = await _lane.SubscribeToTerminalAsync(_agentId, _token);
        } catch (OperationCanceledException) {
            // Teardown cancelled _token while the hub may already have taken the subscribe —
            // ReleaseAsync's own calls carry CancellationToken.None.
            if (!_receiving) _ = ReleaseAsync();
            return;
        } catch (Exception ex) {
            outcome = HubCallOutcome.Failed(ex.Message);
        }
        await Dispatcher.UIThread.InvokeAsync(() => {
            // Group membership belongs to the connection, not to this attempt: unsubscribing for a
            // superseded one would deafen the pane a newer attach is already receiving on.
            if (generation != _generation) { if (!_receiving) _ = ReleaseAsync(); return; }
            // A refused subscribe must not render frames meant for another viewer on the connection.
            if (outcome.Result != HubCallResult.Ok) { _receiving = false; Phase = RemoteTerminalPhase.Offline; return; }
            _subscribed = true;
            Phase = RemoteTerminalPhase.Live;
            ReportViewport(surface.CurrentSize);
        });
    }

    /// The one place a viewport is reported: a pane off screen still carries its unmeasured
    /// constructor size, and the server clamps the source to the smallest viewer.
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
        _generation++;
        _receiving = false;
        Phase = phase;
        if (!_subscribed) return;
        _subscribed = false;
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
        _generation++;
        _receiving = false;
        _disposables.Dispose();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        if (_subscribed) {
            _subscribed = false;
            await ReleaseAsync();
        }
        Surface = null;
        _decoder = null;
        _lifetime.Dispose();
    }
}
