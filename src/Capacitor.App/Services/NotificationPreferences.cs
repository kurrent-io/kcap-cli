namespace Capacitor.App.Services;

public sealed record NotificationPreferences(
    bool Permissions = true,
    bool Questions = true,
    bool Idle = true);
