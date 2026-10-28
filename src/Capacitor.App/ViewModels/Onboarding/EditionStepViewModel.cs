using System.Reactive;
using System.Reactive.Linq;
using Capacitor.Cli.Core.Telemetry;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The first page: which edition this machine sets up. Cloud and "already have a workspace" continue
/// to Sign-in, Local to Install.
/// </summary>
public sealed class EditionStepViewModel : ReactiveObject, IWizardStep {
    public const string LocalTitle = "Create a local workspace";
    public const string LocalDetail =
        "Keep your sessions on this machine for your own use. The local edition is free. No account is needed.";
    public const string CloudTitle = "Create a cloud workspace";
    public const string CloudDetail =
        "Share sessions with your team in a workspace hosted on kcap.ai. Start with a 60-day free trial of the Team plan.";
    public const string ExistingTitle = "Sign in to a workspace";
    public const string ExistingDetail =
        "Connect to a workspace you created or one your team invited you to.";
    public const string UnsupportedPlatform = "The local edition isn't available on this platform yet.";
    public const string CloudProfilePresent =
        "This machine is set up for a cloud workspace. Switching to local setup isn't supported yet.";

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
    public string       Title             => "Set up kcap";
    public string       Eyebrow           => "Get started";
    public bool         Applicable        => true;
    public bool         Satisfied         => true;
    public bool         Skippable         => false;
    public bool         OwnsPrimaryAction => true;
    public bool         PassThrough       => _locked();

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
