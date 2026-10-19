using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// The local stack beside the daemon in the main window: its version and state in the rail, and a
/// launcher banner for an upgrade in flight or one that did not finish. Start for a stopped server
/// stays with <see cref="LocalServerRecoveryViewModel"/>; this banner says why and what comes next.
/// </summary>
public sealed class LocalStackViewModel : ReactiveObject, IDisposable {
    internal const string BusyGuidance  = "Another local setup, upgrade or uninstall is running on this Mac. Try again once it finishes.";
    internal const string StartGuidance = "Its data is kept. Start the local server to try again.";
    public const string NameTip         = "The local Capacitor server on this machine";
    public const string VersionTip      = "Version of Capacitor local on this machine";

    static readonly string[] Derived = [
        nameof(Version), nameof(StateText), nameof(Attention), nameof(Upgrading), nameof(BannerVisible), nameof(Headline),
        nameof(Detail), nameof(Guidance), nameof(DownloadFraction), nameof(ProgressIndeterminate), nameof(TryAgainVisible),
        nameof(DismissVisible), nameof(IsNote),
    ];

    readonly LocalUpgradeController _upgrade;
    readonly Func<Task> _reconnect;
    readonly Func<string?> _readVersion;
    readonly IScheduler _readScheduler;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _read = new();
    string? _installed;
    ServerLaneState? _lane;
    LocalUpgradeOutcome? _shown;

    /// <param name="readVersion">The version the installation registry names; a disk read, run on
    /// <paramref name="readScheduler"/> (the task pool by default), never on the UI thread.</param>
    public LocalStackViewModel(
            Func<string?> readVersion, IObservable<ServerLaneStatus> status, LocalUpgradeController upgrade,
            Func<Task> reconnect, CancellationToken shutdown, IScheduler? readScheduler = null) {
        _upgrade       = upgrade;
        _reconnect     = reconnect;
        _readVersion   = readVersion;
        _readScheduler = readScheduler ?? RxSchedulers.TaskpoolScheduler;
        _read.DisposeWith(_disposables);

        ReadVersion();

        status
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(s => {
                // A version changed outside the app restarts the server, so a reconnect rereads it.
                if (s.State == ServerLaneState.Connected && _lane is not null and not ServerLaneState.Connected) ReadVersion();
                _lane = s.State;
                if (s.State == ServerLaneState.Connected && _shown is LocalUpgradeOutcome.Failed { Switched: true }) _shown = null;
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

        TryAgainCommand = ReactiveCommand.CreateFromTask(() => _upgrade.StartAsync(shutdown));
        DismissCommand  = ReactiveCommand.Create(() => {
            _shown = null;
            Refresh();
        });
    }

    LocalUpgradeProgress Progress => _upgrade.Progress;

    public bool Upgrading => _upgrade.Running;

    public string? Version => (Upgrading ? Progress.SwitchedTo : null) ?? _installed;

    public string? StateText =>
        Upgrading ? "upgrading"
        : _shown is LocalUpgradeOutcome.Failed ? "upgrade failed"
        : _lane switch {
            ServerLaneState.Connected  => "running",
            ServerLaneState.Retrying   => "not running",
            ServerLaneState.Connecting => "connecting",
            _                          => null,
        };

    /// A failed upgrade waits on the user; the rail's state then takes the warning colour.
    public bool Attention => !Upgrading && _shown is LocalUpgradeOutcome.Failed;

    public bool BannerVisible => Upgrading || _shown is not null;

    /// A settled banner that needs nothing from the user, such as a bundle not published yet.
    public bool IsNote => !Upgrading && _shown is LocalUpgradeOutcome.NotPublished;

    public string? Headline => Upgrading
        ? (Progress.From, Progress.To) switch {
            ({ } from, { } to) => $"Upgrading Capacitor local {from} to {to}",
            (_, { } to)        => $"Upgrading Capacitor local to {to}",
            _                  => "Upgrading Capacitor local",
        }
        : _shown switch {
            LocalUpgradeOutcome.Failed { Switched: true } f => $"Capacitor local {f.To ?? Version} did not start after the upgrade",
            LocalUpgradeOutcome.Failed { To: { } to }       => $"Capacitor local could not be upgraded to {to}",
            LocalUpgradeOutcome.Failed                      => "Capacitor local could not be upgraded",
            LocalUpgradeOutcome.NotPublished                => Progress.To is { } to
                ? $"Capacitor local {to} is not available yet"
                : "This version of Capacitor local is not available yet",
            _ => null,
        };

    public string? Detail => Upgrading
        ? Progress.Phase switch {
            LocalUpgradePhase.Recovering  => "Finishing an earlier change that was interrupted…",
            LocalUpgradePhase.Downloading => DownloadFraction is { } f
                ? $"Downloading {Progress.To}… {(int)Math.Round(f * 100)}%"
                : $"Downloading {Progress.To}…",
            LocalUpgradePhase.Stopping    => "Stopping the local server…",
            LocalUpgradePhase.Starting    => $"Starting {Progress.To}. A database migration can take a few minutes.",
            LocalUpgradePhase.Restarting  => $"Starting {Progress.From} again…",
            _                             => "Checking the local installation…",
        }
        : _shown switch {
            LocalUpgradeOutcome.Failed f => TerminalAdvice.Strip(f.Message),
            LocalUpgradeOutcome.NotPublished when Progress.From is { } from => $"The local server stays at {from}.",
            _ => null,
        };

    public string? Guidance => Upgrading ? null : _shown switch {
        LocalUpgradeOutcome.Failed { Switched: true }                          => StartGuidance,
        LocalUpgradeOutcome.Failed { Affordance: LocalInstallAffordance.Busy } => BusyGuidance,
        _                                                                      => null,
    };

    public double? DownloadFraction => Upgrading && Progress.Phase == LocalUpgradePhase.Downloading ? Progress.DownloadFraction : null;

    public bool ProgressIndeterminate => Upgrading && DownloadFraction is null;

    public bool TryAgainVisible => !Upgrading && _shown switch {
        LocalUpgradeOutcome.NotPublished => true,
        LocalUpgradeOutcome.Failed { Switched: false, Affordance: not LocalInstallAffordance.Permanent } => true,
        _ => false,
    };

    public bool DismissVisible => !Upgrading && _shown is not null;

    public ReactiveCommand<Unit, Unit> TryAgainCommand { get; }

    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    void OnSettled(LocalUpgradeOutcome outcome) {
        // A confirmed switch holds until the registry read lands.
        _installed = Progress.SwitchedTo ?? _installed;
        _shown = outcome is LocalUpgradeOutcome.Failed or LocalUpgradeOutcome.NotPublished ? outcome : null;
        if (outcome is LocalUpgradeOutcome.Upgraded) _ = ReconnectAsync();
        ReadVersion();
        Refresh();
    }

    void ReadVersion() =>
        _read.Disposable = Observable.Start(_readVersion, _readScheduler)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v => {
                _installed = v;
                Refresh();
            }, ex => Console.Error.WriteLine($"kcap app: reading the local installation's version failed: {ex.Message}"));

    // The lane would find the new server on its own backoff; this skips the wait.
    async Task ReconnectAsync() {
        try {
            await _reconnect();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: reconnecting after the local upgrade failed: {ex.Message}");
        }
    }

    void Refresh() {
        foreach (var name in Derived) this.RaisePropertyChanged(name);
    }

    public void Dispose() => _disposables.Dispose();
}
