using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.Harness.OpenCode;

/// <summary>Whether an OpenCode transcript line is a <c>session_title</c> record (written by the kcap plugin) carrying
/// a real title.</summary>
public static partial class OpenCodeTitleLine {
    public static bool CarriesTitle(string line) {
        if (!line.Contains("\"session_title\"")) return false;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.Str("type") == "session_title" && root.Str("title") is { } title
                && !string.IsNullOrWhiteSpace(title) && !IsPlaceholder(title);
        } catch (JsonException) {
            return false;
        }
    }

    /// <summary>OpenCode seeds every new session with <c>New session - &lt;ISO date&gt;</c> before the real title
    /// arrives.</summary>
    public static bool IsPlaceholder(string title) => Placeholder().IsMatch(title);

    [GeneratedRegex(@"^New session - \d{4}-")]
    private static partial Regex Placeholder();
}
