using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Harness.Claude;

/// The system blocks Claude Code injects into a user message.
static partial class ClaudeSystemInstructions {
    /// The text with every block removed and trimmed; unchanged when it carries none.
    public static string Strip(string text) {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return Block().IsMatch(text) ? Block().Replace(text, "").Trim() : text;
    }

    [GeneratedRegex(
        @"<(system_instructions|system-instructions|system-reminder|system_reminder|SYSTEM_INSTRUCTIONS)\b[^>]*>([\s\S]*?)</\1>",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex Block();
}
