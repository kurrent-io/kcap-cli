namespace Capacitor.App.ViewModels;

/// Shapes a one-string status line for a notice card. The line stays one string because other code
/// compares it whole; only the card splits it.
internal static class NoticeText {
    /// The first sentence as the title, without its full stop, and the rest as the body.
    public static (string? Title, string? Body) Headline(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        text = text.Trim();
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        if (end < 0) return (text.EndsWith('.') && !text.EndsWith("..", StringComparison.Ordinal) ? text[..^1] : text, null);
        var body = text[(end + 2)..].Trim();
        return (text[..end], body.Length == 0 ? null : body);
    }

    /// The prose and the shell commands that end it in backticks, so a card can show the commands as
    /// copyable code. Several commands come back one per line, in order, which pastes into a shell as
    /// the same sequence. A command with prose after it stays inline: lifting it would leave the
    /// sentence with a hole in it.
    public static (string? Prose, string? Command) Commands(string? text) {
        if (string.IsNullOrEmpty(text)) return (text, null);
        var parts = text.Split('`');
        // An unbalanced backtick is not a quoted command; leave the line as written.
        if (parts.Length < 3 || parts.Length % 2 == 0) return (text, null);

        if (parts.Where((part, i) => i > 0 && i % 2 == 0).Any(part => part.Trim().TrimEnd('.').Length > 0)) return (text, null);
        var commands = parts.Where((part, i) => i % 2 == 1 && part.Trim().Length > 0).Select(part => part.Trim()).ToArray();
        if (commands.Length == 0) return (text, null);
        var prose = string.Join(' ', parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd(':', ',', ' ');
        // The colon that introduced the commands closes the sentence instead.
        if (prose.Length > 0 && !prose.EndsWith('.') && !prose.EndsWith('…')) prose += ".";
        return (prose.Length == 0 ? null : prose, string.Join('\n', commands));
    }
}
