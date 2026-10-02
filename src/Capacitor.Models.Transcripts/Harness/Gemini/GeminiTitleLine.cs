using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Gemini;

/// <summary>Whether a Gemini transcript line is a <c>$set</c> patch carrying a non-blank <c>summary</c>.</summary>
public static class GeminiTitleLine {
    public static bool CarriesTitle(string line) {
        if (!line.Contains("\"summary\"")) return false;

        try {
            using var doc = JsonDocument.Parse(line);

            return !string.IsNullOrWhiteSpace(doc.RootElement.Obj("$set")?.Str("summary"));
        } catch (JsonException) {
            return false;
        }
    }
}
