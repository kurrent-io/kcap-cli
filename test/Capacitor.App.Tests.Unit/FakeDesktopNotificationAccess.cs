using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public sealed class FakeDesktopNotificationAccess(DesktopNotificationAccess current) : IDesktopNotificationAccess {
    public DesktopNotificationAccess Current { get; set; } = current;
    public DesktopNotificationAccess AfterRequest { get; set; } = DesktopNotificationAccess.Allowed;
    public int Requests { get; private set; }
    public int SettingsOpened { get; private set; }

    public Task<DesktopNotificationAccess> GetAsync() => Task.FromResult(Current);

    public Task<DesktopNotificationAccess> RequestAsync() {
        Requests++;
        if (Current == DesktopNotificationAccess.NotDetermined) Current = AfterRequest;
        return Task.FromResult(Current);
    }

    public void OpenSystemSettings() => SettingsOpened++;
}
