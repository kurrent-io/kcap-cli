using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteTerminalPhase { Waiting, Connecting, Live, Offline, Ended }

/// A read-only view of a terminal on another machine: the server replays its buffer, then
/// streams. The pane takes the source's size, and the viewport it reports back is released when
/// it stops driving — the server's (0,0) is a clear sentinel a client never sends. Every access
/// establishment subscribes again onto a fresh surface, so a replay never stacks on scrollback.
/// Subscribing is attempted only once access stands: the server refuses with silence, so an
/// empty pane after that is "no output yet", never "not allowed".
public sealed class RemoteTerminalViewModel : ReactiveObject {
    readonly string _agentId;
    readonly IServerLane _lane;
    readonly Func<ITerminalSurface> _surfaceFactory;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly CancellationToken _token;
    int _generation;
    Utf8StreamDecoder? _decoder;
    bool _subscribed;
    // The hub delivers the replay (TerminalDimensions and buffered TerminalOutput) INSIDE the
    // SubscribeToTerminal invocation, before it returns -- so frames arrive before _subscribed is
    // ever set. This tracks "attached and expecting output" instead, true from the moment Attach
    // starts the subscribe call, not from the moment it resolves.
    bool _receiving;
    bool _ended;
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
            Func<ITerminalSurface> surfaceFactory) {
        _agentId = agentId;
        _lane = lane;
        _surfaceFactory = surfaceFactory;
        _token = _lifetime.Token;
        SendKeyCommand = ReactiveCommand.CreateFromTask<string>(SendKeyAsync);
        _disposables.Add(SendKeyCommand);

        sessionEnded.Where(ended => ended).Take(1).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => {
            _ended = true;
            Detach(RemoteTerminalPhase.Ended);
        }).DisposeWith(_disposables);
        // Establishing is the pre-verdict handshake, not a refusal.
        access.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(state => {
            if (_ended) return;
            if (state == SessionAccessState.Established) { Attach(); return; }
            Detach(state == SessionAccessState.Establishing ? RemoteTerminalPhase.Waiting : RemoteTerminalPhase.Offline);
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

    void Attach() {
        var generation = ++_generation;
        var surface = _surfaceFactory();
        var decoder = new Utf8StreamDecoder();
        surface.InputProduced += bytes => {
            if (generation != _generation || SpecialKeyMapper.Map(bytes) is not { } key) return;
            _ = SendKeyAsync(key);
        };
        surface.Resized += (cols, rows) => {
            if (generation != _generation || !_subscribed) return;
            _ = Report(_lane.RequestResizeTerminalAsync(_agentId, cols, rows, _token), "resize");
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
            // release regardless, since ReleaseAsync's own calls carry CancellationToken.None.
            _ = ReleaseAsync();
            return;
        } catch (Exception ex) {
            outcome = HubCallOutcome.Failed(ex.Message);
        }
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (generation != _generation) { _ = ReleaseAsync(); return; }
            // A refused subscribe must not render frames meant for another viewer on the connection.
            if (outcome.Result != HubCallResult.Ok) { _receiving = false; Phase = RemoteTerminalPhase.Offline; return; }
            _subscribed = true;
            Phase = RemoteTerminalPhase.Live;
            var (cols, rows) = surface.CurrentSize;
            _ = Report(_lane.RequestResizeTerminalAsync(_agentId, cols, rows, _token), "resize");
        });
    }

    void Detach(RemoteTerminalPhase phase) {
        _generation++;
        _receiving = false;
        Phase = phase;
        if (!_subscribed) return;
        _subscribed = false;
        _ = ReleaseAsync();
    }

    /// Unsubscribe, then release the viewport: the server keeps a viewer's size in its aggregate
    /// until told otherwise or until the whole connection drops.
    async Task ReleaseAsync() {
        await Report(_lane.UnsubscribeFromTerminalAsync(_agentId, CancellationToken.None), "unsubscribe");
        await Report(_lane.ReleaseResizeTerminalAsync(_agentId, CancellationToken.None), "release");
    }

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
