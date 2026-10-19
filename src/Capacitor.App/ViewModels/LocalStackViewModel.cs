using System.Reactive;
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
    readonly CompositeDisposable _disposables = new();
    string? _installed, _upgradedTo;
    ServerLaneState? _lane;
    LocalUpgradeOutcome? _shown;

    public LocalStackViewModel(
            IObservable<string?> installedVersion, IObservable<ServerLaneStatus> status, LocalUpgradeController upgrade,
            Func<Task> reconnect, CancellationToken shutdown) {
        _upgrade   = upgrade;
        _reconnect = reconnect;

        installedVersion
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v => {
                _installed = v;
                Refresh();
            })
            .DisposeWith(_disposables);

        status
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(s => {
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

    public string? Version => _upgradedTo ?? (Upgrading ? Progress.SwitchedTo : null) ?? _installed;

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
        switch (outcome) {
            case LocalUpgradeOutcome.Upgraded upgraded:
                _upgradedTo = upgraded.To ?? Progress.To;
                _shown      = null;
                _ = ReconnectAsync();
                break;
            case LocalUpgradeOutcome.Failed { Switched: true } failed:
                _upgradedTo = Progress.SwitchedTo ?? failed.To;
                _shown      = outcome;
                break;
            case LocalUpgradeOutcome.Failed or LocalUpgradeOutcome.NotPublished:
                _shown = outcome;
                break;
            default:
                _shown = null;
                break;
        }
        Refresh();
    }

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
