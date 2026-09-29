using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary>Whether a transcript line is one of Claude Code's own title records the server records: a non-blank
/// <c>ai-title</c> or <c>custom-title</c>. The legacy <c>summary</c> shape is not one — the server does not record it,
/// so it must not stand in for a title the server will have.</summary>
public static class ClaudeTitleLine {
    /// <param name="sessionId">The session the line is recorded for. The server records a title line only when its own
    /// <c>sessionId</c> names that session, so one copied in from another session does not count.</param>
    /// <param name="isRename">A <c>custom-title</c>; otherwise an <c>ai-title</c>, which every server records.</param>
    public static bool CarriesTitle(string line, string sessionId, out bool isRename) {
        isRename = false;
        if (!line.Contains("\"ai-title\"") && !line.Contains("\"custom-title\"")) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("sessionId") is not { } own || Canonical(own) != Canonical(sessionId)) return false;

            switch (root.Str("type")) {
                case "ai-title":
                    return !string.IsNullOrWhiteSpace(root.Str("aiTitle"));
                case "custom-title":
                    isRename = true;
                    return !string.IsNullOrWhiteSpace(root.Str("customTitle"));
                default:
                    return false;
            }
        } catch (JsonException) {
            return false;
        }
    }

    // The server's canonical form: a GUID in its dashless form, anything else as given.
    static string Canonical(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;
}
