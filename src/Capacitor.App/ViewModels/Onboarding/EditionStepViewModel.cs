using System.Reactive;
using System.Reactive.Linq;
using Capacitor.Cli.Core.Telemetry;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The first page: what Capacitor is, and which edition this machine sets up. Cloud and "already
/// have a workspace" continue to Sign-in, Local to Install.
/// </summary>
public sealed class EditionStepViewModel : ReactiveObject, IWizardStep {
    public const string LocalTitle = "For you, locally";
    public const string LocalDetail =
        "For a Mac one person uses, no account. The server is downloaded to this Mac and runs there, so your sessions " +
        "never leave it. Free.";
    public const string CloudTitle = "For your team, cloud";
    public const string CloudDetail =
        "A Team workspace on kcap.ai that your team shares. Sign in with your work account; Team is free for 60 days.";
    public const string UnsupportedPlatform = "The local edition isn't available on this platform yet.";
    public const string CloudProfilePresent =
        "This Mac is set up for a cloud workspace. Moving it to local comes in a later release.";

    readonly SetupFunnel   _funnel;
    readonly Func<string?> _localUnavailableReason;
    readonly Func<bool>    _locked;
    bool                   _offered;
    EditionChoice?         _choice;
    string?                _unavailable;
    string?                _notice;

    /// <param name="localUnavailableReason">Why Local cannot be chosen, or null when it can.</param>
    /// <param name="locked">True once this machine is committed to the local edition, so the page has nothing to ask.</param>
    public EditionStepViewModel(SetupFunnel funnel, Func<string?> localUnavailableReason, Func<bool>? locked = null) {
        _funnel                 = funnel;
        _localUnavailableReason = localUnavailableReason;
        _locked                 = locked ?? (() => false);
        _unavailable            = localUnavailableReason();

        ChooseLocalCommand = ReactiveCommand.Create(
            () => Choose(EditionChoice.Local), this.WhenAnyValue(x => x.LocalUnavailableReason).Select(r => r is null));
        ChooseCloudCommand = ReactiveCommand.Create(() => Choose(EditionChoice.Cloud));
        SignInCommand      = ReactiveCommand.Create(() => Choose(EditionChoice.ExistingWorkspace));
    }

    public WizardStepId Id                => WizardStepId.Edition;
    public string       Title             => "Your coding agents, with a memory";
    public string       Eyebrow           => "Get started";
    public bool         Applicable        => true;
    public bool         Satisfied         => true;
    public bool         Skippable         => false;
    public bool         OwnsPrimaryAction => true;
    public bool         PassThrough       => _locked();

    public string Lede =>
        "Capacitor records the sessions your coding agents run on this machine and gives them tools to search " +
        "that history, so what you already worked out is there the next time you need it.";

    public EditionChoice? Choice {
        get => _choice;
        private set => this.RaiseAndSetIfChanged(ref _choice, value);
    }

    public string? LocalUnavailableReason {
        get => _unavailable;
        private set {
            this.RaiseAndSetIfChanged(ref _unavailable, value);
            this.RaisePropertyChanged(nameof(LocalUnavailableVisible));
        }
    }

    public bool LocalUnavailableVisible => LocalUnavailableReason is not null;

    /// A line from a later step that sent the user back here; the next choice clears it.
    public string? Notice {
        get => _notice;
        private set {
            this.RaiseAndSetIfChanged(ref _notice, value);
            this.RaisePropertyChanged(nameof(NoticeVisible));
        }
    }

    public bool NoticeVisible => Notice is not null;

    internal void ShowNotice(string notice) => Notice = notice;

    public ReactiveCommand<Unit, Unit> ChooseLocalCommand { get; }
    public ReactiveCommand<Unit, Unit> ChooseCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand      { get; }

    public event Action? AdvanceRequested;

    public Task OnEnterAsync(CancellationToken ct) {
        LocalUnavailableReason = _localUnavailableReason();

        if (!_offered) {
            _offered = true;
            _funnel.EditionOffered();
        }

        return Task.CompletedTask;
    }

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);

    void Choose(EditionChoice choice) {
        Notice = null;
        Choice = choice;
        _funnel.EditionChosen(choice.Wire());
        AdvanceRequested?.Invoke();
    }
}
