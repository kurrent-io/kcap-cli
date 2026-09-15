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

    /// <summary>
    /// Lets a continuation the fake clock just released reach the PTY before an assertion reads it:
    /// advancing a <see cref="FakeTimeProvider"/> completes the delay but does not run the awaiting
    /// state machine, and the next <c>Task.Delay</c> is only registered once it does.
    /// </summary>
    static Task SettleAsync() => Task.Delay(25);

    [Test]
    public async Task Paste_is_followed_by_one_cr_no_earlier_than_150ms() {
        var pty  = new RecordingPtyProcess();
        var time = new FakeTimeProvider();
        var rt   = new PtyHostedAgentRuntime("claude", pty, approvalsDisabled: false, time);

        var send = rt.SendUserInputAsync("hi");

        await Assert.That(pty.Writes).IsEquivalentTo(new[] { Paste }, CollectionOrdering.Matching);

        time.Advance(TimeSpan.FromMilliseconds(149));
        await SettleAsync();
        await Assert.That(pty.Writes.Count).IsEqualTo(1);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await send;

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

        await SettleAsync();
        await Assert.That(raw.IsCompleted).IsFalse();
        await Assert.That(key.IsCompleted).IsFalse();

        time.Advance(TimeSpan.FromMilliseconds(150));
        await Task.WhenAll(send, raw, key);

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

        foreach (var d in PtyHostedAgentRuntime.SubmitCarriageReturnSchedule) {
            await SettleAsync();
            time.Advance(d);
        }

        await Task.WhenAll(send, raw);

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

        await SettleAsync();
        await Assert.That(pty.Writes).IsEquivalentTo(new[] { Paste }, CollectionOrdering.Matching);

        time.Advance(TimeSpan.FromMilliseconds(150));
        await send;

        await SettleAsync();
        time.Advance(TimeSpan.FromMilliseconds(150));
        await stop;

        await Assert.That(pty.Writes).IsEquivalentTo(new[] { Paste, "\r", "/exit", "\r" }, CollectionOrdering.Matching);
    }
}
