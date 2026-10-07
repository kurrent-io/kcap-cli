using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Harness.Claude;

/// The messages Claude Code records when the user runs a shell command with the <c>!</c> prefix.
static partial class ClaudeBashPayload {
    /// True when the text is a command or its output and nothing else.
    public static bool IsPure(string text) {
        if (string.IsNullOrEmpty(text)) return false;

        if (!Input().IsMatch(text) && !Stdout().IsMatch(text) && !Stderr().IsMatch(text)) return false;

        return string.IsNullOrWhiteSpace(Stderr().Replace(Stdout().Replace(Input().Replace(text, ""), ""), ""));
    }

    // The input needs its closing tag, so a prompt that opens with the bare tag stays a prompt;
    // output can be truncated before its own.
    [GeneratedRegex("<bash-input>(.*?)</bash-input>", RegexOptions.Singleline)]
    private static partial Regex Input();

    [GeneratedRegex("<bash-stdout>(.*?)(?:</bash-stdout>|$)", RegexOptions.Singleline)]
    private static partial Regex Stdout();

    [GeneratedRegex("<bash-stderr>(.*?)(?:</bash-stderr>|$)", RegexOptions.Singleline)]
    private static partial Regex Stderr();
}
