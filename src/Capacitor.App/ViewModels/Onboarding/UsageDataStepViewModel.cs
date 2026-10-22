using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// An opt-out that couldn't be saved leaves the notice due and offers no second choice: this process has
/// already stopped sending, so a Share picked afterwards could not take effect.
public sealed class UsageDataStepViewModel : ReactiveObject, IWizardStep {
    public const string ShareTitle  = "Share usage data";
    public const string ShareDetail = "Shows us which parts of kcap get used and where setup gets stuck.";
    public const string OffTitle    = "Don't share";
    public const string OffDetail   = "Nothing is sent, from this app or the kcap command. You can change this in Settings → Privacy.";

    readonly UsageDataNotice? _notice;
    bool _share = true;
    bool _unsavedShown;

    public UsageDataStepViewModel(UsageDataNotice? notice) {
        _notice    = notice;
        Applicable = notice is { Visible: true };
    }

    public WizardStepId Id         => WizardStepId.Privacy;
    public string       Title      => "Help improve kcap";
    public string       Eyebrow    => "Privacy";
    public string       Lede       => UsageDataNotice.Text;
    public bool         Applicable { get; }
    public bool         Satisfied  => _notice is not { Visible: true };
    public bool         Skippable  => false;
    public string       NextLabel  => "Continue";

    /// Acknowledged elsewhere since the run began: nothing left to ask.
    public bool PassThrough => Satisfied;

    /// False when an environment variable decides, so the page only discloses, and after an unsaved opt-out,
    /// whose message the choice would contradict.
    public bool ChoiceVisible => _notice?.CanTurnOff == true && !_unsavedShown;

    public bool Share {
        get => _share;
        set {
            this.RaiseAndSetIfChanged(ref _share, value);
            this.RaisePropertyChanged(nameof(DontShare));
        }
    }

    public bool DontShare {
        get => !_share;
        set => Share = !value;
    }

    public string? Problem => _unsavedShown ? UsageDataNotice.NotSaved : null;

    public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction == WizardNavigation.Back || _unsavedShown || _notice is not { Visible: true }) return Task.FromResult(true);

        if (_share || !_notice.CanTurnOff) {
            _notice.Acknowledge();
            return Task.FromResult(true);
        }

        if (_notice.TurnOff()) return Task.FromResult(true);

        _unsavedShown = true;
        this.RaisePropertyChanged(nameof(Problem));
        this.RaisePropertyChanged(nameof(ChoiceVisible));

        return Task.FromResult(false);
    }
}
