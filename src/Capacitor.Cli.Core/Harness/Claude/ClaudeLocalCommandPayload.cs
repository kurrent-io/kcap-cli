using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Harness.Claude;

/// The messages Claude Code records when the user runs a slash or local command.
static partial class ClaudeLocalCommandPayload {
    /// True when the text is a command invocation, its output or the caveat, and nothing else. Only
    /// the first kind present is stripped: invocation, then output, then caveat.
    public static bool IsPure(string text) {
        if (string.IsNullOrEmpty(text)) return false;

        string remaining;

        if (CommandName().IsMatch(text))
            remaining = CommandArgs().Replace(CommandMessage().Replace(CommandName().Replace(text, ""), ""), "");
        else if (Stdout().IsMatch(text))
            remaining = Stdout().Replace(text, "");
        else if (Caveat().IsMatch(text))
            remaining = Caveat().Replace(text, "");
        else
            return false;

        return string.IsNullOrWhiteSpace(remaining);
    }

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex("<command-message>(.*?)</command-message>", RegexOptions.Singleline)]
    private static partial Regex CommandMessage();

    [GeneratedRegex("<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgs();

    // Stored output can end before its closing tag.
    [GeneratedRegex("<local-command-stdout>(.*?)(?:</local-command-stdout>|$)", RegexOptions.Singleline)]
    private static partial Regex Stdout();

    [GeneratedRegex("<local-command-caveat>(.*?)(?:</local-command-caveat>|$)", RegexOptions.Singleline)]
    private static partial Regex Caveat();
}
