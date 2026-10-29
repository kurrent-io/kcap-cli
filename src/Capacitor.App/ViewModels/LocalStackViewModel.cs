using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// The local stack beside the daemon in the main window: its version and state in the rail, and a
/// launcher banner for an upgrade in flight or one that did not finish. A failure after the switch
/// carries its own Start, offered until a start succeeds: the server can be up with the upgrade still
/// journaled, so connectivity proves nothing.
/// </summary>
public sealed class LocalStackViewModel : ReactiveObject, IDisposable {
    internal const string BusyDetail    = "Another local setup, upgrade or uninstall is running on this Mac. The upgrade runs again once the local server is back.";
    internal const string StartGuidance = "Its data is kept. Start it to finish the upgrade.";
    const int StartMessageTailLines     = 12;
    public const string NameTip         = "The local kcap server on this machine";
    public const string VersionTip      = "Version of kcap local on this machine";

    static readonly string[] Derived = [
        nameof(Version), nameof(StateText), nameof(Attention), nameof(Upgrading), nameof(BannerVisible), nameof(Headline),
        nameof(Detail), nameof(Guidance), nameof(DownloadFraction), nameof(ProgressIndeterminate), nameof(TryAgainVisible),
        nameof(DismissVisible), nameof(IsNote), nameof(StartVisible), nameof(ActionsVisible), nameof(SuppressesServerStart),
    ];

    readonly LocalUpgradeController _upgrade;
    readonly LocalSearchController? _searchChange;
    readonly Func<CancellationToken, Task<ProcessResult>> _start;
    readonly Func<Task> _reconnect;
    readonly CancellationToken _shutdown;
    readonly Func<string?> _readVersion;
    readonly IScheduler _readScheduler;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _read = new();
    string? _installed;
    ServerLaneState? _lane;
    LocalUpgradeOutcome? _shown;
    string? _startMessage;
    int _starting;
    readonly ObservableAsPropertyHelper<bool> _isStarting;

    /// <param name="readVersion">The version the installation registry names; a disk read, run on
    /// <paramref name="readScheduler"/> (the task pool by default), never on the UI thread.</param>
    /// <param name="searchChange">The search change that shares the installation with the upgrade, when there is one.</param>
    public LocalStackViewModel(
            Func<string?> readVersion, IObservable<ServerLaneStatus> status, LocalUpgradeController upgrade,
            Func<CancellationToken, Task<ProcessResult>> start, Func<Task> reconnect, CancellationToken shutdown,
            IScheduler? readScheduler = null, LocalSearchController? searchChange = null) {
        _upgrade       = upgrade;
        _searchChange  = searchChange;
        _start         = start;
        _reconnect     = reconnect;
        _shutdown      = shutdown;
        _readVersion   = readVersion;
        _readScheduler = readScheduler ?? RxSchedulers.TaskpoolScheduler;
        _read.DisposeWith(_disposables);

        ReadVersion();

        status
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(s => {
                // Another kcap's change to the stack ends with the server coming back: the version is
                // reread, and an upgrade it held off runs again.
                if (s.State == ServerLaneState.Connected && _lane is not null and not ServerLaneState.Connected) {
                    ReadVersion();
                    if (Waiting) _ = _upgrade.StartAsync(_shutdown);
                }
                _lane = s.State;
                Refresh();
            })
            .DisposeWith(_disposables);

        Observable.Switch(upgrade.WhenAnyValue(x => x.Progress)
                .Select(p => p.Changed.Select(_ => Unit.Default).StartWith(Unit.Default)))
            .Merge(upgrade.WhenAnyValue(x => x.Running).Select(_ => Unit.Default))
            .Subscribe(_ => Refresh())
            .DisposeWith(_disposables);

        upgrade.Settled += OnSettled;
        Disposable.Create(() => upgrade.Settled -= OnSettled).DisposeWith(_disposables);

        if (searchChange is not null) {
            searchChange.WhenAnyValue(x => x.Running).Subscribe(_ => Refresh()).DisposeWith(_disposables);
            // An upgrade held off by the search change runs once the change is over.
            void OnSearchSettled(LocalInstallOutcome outcome) {
                if (_shown is LocalUpgradeOutcome.Upgraded { SearchWarning: not null }
                    && outcome is LocalInstallOutcome.Installed && searchChange.Progress.Embeddings.Configured) {
                    _shown = null;
                    Refresh();
                }
                if (Waiting) _ = _upgrade.StartAsync(_shutdown);
            }
            searchChange.Settled += OnSearchSettled;
            Disposable.Create(() => searchChange.Settled -= OnSearchSettled).DisposeWith(_disposables);
        }

        TryAgainCommand = ReactiveCommand.CreateFromTask(() => _upgrade.StartAsync(shutdown));
        StartCommand    = ReactiveCommand.CreateFromTask(RunStartAsync);
        _isStarting = StartCommand.IsExecuting
            .ToProperty(this, x => x.IsStarting, initialValue: false)
            .DisposeWith(_disposables);
        DismissCommand  = ReactiveCommand.Create(() => {
            if (!DismissVisible) return;
            _shown = null;
            Refresh();
        });
    }

