using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary>Whether a transcript line is one of Claude Code's own title records the server records: a non-blank
/// <c>ai-title</c> or <c>custom-title</c>. The legacy <c>summary</c> shape is not one — the server does not record it,
/// so it must not stand in for a title the server will have.</summary>
public static class ClaudeTitleLine {
    public static bool CarriesTitle(string line) {
        if (!line.Contains("\"ai-title\"") && !line.Contains("\"custom-title\"")) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.Str("type") switch {
                "ai-title"     => !string.IsNullOrWhiteSpace(root.Str("aiTitle")),
                "custom-title" => !string.IsNullOrWhiteSpace(root.Str("customTitle")),
                _              => false,
            };
        } catch (JsonException) {
            return false;
        }
    }
}
