using System.Globalization;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The first message a resumed flow participant receives. The daemon cannot tell whether the round it
/// was last sent is still open, so the reviewer's own history decides, and the round is repeated because
/// only it carries the round token a result is submitted under.
/// </summary>
internal static class ReviewerResumeNudge {
    public static string Build(DateTimeOffset interruptedAt, string? lastRoundInput) {
        var at = interruptedAt.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
        var head = $"The kcap daemon restarted at {at}, possibly while you were working on a review round. ";

        if (string.IsNullOrWhiteSpace(lastRoundInput))
            return head + "If you were not in the middle of a round, wait for the next round and do nothing else.";

        return head
             + "If you already submitted that round's result with submit_review_result, do nothing and wait for the next round. "
             + "If submit_review_result refuses the round token, the round has ended, so stop. "
             + "Otherwise finish the round and submit its result.\n\n"
             + "The last round you were sent:\n\n"
             + lastRoundInput;
    }
}
