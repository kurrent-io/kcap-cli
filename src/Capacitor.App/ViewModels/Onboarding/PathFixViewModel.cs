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
            await ClaimOfferedOnceAsync().ConfigureAwait(true);
            Apply(await _installer.InstallAsync(_target, CancellationToken.None).ConfigureAwait(true));
        } catch (Exception ex) {
            Message = $"Could not install the terminal command: {ex.Message}. Try again.";
        } finally {
            _attempted = true;
            this.RaisePropertyChanged(nameof(ActionLabel));
            Busy = false;
        }
    }

    // Claim before installing so a retry cannot trigger another startup offer.
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
                Message = result.Detail ?? "Installed, but your terminal cannot find kcap yet. Open a new terminal and run kcap --version.";
                break;
            case ShimOutcome.Cancelled:
                Message = "Cancelled. Nothing changed.";
                break;
            default: // Failed
                Message = result.SudoFallback is null
                    ? $"Could not finish installing the terminal command. {result.Detail}".TrimEnd()
                    : $"Could not finish installing the terminal command. Run: {result.SudoFallback}";
                break;
        }
    }
}
