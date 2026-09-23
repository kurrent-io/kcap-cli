using Capacitor.Cli.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

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
}

/// <summary>
/// Exercises <see cref="WatchCommand.WaitForFinalLineCompletionAsync"/> against a real file so the
/// bounded-wait loop itself (not just the pure predicate) is covered — small attempts/delay keep
/// these fast.
/// </summary>
public class WaitForFinalLineCompletionAsyncTests {
    [Test]
    public async Task already_complete_returns_true_immediately() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("transcript.tmp", "{\"a\":1}\n");
        var result = await WatchCommand.WaitForFinalLineCompletionAsync(path, TimeProvider.System, attempts: 4, delayMs: 20);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task still_growing_line_completes_within_the_window_returns_true() {
        using var tmp = TempDir.WithPathTo("transcript.tmp", out var path);

        // Incomplete when the wait first reads it. The poll then parks on the clock; the line is
        // finished before that delay is released, so the next read must report complete.
        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writ");

        var clock = new FakeTimeProvider();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = WatchCommand.WaitForFinalLineCompletionAsync(
            path, new DelayObservingTime(clock, parked), attempts: 6, delayMs: 20);
        await parked.Task;

        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writing\"}\n");
        clock.Advance(TimeSpan.FromMilliseconds(20));

        await Assert.That(await result).IsTrue();
    }

    /// Forwards to a <see cref="FakeTimeProvider"/> and signals the first time the wait arms a delay,
    /// which is after it has read the file and found it incomplete.
    sealed class DelayObservingTime(FakeTimeProvider inner, TaskCompletionSource parked) : TimeProvider {
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            // Register the timer before signalling. The waiter advances the clock from that signal,
            // and an advance that lands before the timer exists never fires.
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            parked.TrySetResult();
            return timer;
        }
    }

    [Test]
    public async Task never_completes_returns_false_after_exhausting_attempts() {
        using var tmp = TempDir.WithPathTo("transcript.tmp", out var path);

        // Length-stable AND unparseable for the entire window — must never flip to "complete".
        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writ");
        var result = await WatchCommand.WaitForFinalLineCompletionAsync(path, TimeProvider.System, attempts: 3, delayMs: 10);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task missing_file_returns_false() {
        var result = await WatchCommand.WaitForFinalLineCompletionAsync(
            "/tmp/nonexistent_" + Guid.NewGuid(), TimeProvider.System, attempts: 2, delayMs: 10);
        await Assert.That(result).IsFalse();
    }

    /// <summary>The poll must not lock the still-writing agent out of its transcript.
    /// <para>Only DISCRIMINATES on Windows — Unix has no mandatory sharing, so this also passes with the
    /// fix reverted (verified). Keep that in mind before trusting a local green.</para></summary>
    [Test]
    public async Task completes_while_another_handle_holds_the_file_open_for_writing() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("transcript.jsonl");

        await File.WriteAllTextAsync(path, "{\"a\":1}\n");

        // A well-behaved writer: holds the file open for Write, sharing read+write, exactly as an
        // agent appending to its own transcript does.
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var result = await WatchCommand.WaitForFinalLineCompletionAsync(path, TimeProvider.System, attempts: 3, delayMs: 10);

        await Assert.That(result).IsTrue();
    }
}
