namespace Capacitor.App.Services.Notifications;

/// Unknown covers every platform and launch mode with no authorization to read; callers show nothing for it.
public enum DesktopNotificationAccess { Unknown, NotDetermined, Denied, Allowed }
