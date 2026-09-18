namespace Capacitor.App.Services;

/// The org-wide settlement ping. A null RequestId is a session-wide clear.
public sealed record PermissionRespondedPing(string SessionId, string? RequestId);
