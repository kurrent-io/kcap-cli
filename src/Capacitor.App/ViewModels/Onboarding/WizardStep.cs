namespace Capacitor.App.ViewModels.Onboarding;

/// Declaration order is display order.
public enum WizardStepId { Welcome, SignIn, Harnesses, Import, Daemon, Done }

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

    bool CanContinue => true;

    Task OnEnterAsync(CancellationToken ct);

    /// False vetoes the navigation and holds the current step.
    Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct);
}
