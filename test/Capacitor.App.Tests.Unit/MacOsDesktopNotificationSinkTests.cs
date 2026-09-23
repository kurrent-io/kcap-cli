using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public class MacOsDesktopNotificationSinkTests {
    [Test]
    [Arguments(0, DesktopNotificationAccess.NotDetermined)]
    [Arguments(1, DesktopNotificationAccess.Denied)]
    [Arguments(2, DesktopNotificationAccess.Allowed)]
    [Arguments(3, DesktopNotificationAccess.Allowed)]
    [Arguments(4, DesktopNotificationAccess.Allowed)]
    [Arguments(9, DesktopNotificationAccess.Unknown)]
    public async Task Authorization_status_maps_to_access(int status, DesktopNotificationAccess expected) {
        if (!OperatingSystem.IsMacOS()) return;
        await Assert.That(MacOsDesktopNotificationSink.Access(status)).IsEqualTo(expected);
    }
}
