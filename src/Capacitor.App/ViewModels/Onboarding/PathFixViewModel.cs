using System.Reactive;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

public sealed class PathFixViewModel : ReactiveObject {
    readonly ICliPathInstaller _installer;
    readonly IAppStateStore    _store;
    readonly string?           _target;

    bool    _offerClaimed;
    bool    _busy;
    bool    _fixed;
    bool    _attempted;
    string? _message;

    public PathFixViewModel(ICliPathInstaller installer, IAppStateStore store, string? target) {
        _installer   = installer;
        _store       = store;
        _target      = target;

        InstallCommand = ReactiveCommand.CreateFromTask(RunInstallAsync, this.WhenAnyValue(x => x.Idle));
    }

    // An inconclusive PATH probe never offers an install.
    public static bool ComputeApplicable(bool hasInstaller, string? target, bool? kcapOnPath) =>
        hasInstaller && target is not null && kcapOnPath == false;

    public string Disclosure => _installer.Disclosure;

    /// The login shell finds kcap now.
    public bool Fixed {
        get => _fixed;
        private set => this.RaiseAndSetIfChanged(ref _fixed, value);
    }

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(Idle));
        }
    }

    public bool Idle => !Busy;

    public string ActionLabel => _attempted ? "Try again" : "Fix it on this machine";

    /// What the last attempt did, or null before one; set while the admin prompt is up too.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public ReactiveCommand<Unit, Unit> InstallCommand { get; }

    async Task RunInstallAsync() {
        if (_target is null) {
            Message = "This machine could not find its own kcap, so nothing changed.";
            return;
        }

        Busy    = true;
        Message = Disclosure;
        try {
            await ClaimOfferedOnceAsync().ConfigureAwait(false);
            Apply(await _installer.InstallAsync(_target, CancellationToken.None).ConfigureAwait(false));
        } finally {
            _attempted = true;
            this.RaisePropertyChanged(nameof(ActionLabel));
            Busy = false;
        }
    }

    // Claim-before-install (mirrors ShimOfferCoordinator): persisted once, before the outcome is known, so a retry click never re-persists.
    Task ClaimOfferedOnceAsync() {
        if (_offerClaimed) return Task.CompletedTask;
        _offerClaimed = true;
        return _store.UpdateAsync(s => s.ShimOffered ? s : s with { ShimOffered = true });
    }

    void Apply(ShimResult result) {
        switch (result.Outcome) {
            case ShimOutcome.Installed:
                Fixed   = true;
                Message = null;
                break;
            case ShimOutcome.InstalledButNotOnPath:
                Message = $"Linked it, but your login shell still does not look there. {result.Detail}".TrimEnd();
                break;
            case ShimOutcome.Cancelled:
                Message = "Cancelled. Nothing changed.";
                break;
            default: // Failed
                Message = result.SudoFallback is null
                    ? $"That did not work, and nothing changed. {result.Detail}".TrimEnd()
                    : $"That did not work, and nothing changed. Run: {result.SudoFallback}";
                break;
        }
    }
}
