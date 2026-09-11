namespace Capacitor.Cli.Core;

/// The session id the server files a session under: a GUID in any spelling collapses to its 32-hex
/// form, and an opaque vendor id (an ACP `sess-1`) is kept as written — stripping its dashes would
/// name a session the server has never seen.
public static class SessionIds {
    public static string? Canonical(string? id) {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();
        return Guid.TryParse(trimmed, out var g) ? g.ToString("N") : trimmed;
    }
}
