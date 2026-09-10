namespace Capacitor.Cli.Core.WorkItems;

/// Ids are opaque values from another process. Canonicalize, then validate, then let the caller
/// escape: `.` is unreserved, so escaping leaves a dot segment intact and URI normalization would
/// walk it out of the route.
public static class WorkContextIds {
    /// The key the server files a session under; null when nothing usable survives.
    public static string? CanonicalSessionId(string? raw) => Validate(SessionIds.Canonical(raw));

    public static string? ValidWorkItemId(string? raw) => Validate(raw?.Trim());

    static string? Validate(string? id) => id is null || id.Length == 0 || id == "." || id == ".." ? null : id;
}
