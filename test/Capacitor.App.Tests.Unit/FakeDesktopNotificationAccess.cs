using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public sealed class FakeDesktopNotificationAccess(DesktopNotificationAccess current) : IDesktopNotificationAccess {
    public DesktopNotificationAccess Current { get; set; } = current;
    public DesktopNotificationAccess AfterRequest { get; set; } = DesktopNotificationAccess.Allowed;
    /// Answers the next read instead of Current, so a test decides when and with what it completes.
    public Task<DesktopNotificationAccess>? NextRead { get; set; }
    public int Reads { get; private set; }
    public int Requests { get; private set; }
    public int SettingsOpened { get; private set; }

    public Task<DesktopNotificationAccess> GetAsync() {
        Reads++;
        if (NextRead is not { } held) return Task.FromResult(Current);
        NextRead = null;
        return held;
    }

    public Task<DesktopNotificationAccess> RequestAsync() {
        Requests++;
        if (Current == DesktopNotificationAccess.NotDetermined) Current = AfterRequest;
        return Task.FromResult(Current);
    }

    public void OpenSystemSettings() => SettingsOpened++;
}
