using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Pi;

/// <summary>Whether a Pi transcript line is a <c>session_info</c> record carrying a non-blank <c>/name</c>.</summary>
public static class PiTitleLine {
    public static bool CarriesTitle(string line) {
        if (!line.Contains("\"session_info\"")) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.Str("type") == "session_info" && !string.IsNullOrWhiteSpace(root.Str("name"));
        } catch (JsonException) {
            return false;
        }
    }
}
