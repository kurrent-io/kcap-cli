using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Services.Onboarding;

/// <summary>The desktop wizard's own funnel: the pages it shows, the daemon coming up, and where it closed.
/// The edition, install and sign-in events come from the steps that own them.</summary>
static class WizardFunnel {
    public static IDisposable Attach(OnboardingViewModel wizard, SetupFunnel funnel, IObservable<bool> daemonEnabled) {
        void Closed() => funnel.WizardClosed(Name(wizard.Current.Id), wizard.Current.Id == WizardStepId.Done);

        wizard.CloseRequested += Closed;

        return new CompositeDisposable(
            wizard.WhenAnyValue(x => x.Current).Select(step => step.Id).DistinctUntilChanged()
                .Subscribe(id => funnel.StepShown(Name(id))),
            daemonEnabled.Where(enabled => enabled).Take(1).Subscribe(_ => funnel.DaemonEnabled()),
            Disposable.Create(() => wizard.CloseRequested -= Closed));
    }

    static string Name(WizardStepId id) => id.ToString().ToLowerInvariant();
}
