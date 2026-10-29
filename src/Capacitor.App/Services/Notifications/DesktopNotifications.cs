namespace Capacitor.App.Services.Notifications;

public static class DesktopNotifications {
    /// The sink's own permission model, or Unknown where the platform has none to ask.
    public static IDesktopNotificationAccess AccessOf(IDesktopNotificationSink sink) =>
        sink as IDesktopNotificationAccess ?? UnknownDesktopNotificationAccess.Instance;

    internal static void Report(Exception error) => Report(error.Message);

    internal static void Report(string message) {
        // Never let logging failures propagate across an Objective-C/COM callback boundary.
        try { Console.Error.WriteLine($"Desktop notifications: {message}"); } catch (Exception) { }
    }
}
