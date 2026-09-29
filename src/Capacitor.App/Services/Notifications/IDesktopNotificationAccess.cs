namespace Capacitor.App.Services.Notifications;

public interface IDesktopNotificationAccess {
    Task<DesktopNotificationAccess> GetAsync();

    /// Shows the operating system's permission prompt when access is undetermined. An ignored
    /// prompt settles as denied, so call this only while the user is looking at the app.
    Task<DesktopNotificationAccess> RequestAsync();

    void OpenSystemSettings();
}
