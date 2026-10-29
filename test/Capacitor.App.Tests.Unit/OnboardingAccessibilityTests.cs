using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;

namespace Capacitor.App.Tests.Unit;

public class OnboardingAccessibilityTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Composite_onboarding_links_expose_their_visible_labels_to_assistive_technology() {
        await AvaloniaSession.DispatchAsync(async () => {
            var done = new DoneStepViewModel(() => DoneFacts.Empty, addAgent: () => { });
            await done.OnEnterAsync(CancellationToken.None);
            var addAgent = new DonePaneView { DataContext = done }.FindControl<Button>("AddAgentButton")!;
            var docs = new HistoryPaneView().FindControl<Button>("RepoSettingsDocsButton")!;

            await Assert.That(ControlAutomationPeer.CreatePeerForElement(addAgent)!.GetName())
                .IsEqualTo("Add an agent");
            await Assert.That(ControlAutomationPeer.CreatePeerForElement(docs)!.GetName())
                .IsEqualTo("Repository settings in the docs");
        });
    }
}
