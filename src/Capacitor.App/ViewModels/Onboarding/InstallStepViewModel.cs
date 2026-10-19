using System.ComponentModel;
using System.Reactive;
using Capacitor.App.Services.LocalSetup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The local path's install page. The run belongs to <see cref="LocalInstallController"/>, which
/// outlives this page; the page renders its progress and settled outcome and advances by itself
/// once the outcome is Installed.
/// </summary>
public sealed class InstallStepViewModel : ReactiveObject, IWizardStep {
    public const string BusyText   = "Another kcap setup, upgrade or uninstall is running on this Mac.";
    public const string LockedText     = "The install did not finish.";
    public const string NotStartedText = "The local server didn't finish starting. Try again to finish the install.";

    readonly LocalInstallController _controller;
    readonly Func<string?>          _appProfile;
    readonly Func<string?>          _configActiveProfile;
    readonly Func<bool>             _onLocalPath;
    CancellationToken               _lifetime;
    string?                         _refusal;
    IReadOnlyList<InstallStepRow>   _rows = [];
    LocalSetupProgress?             _watched;

    public InstallStepViewModel(
            LocalInstallController controller, Func<string?> appProfile, Func<string?> configActiveProfile,
            Func<bool> onLocalPath) {
        _controller          = controller;
        _appProfile          = appProfile;
        _configActiveProfile = configActiveProfile;
        _onLocalPath         = onLocalPath;

        TryAgainCommand = ReactiveCommand.CreateFromTask(TryAgainAsync);
        CancelCommand   = ReactiveCommand.Create(() => { if (CancelVisible) _controller.Interrupt(); });

        _controller.PropertyChanged += OnControllerChanged;
        _controller.Settled         += OnSettled;
        Rewire(_controller.Progress);
    }

    public WizardStepId Id                => WizardStepId.Install;
    public string       Title             => "Installing Capacitor on this Mac";
    public string       Eyebrow           => "Install";
    public bool         Applicable        => true;
    public bool         Satisfied         => _controller.Outcome is LocalInstallOutcome.Installed;
    public bool         Skippable         => false;
    public bool         OwnsPrimaryAction => true;
    public bool         PassThrough       => !_onLocalPath() || _controller.Outcome is LocalInstallOutcome.Installed;

    public string Lede => "The server runs on this Mac and your sessions stay on it. This usually takes a minute or two.";

    public LocalSetupProgress Progress => _controller.Progress;

    public IReadOnlyList<InstallStepRow> Rows {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    public bool Running => _controller.Running;

    public bool DownloadBarVisible    => Progress.Download.State == RowState.Running;
    public bool DownloadIndeterminate => Progress.DownloadFraction is null;
    public double DownloadPercent     => (Progress.DownloadFraction ?? 0) * 100;

    LocalInstallOutcome.Failed? Failure => _controller.Outcome as LocalInstallOutcome.Failed;

    public bool FailureVisible => Failure is not null && !Running;

    public string? FailureMessage => Failure switch {
        null                                         => null,
        { Affordance: LocalInstallAffordance.Busy }  => BusyText,
        { Affordance: LocalInstallAffordance.Retry } => RetryGuidance(Failure.Code),
        var failed                                   => failed.Message,
    };

    /// The CLI's own words behind a retryable failure. They can name a terminal command, so they never lead.
    public string? FailureDetail => Failure is { Affordance: LocalInstallAffordance.Retry } failed ? failed.Message : null;

    public bool FailureDetailVisible => FailureDetail is not null && !Running;

    public bool LockedVisible   => Failure is { LockedToLocal: true } && !Running;
    public bool TryAgainVisible => Failure is { Affordance: LocalInstallAffordance.Retry } && !Running;
    public bool CancelVisible   => Running;
    public bool BackVisible     => !Running && Failure is not { LockedToLocal: true };

    public bool ProgressVisible => RefusalMessage is null;

    public string? RefusalMessage {
        get => _refusal;
        private set {
            this.RaiseAndSetIfChanged(ref _refusal, value);
            this.RaisePropertyChanged(nameof(RefusalVisible));
            this.RaisePropertyChanged(nameof(ProgressVisible));
        }
    }

    public bool RefusalVisible => RefusalMessage is not null;

    public ReactiveCommand<Unit, Unit> TryAgainCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand   { get; }

    public event Action? AdvanceRequested;

    public async Task OnEnterAsync(CancellationToken ct) {
        _lifetime = ct;

        if (_controller.Outcome is LocalInstallOutcome.Installed) {
            AdvanceRequested?.Invoke();
            return;
        }

        // A settled failure stays on screen until Try again: re-entry never starts a run of its own.
        if (_controller.Running || _controller.Outcome is not null) return;

        // The installer writes the config's active profile; the app reads KCAP_PROFILE first.
        if (_appProfile() is { } app && _configActiveProfile() is { } active && !string.Equals(app, active, StringComparison.Ordinal)) {
            RefusalMessage =
                $"This app is using the profile \"{app}\" (from KCAP_PROFILE), but the install would set up \"{active}\", " +
                $"the active profile in your kcap config. Unset KCAP_PROFILE or make \"{app}\" the active profile, then reopen setup.";
            return;
        }

        RefusalMessage = null;
        await _controller.StartAsync(ct);
    }

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) =>
        Task.FromResult(direction switch {
            WizardNavigation.Back => BackVisible,
            WizardNavigation.Next => _controller.Outcome is LocalInstallOutcome.Installed,
            _                     => false,
        });

