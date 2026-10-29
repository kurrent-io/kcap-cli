namespace Capacitor.App.Services.Notifications;

/// A platform with no notification permission to read or request.
public sealed class UnknownDesktopNotificationAccess : IDesktopNotificationAccess {
    public static UnknownDesktopNotificationAccess Instance { get; } = new();

    UnknownDesktopNotificationAccess() { }

    public Task<DesktopNotificationAccess> GetAsync() => Task.FromResult(DesktopNotificationAccess.Unknown);
    public Task<DesktopNotificationAccess> RequestAsync() => Task.FromResult(DesktopNotificationAccess.Unknown);
    public void OpenSystemSettings() { }
}
