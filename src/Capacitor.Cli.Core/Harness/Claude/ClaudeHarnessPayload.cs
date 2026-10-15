namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// Whether a Claude user-channel message is the harness talking rather than a person. Mirrors the
/// server's predicate check for check: a message the two disagree on is a turn one side shows and
/// the other hides.
/// </summary>
/// <remarks>The interrupt marker is the one check the server's predicate must carry too: while it
/// does not, a marker that is the session's newest user line fails the server's anchor check and
/// the consultation runs windowless.</remarks>
public static class ClaudeHarnessPayload {
    const string NotificationTag = "<task-notification";
    const string NotificationEnd = "</task-notification>";
    const string SkillHeader     = "Base directory for this skill:";

    // Written on Esc or a declined prompt. The words are Claude Code's; the human's no is declared
    // as a refusal, so as a turn it would only push a real message out of the window.
    static readonly string[] InterruptMarkers = [
        "[Request interrupted by user]",
        "[Request interrupted by user for tool use]",
    ];

    /// <summary>
    /// True when <paramref name="text"/> is wholly a harness payload. A message that merely quotes
    /// one of the tags has words of its own and is a real ask.
    /// </summary>
    public static bool IsHarnessPayload(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (IsWhollyTaskNotification(text) || ClaudeAgentMessageFrame.IsMatch(text)) return true;

        if (InterruptMarkers.Contains(text.Trim(), StringComparer.Ordinal)) return true;

        if (text.AsSpan().TrimStart().StartsWith(SkillHeader, StringComparison.Ordinal)) return true;

        // Purity is judged on the stripped text, or a payload carrying a system block reads as an ask.
        var clean = ClaudeSystemInstructions.Strip(text);

        return ClaudeLocalCommandPayload.IsPure(clean) || ClaudeBashPayload.IsPure(clean);
    }

    static bool IsWhollyTaskNotification(string text) {
        var span = text.AsSpan().TrimStart();

        if (!span.StartsWith(NotificationTag, StringComparison.Ordinal)) return false;

        // The tag has to end where the name ends, or <task-notification-guide> reads as one.
        var after = span[NotificationTag.Length..];

        if (after.Length > 0 && after[0] is not ('>' or '/' or ' ' or '\t' or '\r' or '\n')) return false;

        // A truncated payload never closed, so there is nothing after it either way.
        var close = span.LastIndexOf(NotificationEnd, StringComparison.Ordinal);

        return close < 0 || span[(close + NotificationEnd.Length)..].IsWhiteSpace();
    }
}
