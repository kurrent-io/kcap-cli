using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A resumed reviewer is told the round may still be open and is handed it again, since only
/// that input carries the round's token.</summary>
public class ReviewerResumeNudgeTests {
    static readonly DateTimeOffset InterruptedAt = new(2026, 10, 23, 9, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task The_nudge_repeats_the_last_round_and_says_how_to_settle_it() {
        var nudge = ReviewerResumeNudge.Build(InterruptedAt, "Round 2 (token r2-abc): re-check the lock order");

        await Assert.That(nudge).Contains(InterruptedAt.ToString("u"));
        await Assert.That(nudge).Contains("submit_review_result");
        await Assert.That(nudge).Contains("already submitted");
        await Assert.That(nudge).Contains("Round 2 (token r2-abc): re-check the lock order");
    }

    [Test]
    public async Task Without_a_round_the_nudge_asks_the_reviewer_to_wait() {
        var nudge = ReviewerResumeNudge.Build(InterruptedAt, null);

        await Assert.That(nudge).Contains("wait for the next round");
        await Assert.That(nudge).DoesNotContain("The last round you were sent");
    }
}