    /// Stops following the controller, which outlives this page and the wizard that showed it.
    public void Detach() {
        _controller.PropertyChanged -= OnControllerChanged;
        _controller.Settled         -= OnSettled;
        if (_watched is not null) {
            _watched.PropertyChanged          -= OnProgressChanged;
            _watched.Download.PropertyChanged -= OnProgressChanged;
            _watched = null;
        }
    }

    static string RetryGuidance(string? code) => code switch {
        "download_failed"     => "The download didn't finish. Check your connection and try again.",
        "verification_failed" => "The download didn't pass its signature check. Try again to download it again.",
        "cancelled"           => "The install was cancelled.",
        null                  => "The install stopped before it finished. Try again.",
        _                     => NotStartedText,
    };

    Task TryAgainAsync() => TryAgainVisible ? _controller.RetryAsync(_lifetime) : Task.CompletedTask;

    void OnSettled(LocalInstallOutcome outcome) {
        if (outcome is LocalInstallOutcome.Installed) AdvanceRequested?.Invoke();
    }

    void OnControllerChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(LocalInstallController.Progress):
                Rewire(_controller.Progress);
                this.RaisePropertyChanged(nameof(Progress));
                break;
            case nameof(LocalInstallController.Running) or nameof(LocalInstallController.Outcome):
                RestateOutcome();
                break;
        }
    }

    void RestateOutcome() {
        this.RaisePropertyChanged(nameof(Running));
        this.RaisePropertyChanged(nameof(Satisfied));
        this.RaisePropertyChanged(nameof(FailureVisible));
        this.RaisePropertyChanged(nameof(FailureMessage));
        this.RaisePropertyChanged(nameof(FailureDetail));
        this.RaisePropertyChanged(nameof(FailureDetailVisible));
        this.RaisePropertyChanged(nameof(LockedVisible));
        this.RaisePropertyChanged(nameof(TryAgainVisible));
        this.RaisePropertyChanged(nameof(CancelVisible));
        this.RaisePropertyChanged(nameof(BackVisible));
    }

    void Rewire(LocalSetupProgress next) {
        if (_watched is not null) {
            _watched.PropertyChanged          -= OnProgressChanged;
            _watched.Download.PropertyChanged -= OnProgressChanged;
        }

        _watched = next;
        next.PropertyChanged          += OnProgressChanged;
        next.Download.PropertyChanged += OnProgressChanged;
        Rows = [
            new("Downloading", next.Download),
            new("Verifying the signature", next.Verify),
            new("Installing", next.Install),
            new("First start", next.FirstStart),
        ];
        RestateDownload();
    }

    void OnProgressChanged(object? sender, PropertyChangedEventArgs e) => RestateDownload();

    void RestateDownload() {
        this.RaisePropertyChanged(nameof(DownloadBarVisible));
        this.RaisePropertyChanged(nameof(DownloadIndeterminate));
        this.RaisePropertyChanged(nameof(DownloadPercent));
    }
}
