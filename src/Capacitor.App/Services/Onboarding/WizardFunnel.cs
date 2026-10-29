using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Services.Onboarding;

/// <summary>The desktop wizard's own funnel: the pages it shows, the daemon coming up, and where it closed.
/// The edition, install and sign-in events come from the steps that own them.</summary>
static class WizardFunnel {
    /// <param name="daemon">The enabler's state: whether it is satisfied, and whether that is because the
    /// daemon was already running. Watched past the close, since the enabler finishes after it.</param>
    /// <returns>What to dispose when the wizard closes: the page subscription and the close handler.</returns>
    public static IDisposable Attach(
            OnboardingViewModel wizard, SetupFunnel funnel, IObservable<(bool Satisfied, bool AlreadyRunning)> daemon) {
        void Closed() => funnel.WizardClosed(Name(wizard.Current.Id), wizard.Current.Id == WizardStepId.Done);

        wizard.CloseRequested += Closed;
        daemon.Where(d => d.Satisfied).Take(1).Subscribe(d => funnel.DaemonEnabled(d.AlreadyRunning));

        return new CompositeDisposable(
            wizard.WhenAnyValue(x => x.Current).Select(step => step.Id).DistinctUntilChanged()
                .Subscribe(id => funnel.StepShown(Name(id))),
            Disposable.Create(() => wizard.CloseRequested -= Closed));
    }

    static string Name(WizardStepId id) => id.ToString().ToLowerInvariant();
}
