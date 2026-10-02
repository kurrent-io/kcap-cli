using System.Reactive;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// The Harnesses page's "Fix it on this machine": puts kcap on the login shell's PATH. Reuses
/// PathShimInstaller as-is (AppleScript sudo, non-forcing symlink, post-install re-probe) and
/// claims ShimOffered so the post-wizard ShimOfferCoordinator never re-offers this machine.
public sealed class PathFixViewModel : ReactiveObject {
    readonly PathShimInstaller _installer;
    readonly IAppStateStore    _store;
    readonly string?           _target;
    readonly string            _destination;

    bool    _offerClaimed;
    bool    _busy;
    bool    _fixed;
    bool    _attempted;
    string? _message;

    public PathFixViewModel(PathShimInstaller installer, IAppStateStore store, string? target)
        : this(installer, store, target, PathShimInstaller.Destination) { }

    // Test seam mirroring ShimOfferCoordinator's own destination-override constructor (real filesystem taxonomy against a temp path, never the real /usr/local/bin/kcap).
    internal PathFixViewModel(PathShimInstaller installer, IAppStateStore store, string? target, string destination) {
        _installer   = installer;
        _store       = store;
        _target      = target;
        _destination = destination;

        InstallCommand = ReactiveCommand.CreateFromTask(RunInstallAsync, this.WhenAnyValue(x => x.Idle));
    }

    /// Pure decision: macOS AND a resolved absolute CLI path AND the login-shell probe positively
    /// found no kcap on the terminal PATH. A null (unknown) probe fails quiet — never offer on an
    /// inconclusive read.
    public static bool ComputeApplicable(bool isMacOs, string? target, bool? kcapOnPath) =>
        isMacOs && target is not null && kcapOnPath == false;

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
        Message = "Look for a password prompt.";
        try {
            await ClaimOfferedOnceAsync().ConfigureAwait(false);
            Apply(await _installer.InstallAsync(_target, _destination, CancellationToken.None).ConfigureAwait(false));
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
                Message = "You dismissed the password prompt, so nothing changed.";
                break;
            default: // Failed
                Message = result.SudoFallback is null
                    ? $"That did not work, and nothing changed. {result.Detail}".TrimEnd()
                    : $"That did not work, and nothing changed. Run: {result.SudoFallback}";
                break;
        }
    }
}
