using System.Reactive;
using Capacitor.Cli.Core.Telemetry;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The first page: what Capacitor is, and which edition this machine sets up. Cloud and "already
/// have a workspace" both continue to Sign-in. Local stops on this page with a notice: that is
/// where the local install takes over once it exists.
/// </summary>
public sealed class EditionStepViewModel : ReactiveObject, IWizardStep {
    public const string LocalTitle = "For you, locally";
    public const string LocalDetail =
        "Just you, no account. The server runs on this machine and your sessions never leave it. Free.";
    public const string CloudTitle = "For your team, cloud";
    public const string CloudDetail =
        "A Team workspace on kcap.ai that your team shares. Sign in with your work account; Team is free for 60 days.";
    public const string LocalUnavailable =
        "The local install isn't in this version of the app yet. Choose cloud, or finish setup later.";

    readonly SetupFunnel _funnel;
    bool                 _offered;
    EditionChoice?       _choice;

    public EditionStepViewModel(SetupFunnel funnel) {
        _funnel = funnel;

        ChooseLocalCommand = ReactiveCommand.Create(() => Choose(EditionChoice.Local));
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

    public string Lede =>
        "Capacitor records the sessions your coding agents run on this machine and gives them tools to search " +
        "that history, so what you already worked out is there the next time you need it.";

    public EditionChoice? Choice {
        get => _choice;
        private set {
            this.RaiseAndSetIfChanged(ref _choice, value);
            this.RaisePropertyChanged(nameof(LocalPending));
        }
    }

    public bool LocalPending => Choice == EditionChoice.Local;

    public ReactiveCommand<Unit, Unit> ChooseLocalCommand { get; }
    public ReactiveCommand<Unit, Unit> ChooseCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand      { get; }

    public event Action? AdvanceRequested;

    public Task OnEnterAsync(CancellationToken ct) {
        if (!_offered) {
            _offered = true;
            _funnel.EditionOffered();
        }

        return Task.CompletedTask;
    }

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) =>
        Task.FromResult(direction != WizardNavigation.Next || Choice is EditionChoice.Cloud or EditionChoice.ExistingWorkspace);

    void Choose(EditionChoice choice) {
        Choice = choice;
        _funnel.EditionChosen(choice.Wire());

        if (choice != EditionChoice.Local) AdvanceRequested?.Invoke();
    }
}
