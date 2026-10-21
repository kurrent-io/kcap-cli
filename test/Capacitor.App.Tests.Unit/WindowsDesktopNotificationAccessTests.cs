using System.Runtime.Versioning;
using Capacitor.App.Platform.Windows;
using Capacitor.App.Services.Notifications;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

[RunOn(OS.Windows)]
[SupportedOSPlatform("windows")]
public class WindowsDesktopNotificationAccessTests {
    const string App = "io.kurrent.kcap";
    const string AppKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\io.kurrent.kcap";
    const string GlobalKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    static WindowsDesktopNotificationAccess With(Dictionary<(string, string), object> values) =>
        new(App, (key, name) => values.GetValueOrDefault((key, name)));

    /// The app's value is absent until the user first changes it, and absent means on.
    [Test]
    public async Task No_stored_switch_is_allowed() {
        await Assert.That(await With([]).GetAsync()).IsEqualTo(DesktopNotificationAccess.Allowed);
    }

    [Test]
    public async Task The_apps_own_switch_turned_off_is_denied() {
        var access = With(new() { [(AppKey, "Enabled")] = 0 });
        await Assert.That(await access.GetAsync()).IsEqualTo(DesktopNotificationAccess.Denied);
    }

    [Test]
    public async Task Notifications_turned_off_for_every_app_is_denied() {
        var access = With(new() { [(GlobalKey, "ToastEnabled")] = 0, [(AppKey, "Enabled")] = 1 });
        await Assert.That(await access.GetAsync()).IsEqualTo(DesktopNotificationAccess.Denied);
    }

    [Test]
    public async Task Both_switches_on_is_allowed() {
        var access = With(new() { [(GlobalKey, "ToastEnabled")] = 1, [(AppKey, "Enabled")] = 1 });
        await Assert.That(await access.GetAsync()).IsEqualTo(DesktopNotificationAccess.Allowed);
    }

    /// Windows shows no prompt, so a request only re-reads; it must never report NotDetermined,
    /// which would make the app ask for something Windows cannot grant.
    [Test]
    public async Task A_request_rereads_rather_than_prompting() {
        var access = With(new() { [(AppKey, "Enabled")] = 0 });
        await Assert.That(await access.RequestAsync()).IsEqualTo(DesktopNotificationAccess.Denied);
    }

    [Test]
    public async Task An_unreadable_hive_is_unknown() {
        var access = new WindowsDesktopNotificationAccess(App, (_, _) => throw new System.Security.SecurityException("denied"));
        await Assert.That(await access.GetAsync()).IsEqualTo(DesktopNotificationAccess.Unknown);
    }
}
