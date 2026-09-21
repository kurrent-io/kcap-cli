using Capacitor.App.Services;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public class DesktopNotificationAccessPromptTests {
    [Test]
    public async Task Undetermined_access_is_requested_once_across_activations() {
        var access = new FakeDesktopNotificationAccess(DesktopNotificationAccess.NotDetermined) {
            AfterRequest = DesktopNotificationAccess.NotDetermined,
        };
        var prompt = new DesktopNotificationAccessPrompt(access, () => new NotificationPreferences());

        await prompt.AskOnceAsync();
        await prompt.AskOnceAsync();

        await Assert.That(access.Requests).IsEqualTo(1);
    }

    [Test]
    [Arguments(DesktopNotificationAccess.Unknown)]
    [Arguments(DesktopNotificationAccess.Denied)]
    [Arguments(DesktopNotificationAccess.Allowed)]
    public async Task Settled_access_is_never_requested(DesktopNotificationAccess current) {
        var access = new FakeDesktopNotificationAccess(current);
        var prompt = new DesktopNotificationAccessPrompt(access, () => new NotificationPreferences());

        await prompt.AskOnceAsync();

        await Assert.That(access.Requests).IsEqualTo(0);
    }

    [Test]
    public async Task Nothing_is_requested_until_a_notification_is_switched_on() {
        var access = new FakeDesktopNotificationAccess(DesktopNotificationAccess.NotDetermined);
        var preferences = new NotificationPreferences(false, false, false);
        var prompt = new DesktopNotificationAccessPrompt(access, () => preferences);

        await prompt.AskOnceAsync();
        await Assert.That(access.Requests).IsEqualTo(0);

        preferences = preferences with { Idle = true };
        await prompt.AskOnceAsync();

        await Assert.That(access.Requests).IsEqualTo(1);
    }
}
