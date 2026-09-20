namespace Capacitor.App.Services.Notifications;

public sealed record DesktopNotification(string Id, string Title, string Body, IReadOnlyList<DesktopNotificationAction> Actions);