    public LocalSearchViewModel? Search { get; init; }

    LocalUpgradeProgress Progress => _upgrade.Progress;

    public bool Upgrading => _upgrade.Running;

    /// Another kcap held the installation; not a failure of the stack.
    bool Waiting => !Upgrading && _shown is LocalUpgradeOutcome.Failed { Switched: false, Affordance: LocalInstallAffordance.Busy };

    bool Failed => !Upgrading && _shown is LocalUpgradeOutcome.Failed && !Waiting;

    public string? Version => (Upgrading ? Progress.SwitchedTo : null) ?? _installed;

    public string? StateText =>
        Upgrading ? "upgrading"
        : Failed ? "upgrade failed"
        : _lane switch {
            ServerLaneState.Connected  => "running",
            ServerLaneState.Retrying   => "not running",
            ServerLaneState.Connecting => "connecting",
            _                          => null,
        };

    /// A failed upgrade uses the rail's failure treatment.
    public bool Attention => Failed;

    public bool BannerVisible => Upgrading || _shown is not null;

    /// A non-blocking result: publication or another local operation is pending, or search fell back to keywords.
    public bool IsNote => !Upgrading && (_shown is LocalUpgradeOutcome.NotPublished or LocalUpgradeOutcome.Upgraded { SearchWarning: not null } || Waiting);

    public string? Headline => Upgrading
        ? (Progress.From, Progress.To) switch {
            ({ } from, { } to) => $"Upgrading kcap local {from} to {to}",
            (_, { } to)        => $"Upgrading kcap local to {to}",
            _                  => "Upgrading kcap local",
        }
        : Waiting ? Progress.To is { } waitingFor
            ? $"The upgrade to kcap local {waitingFor} is waiting"
            : "The upgrade of kcap local is waiting"
        : _shown switch {
            LocalUpgradeOutcome.Upgraded => "The local server is upgraded; search needs attention",
            LocalUpgradeOutcome.Failed { Switched: true } f => $"kcap local {f.To ?? Version} did not start after the upgrade",
            LocalUpgradeOutcome.Failed { To: { } to }       => $"kcap local could not be upgraded to {to}",
            LocalUpgradeOutcome.Failed                      => "kcap local could not be upgraded",
            LocalUpgradeOutcome.NotPublished                => Progress.To is { } to
                ? $"kcap local {to} is not available yet"
                : "This version of kcap local is not available yet",
            _ => null,
        };

    public string? Detail => Upgrading
        ? Progress.Phase switch {
            LocalUpgradePhase.Recovering  => "Finishing an earlier change that was interrupted…",
            LocalUpgradePhase.Downloading => DownloadFraction is { } f
                ? $"Downloading {Progress.To}… {(int)Math.Round(f * 100)}%"
                : $"Downloading {Progress.To}…",
            LocalUpgradePhase.Verifying => "Verifying the local server download…",
            LocalUpgradePhase.Extracting => "Extracting the local server…",
            LocalUpgradePhase.DownloadingModel => Progress.Embeddings.PullText ?? "Preparing the search model…",
            LocalUpgradePhase.Stopping    => "Stopping the local server…",
            LocalUpgradePhase.Starting    => $"Starting {Progress.To}. A database migration can take a few minutes.",
            LocalUpgradePhase.Restarting  => $"Starting {Progress.From} again…",
            _                             => "Checking the local installation…",
        }
        : Waiting ? BusyDetail
        : _shown switch {
            LocalUpgradeOutcome.Upgraded => "The search model could not be updated. Search works by keyword. Open Settings → Search to retry.",
            LocalUpgradeOutcome.Failed f => TerminalAdvice.Strip(f.Message) ?? "The upgrade stopped before it finished.",
            LocalUpgradeOutcome.NotPublished when Progress.From is { } from => $"The local server stays at {from}.",
            LocalUpgradeOutcome.NotPublished => "Try again later.",
            _ => null,
        };

