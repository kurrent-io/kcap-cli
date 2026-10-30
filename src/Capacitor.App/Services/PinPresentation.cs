namespace Capacitor.App.Services;

/// What a search hit or a listing row knows about a pinned session's owner that its summary does not.
public sealed record PinPresentation(string? Username, string? DisplayName, string? AvatarUrl);
