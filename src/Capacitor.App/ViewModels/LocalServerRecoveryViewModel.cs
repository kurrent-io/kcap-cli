using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The launcher's "local server is not running" banner. Its evidence is the server lane alone, so a
/// stopped server behind a healthy daemon still offers Start; a cloud server never does. While
/// <c>busy</c> (the upgrade banner owns the stack) the server still reads down but Start is not offered here.
public sealed class LocalServerRecoveryViewModel : ReactiveObject, IDisposable {
    const int MessageTailLines = 12;

    readonly Func<CancellationToken, Task<ProcessResult>> _start;
    readonly Func<Task> _reconnect;
    readonly CancellationToken _shutdown;
    readonly CompositeDisposable _disposables = new();
    readonly ObservableAsPropertyHelper<bool> _visible;
    readonly ObservableAsPropertyHelper<bool> _serverDown;
    bool _isStarting;
    int _running;
    int _connectedVersion;
    string? _message;

    public LocalServerRecoveryViewModel(
            IObservable<ServerLaneStatus> status, bool isLocalServer,
            Func<CancellationToken, Task<ProcessResult>> start, Func<Task> reconnect, CancellationToken shutdown,
            IObservable<bool>? busy = null) {
        _start = start;
        _reconnect = reconnect;
        _shutdown = shutdown;
        IsLocalServer = isLocalServer;

        // Sticky across the Connecting of each retry attempt, so the banner does not blink on every
        // backoff step; only an outcome other than retrying takes it down.
        var down = isLocalServer
            ? status.Select(s => s.State)
                .Scan(false, (shown, state) => state switch {
                    ServerLaneState.Retrying   => true,
                    ServerLaneState.Connecting => shown,
                    _                          => false,
                })
                .DistinctUntilChanged()
            : Observable.Return(false);
        _serverDown = down
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.ServerDown, initialValue: false)
            .DisposeWith(_disposables);
        _visible = down
            .CombineLatest(busy ?? Observable.Return(false), (d, b) => d && !b)
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Visible, initialValue: false)
            .DisposeWith(_disposables);

        // A server that came back on its own leaves no failure behind: a later stop is a stop, not that failure again.
        if (isLocalServer)
            status.Where(s => s.State == ServerLaneState.Connected)
                .Do(_ => Interlocked.Increment(ref _connectedVersion))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => Message = null)
                .DisposeWith(_disposables);

        StartCommand = ReactiveCommand.CreateFromTask(async () => { await StartAsync(); },
            this.WhenAnyValue(x => x.IsStarting).Select(starting => !starting));
    }

    public bool IsLocalServer { get; }

    public bool Visible => _visible.Value;

    /// The server lane's own reading, whether or not Start is offered.
    public bool ServerDown => _serverDown.Value;

    public bool IsStarting {
        get => _isStarting;
        private set => this.RaiseAndSetIfChanged(ref _isStarting, value);
    }

    /// The last start or reconnect failure; cleared when a new start begins or the server connects.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    public async Task<bool> StartAsync() {
        // ReactiveCommand's executing gate only disables a bound button; ICommand.Execute still runs.
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;
        var connectedVersion = Volatile.Read(ref _connectedVersion);
        IsStarting = true;
        try {
            Message = null;
            ProcessResult result;
            try {
                result = await _start(_shutdown);
            } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
                return false;
            } catch (Exception ex) {
                if (Volatile.Read(ref _connectedVersion) == connectedVersion)
                    Message = $"kcap local start could not run: {ex.Message}";
                return false;
            }

            if (result is { ExitCode: 0, TimedOut: false }) {
                try {
                    await _reconnect();
                } catch (Exception) when (_shutdown.IsCancellationRequested) {
                } catch (Exception ex) {
                    if (Volatile.Read(ref _connectedVersion) == connectedVersion)
                        Message = $"The local server started, but reconnecting failed: {ex.Message}";
                    return false;
                }
                return !_shutdown.IsCancellationRequested;
            }

            // A connection during this attempt supersedes even a failure returned after it.
            if (Volatile.Read(ref _connectedVersion) == connectedVersion)
                Message = MessageFor(result);
            return false;
        } finally {
            IsStarting = false;
            Volatile.Write(ref _running, 0);
        }
    }

    static string MessageFor(ProcessResult result) {
        var lines = (result.Stdout + "\n" + result.Stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(MessageTailLines);
        var output = TerminalAdvice.Strip(string.Join("\n", lines)) ?? "";
        var headline = result.TimedOut
            ? "kcap local start did not finish in time."
            : $"kcap local start failed (exit code {result.ExitCode}).";

        return output.Length == 0 ? headline : headline + "\n" + output;
    }

    public void Dispose() => _disposables.Dispose();
}
