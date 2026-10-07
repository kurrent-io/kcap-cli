namespace Capacitor.Cli.Core.Harness.Claude;

/// The frame Claude Code wraps around a message another agent in the session sends on the user
/// channel, a subagent's final report among them.
static class ClaudeAgentMessageFrame {
    // The preamble varies, and a message queued while the session was busy carries none.
    const string Preamble = "Another Claude session sent a message";
    const string OpenTag  = "<agent-message";

    /// True when the message opens with the tag, or with the preamble and then the tag.
    public static bool IsMatch(string text) {
        var span = text.AsSpan().TrimStart();

        if (span.StartsWith(Preamble, StringComparison.Ordinal)) {
            var colon = span.IndexOf(':');
            var line  = span.IndexOf('\n');

            if (colon < 0 || (line >= 0 && colon > line)) return false;

            span = span[(colon + 1)..].TrimStart();
        }

        if (!span.StartsWith(OpenTag, StringComparison.Ordinal)) return false;

        var after = span[OpenTag.Length..];

        return after.Length == 0 || after[0] is '>' or ' ' or '\t' or '\r' or '\n';
    }
}
