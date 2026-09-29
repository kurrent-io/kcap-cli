using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary>Whether a transcript line is one of Claude Code's own title records the server records: a non-blank
/// <c>ai-title</c>, which every server records; or a <c>custom-title</c> or a <c>{"type":"summary","summary":…}</c>
/// line, which only a server with harness titles records (the summary ranking below the others). Stored transcripts
/// can still carry the summary shape as their only title, so it must keep being recognized. Those two count only once
/// that server is confirmed. Like the server's extractor, a line is judged by its parsed
/// <c>type</c>, never its spelling: one the server does not record must not count here either.</summary>
public static class ClaudeTitleLine {
    /// <param name="sessionId">The session the line is recorded for. A title line copied in from another session names
    /// that session and does not count; a legacy summary names none and counts unless it names another.</param>
    /// <param name="recordedByEveryServer">An <c>ai-title</c>. A <c>custom-title</c> or a legacy summary is recorded
    /// only by a server with harness titles.</param>
    public static bool CarriesTitle(string line, string sessionId, out bool recordedByEveryServer) {
        recordedByEveryServer = false;
        if (!line.Contains("\"ai-title\"") && !line.Contains("\"custom-title\"") && !line.Contains("\"summary\"")) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            var (field, legacy) = root.Str("type") switch {
                "ai-title"     => ("aiTitle", false),
                "custom-title" => ("customTitle", false),
                "summary"      => ("summary", true),
                _              => (null, false),
            };
            if (field is null) return false;

            var hasSid = root.Prop("sessionId") is not null;
            if (hasSid && (root.Str("sessionId") is not { } own || Canonical(own) != Canonical(sessionId))) return false;
            if (!hasSid && !legacy) return false;

            recordedByEveryServer = field == "aiTitle";

            return !string.IsNullOrWhiteSpace(root.Str(field));
        } catch (JsonException) {
            return false;
        }
    }

    // The server's canonical form: a GUID in its dashless form, anything else as given.
    static string Canonical(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;
}
