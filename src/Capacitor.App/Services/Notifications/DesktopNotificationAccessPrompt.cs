namespace Capacitor.App.Services.Notifications;

/// Asks for notification access the first time an app window is active. Left to the first
/// notification, the request lands while the app is in the background, where the operating
/// system's prompt reads as an ordinary banner and an unanswered one settles as denied.
public sealed class DesktopNotificationAccessPrompt(IDesktopNotificationAccess access, Func<NotificationPreferences> preferences) {
    int _asked;

    public async Task AskOnceAsync() {
        var current = preferences();
        if (!current.Permissions && !current.Questions && !current.Idle) return;
        if (Interlocked.Exchange(ref _asked, 1) != 0) return;
        try {
            if (await access.GetAsync() == DesktopNotificationAccess.NotDetermined) await access.RequestAsync();
        } catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
    }
}
