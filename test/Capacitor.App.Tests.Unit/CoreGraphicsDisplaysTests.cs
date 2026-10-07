using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class CoreGraphicsDisplaysTests {
    /// Pins the CoreGraphics binding: a wrong library path or entry point throws here. The count
    /// itself is whatever the host has, zero on a runner with no lit display.
    [Test]
    public async Task Active_count_resolves_and_is_never_negative_on_a_mac() {
        if (!OperatingSystem.IsMacOS()) return;

        await Assert.That(CoreGraphicsDisplays.ActiveCount()).IsGreaterThanOrEqualTo(0);
    }
}
