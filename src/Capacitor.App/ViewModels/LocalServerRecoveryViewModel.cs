using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The launcher's "local server is not running" banner. Whether the server is down is the server lane's
/// word alone, so a stopped server behind a healthy daemon still shows; a cloud server never does. Whether
/// Start is offered is the local root's files' and the port's word: never for an installation that is gone, that
/// another kcap is changing, or whose port another program holds. While <c>busy</c> (the upgrade banner owns the
/// stack) the banner stays hidden.
public sealed class LocalServerRecoveryViewModel : ReactiveObject, IDisposable {
    const int MessageTailLines = 12;

    readonly Func<CancellationToken, Task<ProcessResult>> _start;
    readonly Func<Task> _reconnect;
    readonly CancellationToken _shutdown;
    readonly CompositeDisposable _disposables = new();
    readonly ObservableAsPropertyHelper<bool> _visible;
    readonly ObservableAsPropertyHelper<bool> _serverDown;
    readonly Subject<Unit> _rereads = new();
    readonly bool _canStart;
    LocalServerReading? _reading;
    bool _disposed;
    bool _isStarting;
    int _running;
    int _connectedVersion;
    string? _message;

    /// <param name="readCondition">What the local root's files and the server's port say, read off the UI thread each
    /// time the lane retries and after each start; a newer read cancels the one in flight. Null trusts the
    /// installation to exist.</param>
    /// <param name="canStart">Whether <c>kcap local start</c> runs on this platform at all.</param>
    public LocalServerRecoveryViewModel(
            IObservable<ServerLaneStatus> status, bool isLocalServer,
            Func<CancellationToken, Task<ProcessResult>> start, Func<Task> reconnect, CancellationToken shutdown,
            IObservable<bool>? busy = null, Func<CancellationToken, Task<LocalServerReading>>? readCondition = null, bool canStart = true,
            IScheduler? readScheduler = null) {
        _start = start;
        _reconnect = reconnect;
        _shutdown = shutdown;
        _canStart = canStart;
        _reading = readCondition is null ? new(LocalServerCondition.Installed) : null;
        IsLocalServer = isLocalServer;

        if (isLocalServer && readCondition is not null) {
            var scheduler = readScheduler ?? RxSchedulers.TaskpoolScheduler;
            Observable.Switch(status.Where(s => s.State == ServerLaneState.Retrying).Select(_ => Unit.Default)
                    .Merge(_rereads)
                    .Select(_ => Observable.Return(Unit.Default, scheduler)
                        .SelectMany(Observable.FromAsync(readCondition))
                        .Catch(Observable.Return(new LocalServerReading(LocalServerCondition.Unreadable)))))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(r => Reading = r)
                .DisposeWith(_disposables);
        }

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
            this.WhenAnyValue(x => x.IsStarting, x => x.Condition, (starting, condition) => !starting && StartPossible(condition)));
        this.WhenAnyValue(x => x.Visible, x => x.IsStarting, x => x.Reading, (_, _, _) => Unit.Default)
            .Subscribe(_ => {
                this.RaisePropertyChanged(nameof(StartOffered));
                this.RaisePropertyChanged(nameof(Headline));
                this.RaisePropertyChanged(nameof(Body));
            })
            .DisposeWith(_disposables);
    }

    public bool IsLocalServer { get; }

    /// <summary>The banner shows: the lane reads the server down and no upgrade owns the stack.</summary>
    public bool Visible => _visible.Value;

    /// <summary>What the local root's files and the server's port say; null until the first read after the lane first
    /// retries.</summary>
    public LocalServerReading? Reading {
        get => _reading;
        private set {
            if (_reading == value) return;
            _reading = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Condition));
        }
    }

    public LocalServerCondition? Condition => Reading?.Condition;

    /// <summary>Start is offered only for an installation whose files are in place, with no other kcap changing it,
    /// on a platform where <c>kcap local start</c> runs.</summary>
    public bool StartOffered => Visible && !IsStarting && StartPossible(Condition);

    public string Headline => Condition switch {
        LocalServerCondition.Changing                                   => "The local server is being changed",
        LocalServerCondition.PortTaken                                  => "Another program holds the local server's port",
        LocalServerCondition.Uninstalled                                => "The local server was uninstalled",
        LocalServerCondition.Purged or LocalServerCondition.NotInstalled => "The local server was removed",
        LocalServerCondition.FilesMissing or LocalServerCondition.Interrupted or LocalServerCondition.Unreadable
                                                                        => "The local server needs attention",
        _                                                               => "The local server isn't running",
    };

    public string? Body => Condition switch {
        null                                       => null,
        LocalServerCondition.PortTaken             => PortTakenBody(Reading!),
        LocalServerCondition.Installed when _canStart => "Start it to launch agents and open the dashboard.",
        LocalServerCondition.Installed             => "kcap can't start it on this computer. Start it the way it was installed.",
        { } other                                  => other.Explanation,
    };

    /// <summary>Names the holder when the OS or its answer does; kcap never stops it, so the user does.</summary>
    static string PortTakenBody(LocalServerReading reading) {
        var find = reading.Holder is not null ? ""
            : OperatingSystem.IsWindows() ? $" netstat -ano -p TCP | findstr :{reading.Port} shows which process it is."
            : $" sudo lsof -nP -iTCP:{reading.Port} -sTCP:LISTEN shows which process it is.";

        return $"Port {reading.Port} is held by {reading.HolderText}, so the local server cannot listen on it. The port is part of "
             + $"the server's identity, so it cannot move to another. Stop that program; kcap never stops a program it did not start.{find}";
    }

    bool StartPossible(LocalServerCondition? condition) => _canStart && condition == LocalServerCondition.Installed;

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
        if (!StartPossible(Condition)) return false;
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
            if (!_disposed) _rereads.OnNext(Unit.Default);
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

    public void Dispose() {
        _disposed = true;
        _disposables.Dispose();
        _rereads.Dispose();
    }
}
