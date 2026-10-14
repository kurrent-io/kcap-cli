using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Capacitor.App.Views.Onboarding;

namespace Capacitor.App.Tests.Unit;

public class OnboardingAccessibilityTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Composite_onboarding_links_expose_their_visible_labels_to_assistive_technology() {
        await AvaloniaSession.DispatchAsync(async () => {
            var workspace = new DonePaneView().FindControl<Button>("OpenWorkspaceButton")!;
            var docs = new HistoryPaneView().FindControl<Button>("RepoSettingsDocsButton")!;

            await Assert.That(ControlAutomationPeer.CreatePeerForElement(workspace)!.GetName())
                .IsEqualTo("Open your workspace");
            await Assert.That(ControlAutomationPeer.CreatePeerForElement(docs)!.GetName())
                .IsEqualTo("Repository settings in the docs");
        });
    }
}
