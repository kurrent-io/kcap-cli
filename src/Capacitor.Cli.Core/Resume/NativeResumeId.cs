namespace Capacitor.Cli.Core.Resume;

/// <summary>
/// The id a vendor resumes a session by, derived from the session id the server lists. Null where that id cannot
/// name the vendor's session: a vendor that resumes by something else, an id that is not a GUID where the vendor
/// needs one, or an ACP id the server encoded to a <c>senc1…</c> token, which cannot be turned back.
/// </summary>
public static class NativeResumeId {
    /// <summary>ACP vendors whose daemon runtime reloads a session by id.</summary>
    static readonly HashSet<string> AcpLoadable = new(StringComparer.OrdinalIgnoreCase) { "cursor", "copilot", "opencode" };

    public static string? For(string? vendor, string? sessionId) {
        if (string.IsNullOrWhiteSpace(vendor) || string.IsNullOrWhiteSpace(sessionId)) return null;

        var id = sessionId.Trim();

        if (vendor.Equals("claude", StringComparison.OrdinalIgnoreCase) || vendor.Equals("codex", StringComparison.OrdinalIgnoreCase))
            return Guid.TryParse(id, out var guid) ? guid.ToString("D") : null;

        if (!AcpLoadable.Contains(vendor) || id.StartsWith("senc1", StringComparison.Ordinal)) return null;

        // The server files a GUID under its dashless form; the vendor minted it dashed.
        return Guid.TryParse(id, out var acpGuid) ? acpGuid.ToString("D") : id;
    }
}
