using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class FinalDrainCompletionSignalTests {
    [Test]
    public async Task newline_terminated_is_complete() =>
        await Assert.That(WatchCommand.IsFinalLineComplete("{\"a\":1}\n")).IsTrue();

    [Test]
    public async Task parseable_json_without_newline_is_complete() =>
        await Assert.That(WatchCommand.IsFinalLineComplete("{\"a\":1}\n{\"b\":2}")).IsTrue();

    [Test]
    public async Task unparseable_newlineless_tail_is_incomplete() =>
        await Assert.That(WatchCommand.IsFinalLineComplete("{\"a\":1}\n{\"b\":2")).IsFalse();

    [Test]
    public async Task empty_file_is_complete() =>
        await Assert.That(WatchCommand.IsFinalLineComplete("")).IsTrue();

    [Test]
    public async Task whitespace_only_tail_is_complete() =>
        await Assert.That(WatchCommand.IsFinalLineComplete("{\"a\":1}\n   ")).IsTrue();

    [Test]
    public async Task single_incomplete_line_no_prior_newline_is_incomplete() =>
        await Assert.That(WatchCommand.IsFinalLineComplete("{\"a\":1")).IsFalse();

    /// <summary>
    /// The core regression this task guards: a large write that pauses mid-record must never be
    /// treated as "done" just because it stopped growing for a while. Simulates the final-drain
    /// bounded wait directly against <see cref="WatchCommand.IsFinalLineComplete"/> — length-stable
    /// (the string genuinely doesn't change across the simulated wait window) but unparseable, then
    /// the writer resumes and completes the record. The bounded wait must observe "still incomplete"
    /// throughout the stable window (never send-and-advance a truncated line) and only report
    /// complete once the line actually finishes.
    /// </summary>
    [Test]
    public async Task unparseable_length_stable_past_window_then_grows_is_held_not_sent() {
        const string stableButUnparseable = "{\"a\":1}\n{\"b\":\"still writ"; // no trailing newline, not valid JSON
        const string grownAndComplete     = "{\"a\":1}\n{\"b\":\"still writing\"}\n";

        // Simulate the bounded wait: repeated reads of a file whose content does not change
        // (length-stable) for several iterations — none of them should flip to "complete".
        for (var i = 0; i < 4; i++) {
            await Assert.That(WatchCommand.IsFinalLineComplete(stableButUnparseable)).IsFalse();
        }

        // Only once the writer actually finishes the record (newline-terminated) is it complete.
        await Assert.That(WatchCommand.IsFinalLineComplete(grownAndComplete)).IsTrue();
    }
}

/// <summary>
/// Exercises <see cref="WatchCommand.WaitForFinalLineCompletionAsync"/> against a real file so the
/// bounded-wait loop itself (not just the pure predicate) is covered — small attempts/delay keep
/// these fast.
/// </summary>
public class WaitForFinalLineCompletionAsyncTests {
    [Test]
    public async Task already_complete_returns_true_immediately() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, "{\"a\":1}\n");
            var result = await WatchCommand.WaitForFinalLineCompletionAsync(path, attempts: 4, delayMs: 20);
            await Assert.That(result).IsTrue();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task still_growing_line_completes_within_the_window_returns_true() {
        var path = Path.GetTempFileName();

        try {
            // Starts mid-record (no trailing newline, unparseable) — the writer "finishes" the
            // record shortly after, before the bounded wait gives up.
            await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writ");

            var writer = Task.Run(async () => {
                await Task.Delay(40);
                await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writing\"}\n");
            });

            var result = await WatchCommand.WaitForFinalLineCompletionAsync(path, attempts: 6, delayMs: 20);
            await writer;

            await Assert.That(result).IsTrue();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task never_completes_returns_false_after_exhausting_attempts() {
        var path = Path.GetTempFileName();

        try {
            // Length-stable AND unparseable for the entire window — must never flip to "complete".
            await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writ");
            var result = await WatchCommand.WaitForFinalLineCompletionAsync(path, attempts: 3, delayMs: 10);
            await Assert.That(result).IsFalse();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task missing_file_returns_false() {
        var result = await WatchCommand.WaitForFinalLineCompletionAsync(
            "/tmp/nonexistent_" + Guid.NewGuid(), attempts: 2, delayMs: 10);
        await Assert.That(result).IsFalse();
    }
}
