using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The launcher's "local server is not running" banner. Its evidence is the server lane alone, so a
/// stopped server behind a healthy daemon still offers Start; a cloud server never does.
public sealed class LocalServerRecoveryViewModel : ReactiveObject, IDisposable {
    const int MessageTailLines = 12;

    readonly Func<CancellationToken, Task<ProcessResult>> _start;
    readonly Func<Task> _reconnect;
    readonly CancellationToken _shutdown;
    readonly CompositeDisposable _disposables = new();
    readonly ObservableAsPropertyHelper<bool> _visible;
    readonly ObservableAsPropertyHelper<bool> _isStarting;
    int _running;
    string? _message;

    public LocalServerRecoveryViewModel(
            IObservable<ServerLaneStatus> status, bool isLocalServer,
            Func<CancellationToken, Task<ProcessResult>> start, Func<Task> reconnect, CancellationToken shutdown) {
        _start = start;
        _reconnect = reconnect;
        _shutdown = shutdown;
        IsLocalServer = isLocalServer;

        // Sticky across the Connecting of each retry attempt, so the banner does not blink on every
        // backoff step; only an outcome other than retrying takes it down.
        var visible = isLocalServer
            ? status.Select(s => s.State)
                .Scan(false, (shown, state) => state switch {
                    ServerLaneState.Retrying   => true,
                    ServerLaneState.Connecting => shown,
                    _                          => false,
                })
                .DistinctUntilChanged()
            : Observable.Return(false);
        _visible = visible
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Visible, initialValue: false)
            .DisposeWith(_disposables);

        StartCommand = ReactiveCommand.CreateFromTask(RunStartAsync);
        _isStarting = StartCommand.IsExecuting
            .ToProperty(this, x => x.IsStarting, initialValue: false)
            .DisposeWith(_disposables);
    }

    public bool IsLocalServer { get; }

    public bool Visible => _visible.Value;

    public bool IsStarting => _isStarting.Value;

    /// The CLI's own output from the last failed start; null once a start succeeds or a new one begins.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    async Task RunStartAsync() {
        // ReactiveCommand's executing gate only disables a bound button; ICommand.Execute still runs.
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try {
            Message = null;
            ProcessResult result;
            try {
                result = await _start(_shutdown);
            } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                Message = $"kcap local start could not run: {ex.Message}";
                return;
            }

            if (result is { ExitCode: 0, TimedOut: false }) {
                await _reconnect();
                return;
            }

            Message = MessageFor(result);
        } finally {
            Volatile.Write(ref _running, 0);
        }
    }

    static string MessageFor(ProcessResult result) {
        var lines = (result.Stdout + "\n" + result.Stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(MessageTailLines);
        var output = string.Join("\n", lines);
        var headline = result.TimedOut
            ? "kcap local start did not finish in time."
            : $"kcap local start failed (exit code {result.ExitCode}).";

        return output.Length == 0 ? headline : headline + "\n" + output;
    }

    public void Dispose() => _disposables.Dispose();
}
