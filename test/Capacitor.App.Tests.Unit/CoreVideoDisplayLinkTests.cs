using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class CoreVideoDisplayLinkTests {
    /// Pins the CoreVideo binding: a wrong library path or entry point reports null here. The code
    /// itself is whatever the host allows, -6661 on a runner with no lit display.
    [Test]
    public async Task Probe_resolves_on_a_mac() {
        if (!OperatingSystem.IsMacOS()) return;

        await Assert.That(CoreVideoDisplayLink.Probe()).IsNotNull();
    }
}