    public string? Guidance => StartVisible ? StartGuidance : null;

    public double? DownloadFraction => Upgrading && Progress.Phase is LocalUpgradePhase.Downloading or LocalUpgradePhase.DownloadingModel ? Progress.DownloadFraction : null;

    public bool ProgressIndeterminate => Upgrading && DownloadFraction is null;

    bool SearchChanging => _searchChange?.Running == true;

    public bool TryAgainVisible => !Upgrading && !SearchChanging && _shown switch {
        LocalUpgradeOutcome.NotPublished => true,
        LocalUpgradeOutcome.Failed { Switched: false, Affordance: not LocalInstallAffordance.Permanent } => true,
        _ => false,
    };

    /// Not for a failure after the switch: its Start is the only way to finish a journaled upgrade.
    public bool DismissVisible => !Upgrading && _shown is not null && !StartVisible;

    public bool StartVisible => !Upgrading && _shown is LocalUpgradeOutcome.Failed { Switched: true };

    /// The action cluster. False while an upgrade is running, so that row is not left blank.
    public bool ActionsVisible => StartVisible || TryAgainVisible || DismissVisible;

    /// While this banner or a search change owns the stack, the "not running" banner's own Start stays hidden: a search
    /// change can restart the server.
    public bool SuppressesServerStart => Upgrading || StartVisible || SearchChanging;

    public bool IsStarting => _isStarting.Value;

    /// Why the last Start failed, without its terminal advice; null once a start succeeds or a new one begins.
    public string? StartMessage {
        get => _startMessage;
        private set => this.RaiseAndSetIfChanged(ref _startMessage, value);
    }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    public ReactiveCommand<Unit, Unit> TryAgainCommand { get; }

    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    void OnSettled(LocalUpgradeOutcome outcome) {
        // A confirmed switch holds until the registry read lands.
        _installed = Progress.SwitchedTo ?? _installed;
        _shown = outcome is LocalUpgradeOutcome.Failed or LocalUpgradeOutcome.NotPublished or LocalUpgradeOutcome.Upgraded { SearchWarning: not null } ? outcome : null;
        StartMessage = null;
        if (outcome is LocalUpgradeOutcome.Upgraded) _ = ReconnectAsync();
        ReadVersion();
        Refresh();
    }

    async Task RunStartAsync() {
        // ReactiveCommand's executing gate only disables a bound button; ICommand.Execute still runs.
        if (Interlocked.Exchange(ref _starting, 1) == 1) return;
        try {
            StartMessage = null;
            ProcessResult result;
            try {
                result = await _start(_shutdown);
            } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                StartMessage = ex.Message;
                return;
            }

            if (result is { ExitCode: 0, TimedOut: false }) {
                _shown = null;
                ReadVersion();
                Refresh();
                await ReconnectAsync();
                return;
            }

            var tail = string.Join("\n", (result.Stdout + "\n" + result.Stderr)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(StartMessageTailLines));
            StartMessage = TerminalAdvice.Strip(tail)
                ?? (result.TimedOut ? "The local server did not start in time." : $"The local server did not start (exit {result.ExitCode}).");
        } finally {
            Volatile.Write(ref _starting, 0);
        }
    }

    void ReadVersion() =>
        _read.Disposable = Observable.Start(_readVersion, _readScheduler)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v => {
                _installed = v;
                Refresh();
            }, ex => Console.Error.WriteLine($"kcap app: reading the local installation's version failed: {ex.Message}"));

    // The lane would find the server on its own backoff; this skips the wait.
    async Task ReconnectAsync() {
        try {
            await _reconnect();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: reconnecting to the local server failed: {ex.Message}");
        }
    }

    void Refresh() {
        foreach (var name in Derived) this.RaisePropertyChanged(name);
    }

    public void Dispose() {
        Search?.Dispose();
        _disposables.Dispose();
    }
}
