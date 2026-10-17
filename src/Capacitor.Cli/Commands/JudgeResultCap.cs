namespace Capacitor.Cli.Commands;

/// <summary>Bounds every judge tool result. Claude Code spills an MCP result past its output limit (25,000 tokens by default)
/// to a file and tells the model to read it, and the judge has no tool that reads files, so an oversized result is lost.</summary>
static class JudgeResultCap {
    public const int MaxChars = 40_000;

    /// <summary>The text as is when it fits; otherwise its head and a note saying how much was cut and how to narrow the call.</summary>
    public static string Cap(string text, string hint) =>
        text.Length <= MaxChars
            ? text
            : $"{text[..MaxChars]}\n\n[truncated: showing {MaxChars} of {text.Length} characters. {hint}]";

    /// <summary>One page of the text from <paramref name="offset"/>, with a note naming the next offset when more follows.</summary>
    public static string Page(string text, int offset) {
        if (offset >= text.Length) return $"[offset {offset} is past the end: the body has {text.Length} characters]";
        var end  = Math.Min(text.Length, offset + MaxChars);
        var page = text[offset..end];
        return end == text.Length && offset == 0
            ? page
            : $"{page}\n\n[characters {offset}-{end} of {text.Length}.{(end < text.Length ? $" Call again with offset {end} for the rest." : "")}]";
    }
}
