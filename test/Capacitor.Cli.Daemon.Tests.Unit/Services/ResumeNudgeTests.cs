using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The first turn after a restart is a new message, never a re-send of the interrupted one.</summary>
public class ResumeNudgeTests {
    static readonly DateTimeOffset InterruptedAt = new(2026, 10, 17, 9, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task Nudge_names_the_interruption_and_asks_to_verify_before_repeating() {
        var nudge = ResumeNudge.Build(InterruptedAt, "refactor the parser");

        await Assert.That(nudge).Contains("interrupted");
        await Assert.That(nudge).Contains("daemon restart");
        await Assert.That(nudge).Contains(InterruptedAt.ToString("u"));
        await Assert.That(nudge).Contains("working tree");
        await Assert.That(nudge).Contains("do not repeat");
    }

    [Test]
    public async Task Nudge_never_repeats_the_prompt_it_was_given() {
        var nudge = ResumeNudge.Build(InterruptedAt, "delete the staging database");

        await Assert.That(nudge).DoesNotContain("delete the staging database");
    }
}
