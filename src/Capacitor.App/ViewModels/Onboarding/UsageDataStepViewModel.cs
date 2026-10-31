using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// The cloud edition's page discloses usage data with Share already picked. The local edition sends nothing until
/// someone chooses, so neither answer is picked and Continue waits for one.
/// An opt-out that couldn't be saved leaves the notice due and offers no second choice: this process has
/// already stopped sending, so a Share picked afterwards could not take effect.
public sealed class UsageDataStepViewModel : ReactiveObject, IWizardStep {
    public const string ShareTitle  = "Share usage data";
    public const string ShareDetail = "Shows us which parts of kcap get used and where setup gets stuck.";
    public const string OffTitle    = "Don't share";
    public const string OffDetail   = "Nothing is sent, from this app or the kcap command. You can change this in Settings → Privacy.";

    public const string LocalLede =
        "Your sessions stay on this machine whichever you choose. Usage data is separate: pseudonymous events about which "
      + "screens and features you use and how setup goes, never file paths, prompts or transcript content. kcap receives it "
      + "only if you share it.";

    public const string LocalNotSaved = "The choice could not be saved. Nothing is sent, and kcap asks again next time.";

    readonly UsageDataNotice? _notice;
    bool?   _choice;
    bool    _unsavedShown;
    string? _problem;

    public UsageDataStepViewModel(UsageDataNotice? notice) {
        _notice    = notice;
        Applicable = notice is { Visible: true } || notice?.ChoicePending == true;
    }

    public WizardStepId Id         => WizardStepId.Privacy;
    public string       Title      => "Help improve kcap";
    public string       Eyebrow    => "Privacy";
    public string       Lede       => Asking ? LocalLede : UsageDataNotice.Text;
    public bool         Applicable { get; }
    public bool         Satisfied  => !Asking && _notice is not { Visible: true };
    public bool         Skippable  => false;
    public string       NextLabel  => "Continue";
    public bool         CanContinue => !Asking || _choice is not null || _unsavedShown;

    /// Acknowledged or chosen elsewhere since the run began: nothing left to ask.
    public bool PassThrough => Satisfied;

    /// The local edition's explicit choice rather than the cloud edition's disclosure.
    public bool Asking => _notice?.ChoicePending == true;

    /// False when an environment variable decides, so the page only discloses, and after an unsaved choice,
    /// whose message the choice would contradict.
    public bool ChoiceVisible => _notice?.CanTurnOff == true && !_unsavedShown;

    public bool Share {
        get => _choice ?? !Asking;
        set {
            if (value) Choose(true);
            else if (_choice == true) Choose(null);
        }
    }

    public bool DontShare {
        get => _choice == false;
        set {
            if (value) Choose(false);
            else if (_choice == false) Choose(null);
        }
    }

    public string? Problem => _problem;

    public Task OnEnterAsync(CancellationToken ct) {
        // The edition chosen since construction decides which page this is.
        this.RaisePropertyChanged(nameof(Asking));
        this.RaisePropertyChanged(nameof(Lede));
        this.RaisePropertyChanged(nameof(Share));
        this.RaisePropertyChanged(nameof(DontShare));
        this.RaisePropertyChanged(nameof(CanContinue));

        return Task.CompletedTask;
    }

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction == WizardNavigation.Back || _unsavedShown || _notice is null) return Task.FromResult(true);

        if (Asking) {
            if (_choice is not { } share) return Task.FromResult(false);

            return Task.FromResult((share ? _notice.TurnOn() : _notice.TurnOff()) || Unsaved(LocalNotSaved));
        }

        if (!_notice.Visible) return Task.FromResult(true);

        if (Share || !_notice.CanTurnOff) {
            _notice.Acknowledge();
            return Task.FromResult(true);
        }

        return Task.FromResult(_notice.TurnOff() || Unsaved(UsageDataNotice.NotSaved));
    }

    void Choose(bool? choice) {
        if (_choice == choice) return;

        _choice = choice;
        this.RaisePropertyChanged(nameof(Share));
        this.RaisePropertyChanged(nameof(DontShare));
        this.RaisePropertyChanged(nameof(CanContinue));
    }

    /// Holds the page once to say so; the next Continue goes on.
    bool Unsaved(string problem) {
        _unsavedShown = true;
        _problem      = problem;
        this.RaisePropertyChanged(nameof(Problem));
        this.RaisePropertyChanged(nameof(ChoiceVisible));
        this.RaisePropertyChanged(nameof(CanContinue));

        return false;
    }
}
