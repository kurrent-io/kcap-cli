namespace Capacitor.App.Services.Notifications;

public interface IDesktopNotificationSink : IDisposable {
    void Show(DesktopNotification notification, Action<string?> activated);
    void Close(string id);
}
