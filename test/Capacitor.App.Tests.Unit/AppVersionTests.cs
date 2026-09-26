using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class AppVersionTests {
    [Test]
    public async Task Format_drops_build_metadata() {
        await Assert.That(AppVersion.Format("1.2.3-alpha.0.4+abc123")).IsEqualTo("1.2.3-alpha.0.4");
        await Assert.That(AppVersion.Format("1.2.3")).IsEqualTo("1.2.3");
        await Assert.That(AppVersion.Format(null)).IsEqualTo("unknown");
    }
}
