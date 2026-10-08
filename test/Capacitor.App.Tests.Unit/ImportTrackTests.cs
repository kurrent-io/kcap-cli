using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Capacitor.App.Views.Onboarding;

namespace Capacitor.App.Tests.Unit;

public class ImportTrackTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Assistive_technology_can_read_and_change_history_access_but_not_a_disabled_selection() {
        await AvaloniaSession.DispatchAsync(async () => {
            var track = new ImportTrack { Stop = 0 };
            AutomationProperties.SetName(track, "History access for org/repo");
            var peer = ControlAutomationPeer.CreatePeerForElement(track)!;
            var range = peer.GetProvider<IRangeValueProvider>()!;
            range.SetValue(2);
            await Assert.That(track.Stop).IsEqualTo(2);
            await Assert.That(peer.GetName()).Contains("shared");
            await Assert.That(range.Value).IsEqualTo(2);
            track.IsEnabled = false;
            range.SetValue(0);
            await Assert.That(range.IsReadOnly).IsTrue();
            await Assert.That(track.Stop).IsEqualTo(2);
        });
    }
}
