namespace Capacitor.App.ViewModels.Onboarding;

/// Declaration order is display order.
public enum WizardStepId { Edition, Install, SignIn, Harnesses, Intelligence, Import, Privacy, Done }

public enum WizardNavigation { Back, Next, Skip }

/// One wizard page. Applicable is evaluated once, at OnboardingViewModel construction.
public interface IWizardStep {
    WizardStepId Id { get; }

    /// The page headline.
    string Title { get; }

    bool Applicable { get; }
    bool Satisfied { get; }

    /// The short label after "Step n" above the headline.
    string Eyebrow => Title;

    /// The paragraph under the headline, or null for none.
    string? Lede => null;

    /// The primary action's label, or null for the shell's own Next / Get started. A step that
    /// changes it raises PropertyChanged for it.
    string? NextLabel => null;

    string SkipLabel => "Skip";

    bool Skippable => true;

    /// The page carries its own primary action, so the shell shows neither Next nor Skip.
    bool OwnsPrimaryAction => false;

    /// The page is showing its own filled action. The footer Next stays secondary until that action is done.
    bool ShowsOwnPrimary => false;

    /// False hides the shell's Back while this step is current. A step that changes it raises
    /// PropertyChanged for it; CanLeaveAsync still decides whether Back goes anywhere.
    bool BackVisible => true;

    /// Already done with nothing left to ask: forward navigation steps over it.
    bool PassThrough => false;

    /// False hides the shell's Finish later while this step is current. A step that changes it raises
    /// PropertyChanged for it.
    bool OffersFinishLater => true;

    bool CanContinue => true;
    bool CanSkip => true;

    /// The page's own action moves the wizard forward, through the same CanLeaveAsync veto as Next.
    event Action? AdvanceRequested { add { } remove { } }

    Task OnEnterAsync(CancellationToken ct);

    /// False vetoes the navigation and holds the current step.
    Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct);
}
