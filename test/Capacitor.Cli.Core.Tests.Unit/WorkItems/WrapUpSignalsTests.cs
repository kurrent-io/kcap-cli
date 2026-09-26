using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

public class WrapUpSignalsTests {
    public static IEnumerable<string> ClosingMessages() => [
        "## Summary\n\nAdded the retry guard to the uploader and a test that pins the backoff.",
        "**Summary**\n- Fixed the null check\n- Added a regression test",
        "Done. The flaky test now awaits the TCS instead of polling.",
        "Done — the migration runs in 40ms on the seeded database.",
        "I fixed the parser and updated the fixtures.\n\n### Next steps\n- Bump the submodule once this merges",
        "Recap: the endpoint now returns 404 for hidden sessions.",
        "- Shipped: the new flag is on by default.",
        "Merged. The branch is deleted.",
        "Wrapped up — nothing else is pending on my side.",
        "Completed:\n1. Parser fix\n2. Docs",
        "The fix is in and all tests pass locally.",
        "Pushed the change; CI is green on the branch.",
        "Opened PR #1182 against main with the fix and its tests.",
        "The branch is pushed and the PR is ready for review.",
        "Rebased onto main. Everything is ready to merge.",
        "Tests are green after the rename.\n",
        "I've opened PR 1190 for this.",
    ];

    public static IEnumerable<string> MidTaskMessages() => [
        "Not complete yet — waiting on your answer?",
        "Now let me run the tests.",
        "I'll look at the uploader next and check how the retry loop handles a 401.",
        "Should I open PR #12 now, or keep going with the docs?",
        "Complete the migration first, then I can wire the endpoint.",
        "The build failed with CS0246; checking which using is missing.",
        "Which of the two approaches do you prefer?",
        "I'm done reading the spec; starting on the projector now",
        "Summary of what remains is below — want me to continue?**",
        "",
        "   ",
    ];

    [Test]
    [MethodDataSource(nameof(ClosingMessages))]
    public async Task A_closing_message_reads_as_a_wrap_up(string text) =>
        await Assert.That(WrapUpSignals.LooksLikeWrapUp(text)).IsTrue();

    [Test]
    [MethodDataSource(nameof(MidTaskMessages))]
    public async Task A_mid_task_message_or_a_question_does_not(string text) =>
        await Assert.That(WrapUpSignals.LooksLikeWrapUp(text)).IsFalse();

    [Test]
    public async Task Null_is_not_a_wrap_up() =>
        await Assert.That(WrapUpSignals.LooksLikeWrapUp(null)).IsFalse();
}
