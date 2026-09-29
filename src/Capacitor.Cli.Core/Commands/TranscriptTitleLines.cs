using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Whether a transcript line carries the harness's own title, for vendors whose title lives inline in the
/// transcript rather than in a separate store the watcher polls — mirrors what the server's own extractors
/// recognize. A substring prefilter skips the JSON parse for the common case; only vendors that carry a title
/// inline are checked at all.</summary>
internal static partial class TranscriptTitleLines {
    public static bool CarriesHarnessTitle(string vendor, string line) => vendor switch {
        "claude" => (line.Contains("\"ai-title\"") || line.Contains("\"custom-title\"")) && ClaudeTitle(line),
        "pi"     => line.Contains("\"session_info\"") && PiTitle(line),
        "gemini" => line.Contains("\"summary\"") && GeminiTitle(line),
        "opencode" => line.Contains("\"session_title\"") && OpenCodeTitle(line),
        _        => false,
    };

    static bool ClaudeTitle(string line) {
        if (!TryParse(line, out var root)) return false;

        return root.Str("type") switch {
            "ai-title"     => !string.IsNullOrWhiteSpace(root.Str("aiTitle")),
            "custom-title" => !string.IsNullOrWhiteSpace(root.Str("customTitle")),
            _              => false,
        };
    }

    static bool PiTitle(string line) =>
        TryParse(line, out var root) && root.Str("type") == "session_info" && !string.IsNullOrWhiteSpace(root.Str("name"));

    static bool GeminiTitle(string line) =>
        TryParse(line, out var root) && !string.IsNullOrWhiteSpace(root.Obj("$set")?.Str("summary"));

    static bool OpenCodeTitle(string line) {
        if (!TryParse(line, out var root) || root.Str("type") != "session_title") return false;

        var title = root.Str("title");

        return !string.IsNullOrWhiteSpace(title) && !OpenCodePlaceholderTitle().IsMatch(title);
    }

    static bool TryParse(string line, out JsonElement root) {
        try {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();

            return true;
        } catch (JsonException) {
            root = default;

            return false;
        }
    }

    // OpenCode seeds every new session with this placeholder before the real title arrives.
    [GeneratedRegex(@"^New session - \d{4}-")]
    private static partial Regex OpenCodePlaceholderTitle();
}
