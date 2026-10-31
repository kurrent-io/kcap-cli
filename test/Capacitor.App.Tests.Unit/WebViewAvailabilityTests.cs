using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Pages render in-app only on macOS, the one runtime where subframe navigations are proven to be
/// cancelled before a request.
public class WebViewAvailabilityTests {
    [Test]
    public async Task Webview_is_offered_only_on_macos() {
        await Assert.That(new WebViewAvailability().IsAvailable).IsEqualTo(OperatingSystem.IsMacOS());
    }
}
