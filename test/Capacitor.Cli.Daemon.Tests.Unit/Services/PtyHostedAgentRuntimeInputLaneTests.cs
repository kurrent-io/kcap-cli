using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Every PTY write is serialised behind one lane, so a paste and its submit carriage return are
/// never split by another writer.
/// </summary>
public class PtyHostedAgentRuntimeInputLaneTests {
    const string Paste = "\x1b[200~hi\x1b[201~";

    static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(10);

    /// <summary>A window long enough for a write the test expects NOT to happen to show up.</summary>
    static readonly TimeSpan NegativeBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the real-time budget runs out, returning
    /// whether it held. Advancing a <see cref="FakeTimeProvider"/> releases a delay but does not run
    /// the awaiting state machine, so a fixed sleep would race the continuation instead of observing
    /// it.
    /// </summary>
    static async Task<bool> PollAsync(Func<bool> condition, TimeSpan? budget = null) {
        var deadline = DateTime.UtcNow + (budget ?? PollBudget);

        while (!condition()) {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(5);
        }

        return true;
    }

    /// <summary>
    /// Advances the fake clock in <paramref name="step"/>s until <paramref name="condition"/> holds.
    /// An advance only credits a delay that is already registered, and the continuation registering
    /// the next one runs after the advance returns — so the clock is driven from the condition, never
    /// from a guessed number of advances.
    /// </summary>
    static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + PollBudget;

        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            time.Advance(step);
            await Task.Delay(5);
        }
    }

    [Test]
    public async Task Paste_is_followed_by_one_cr_no_earlier_than_150ms() {
        var pty  = new RecordingPtyProcess();
        var time = new FakeTimeProvider();
        var rt   = new PtyHostedAgentRuntime("claude", pty, approvalsDisabled: false, time);

        var send = rt.SendUserInputAsync("hi");

        await Assert.That(pty.Writes).IsEquivalentTo(new[] { Paste }, CollectionOrdering.Matching);

        time.Advance(TimeSpan.FromMilliseconds(149));
        await Assert.That(await PollAsync(() => pty.Writes.Count > 1, NegativeBudget)).IsFalse();

        time.Advance(TimeSpan.FromMilliseconds(1));
        await send.WaitAsync(PollBudget);

        await Assert.That(pty.Writes).IsEquivalentTo(new[] { Paste, "\r" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Raw_input_during_a_send_lands_after_the_cr_and_nothing_is_lost() {
        var pty  = new RecordingPtyProcess();
        var time = new FakeTimeProvider();
        var rt   = new PtyHostedAgentRuntime("claude", pty, false, time);

        await rt.SendRawInputAsync("a"u8.ToArray());

        var send = rt.SendUserInputAsync("hi");
        var raw  = rt.SendRawInputAsync("b"u8.ToArray());
        var key  = rt.SendSpecialKeyAsync("Escape");

        await Assert.That(await PollAsync(() => raw.IsCompleted || key.IsCompleted, NegativeBudget)).IsFalse();

        await AdvanceUntilAsync(time, TimeSpan.FromMilliseconds(150),
            () => send.IsCompleted && raw.IsCompleted && key.IsCompleted, "the queued writers to drain");

        await Assert.That(pty.Writes[0]).IsEqualTo("a");
        await Assert.That(pty.Writes[1]).IsEqualTo(Paste);
        await Assert.That(pty.Writes[2]).IsEqualTo("\r");
        await Assert.That(pty.Writes.Skip(3)).IsEquivalentTo(new[] { "b", "\x1b" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task Spray_schedule_holds_the_lane_until_the_last_cr() {
        var pty  = new RecordingPtyProcess();
        var time = new FakeTimeProvider();
        var rt   = new PtyHostedAgentRuntime("codex", pty, approvalsDisabled: true, time);

        var send = rt.SendUserInputAsync("hi");
        var raw  = rt.SendRawInputAsync("k"u8.ToArray());

        await AdvanceUntilAsync(time, PtyHostedAgentRuntime.SubmitCarriageReturnSchedule[^1],
            () => send.IsCompleted && raw.IsCompleted, "the submit spray to finish");

        await Assert.That(pty.Writes[^1]).IsEqualTo("k");
        await Assert.That(pty.Writes.Count(w => w == "\r")).IsEqualTo(PtyHostedAgentRuntime.SubmitCarriageReturnSchedule.Length);
    }

    [Test]
    public async Task Graceful_stop_cannot_interleave_with_a_paste() {
        var pty  = new RecordingPtyProcess();
        var time = new FakeTimeProvider();
        var rt   = new PtyHostedAgentRuntime("claude", pty, approvalsDisabled: false, time);

        var send = rt.SendUserInputAsync("hi");
        var stop = rt.RequestGracefulStopAsync();

        await Assert.That(await PollAsync(() => pty.Writes.Count > 1, NegativeBudget)).IsFalse();

        await AdvanceUntilAsync(time, TimeSpan.FromMilliseconds(150),
            () => send.IsCompleted && stop.IsCompleted, "the paste and the stop to finish");

        await Assert.That(pty.Writes).IsEquivalentTo(new[] { Paste, "\r", "/exit", "\r" }, CollectionOrdering.Matching);
    }
}
