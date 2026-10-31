using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Tests.Unit;

public class EditionPreselectSmokeTests {
    /// Enter on the page takes the preselected card, so it has to hold focus, not only look selected.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task The_preselected_local_card_is_marked_and_focused(bool lead) {
        var (marked, focused) = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(CliTelemetry.Disabled(TimeProvider.System).Funnel, () => null, leadWithLocal: () => lead);
            var vm = new OnboardingViewModel([edition, new FakeWizardStep(WizardStepId.Install)]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var local = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "LocalEditionButton");
            var result = (local.Classes.Contains("preselected"), local.IsFocused);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(marked).IsEqualTo(lead);
        await Assert.That(focused).IsEqualTo(lead);
    }
}
