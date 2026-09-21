using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class ReapVerdictGateTests {
    static ReapVerdictGate Gate(bool insideWindow = true) => new(() => insideWindow, NullLogger.Instance);

    [Test]
    public async Task First_claim_publishes_the_verdict_with_the_window_bit() {
        var gate = Gate(insideWindow: true);

        var claimed = gate.TryStartReap("reason_a", () => Task.CompletedTask);

        await Assert.That(claimed).IsTrue();
        await Assert.That(gate.ReadVerdict()).IsEqualTo(new TerminationVerdict("reason_a", true));
    }

    [Test]
    public async Task Second_claim_is_refused_and_does_not_replace_the_verdict() {
        var gate = Gate();
        gate.TryStartReap("first", () => Task.CompletedTask);

        var second = gate.TryStartReap("second", () => Task.CompletedTask);

        await Assert.That(second).IsFalse();
        await Assert.That(gate.ReadVerdict()!.Reason).IsEqualTo("first");
    }

    [Test]
    public async Task A_throwing_starter_releases_the_claim_and_publishes_nothing() {
        var gate = Gate();

        await Assert.That(() => gate.TryStartReap("x", () => throw new InvalidOperationException("boom")))
            .Throws<InvalidOperationException>();

        await Assert.That(gate.ReadVerdict()).IsNull();
        await Assert.That(gate.TryStartReap("y", () => Task.CompletedTask)).IsTrue();
    }

    [Test]
    public async Task The_window_bit_is_read_before_the_starter_runs() {
        var inside = true;
        var gate   = new ReapVerdictGate(() => inside, NullLogger.Instance);

        gate.TryStartReap("r", () => { inside = false; return Task.CompletedTask; });

        await Assert.That(gate.ReadVerdict()!.ReapedInsideLaunchWindow).IsTrue();
    }

    [Test]
    public async Task Gated_send_is_refused_after_a_launch_window_verdict() {
        var gate = Gate(insideWindow: true);
        gate.TryStartReap("r", () => Task.CompletedTask);
        var sent = false;

        var initiated = gate.TryInitiateNonFailureStatusSend(() => { sent = true; return Task.CompletedTask; }, out _);

        await Assert.That(initiated).IsFalse();
        await Assert.That(sent).IsFalse();
    }

    [Test]
    public async Task Gated_send_is_initiated_after_a_post_window_verdict() {
        var gate = Gate(insideWindow: false);
        gate.TryStartReap("r", () => Task.CompletedTask);

        var initiated = gate.TryInitiateNonFailureStatusSend(() => Task.CompletedTask, out _);

        await Assert.That(initiated).IsTrue();
    }

    [Test]
    public async Task A_verdict_cannot_publish_between_the_gate_check_and_the_send() {
        var gate      = Gate();
        var published = false;
        gate.BeforeGatedSendHookForTest = () => {
            // Runs while the gate's lock is held. A claim from another thread must block here.
            var racer = Task.Run(() => gate.TryStartReap("late", () => Task.CompletedTask));
            published = racer.Wait(TimeSpan.FromMilliseconds(200));
        };

        gate.TryInitiateNonFailureStatusSend(() => Task.CompletedTask, out _);

        await Assert.That(published).IsFalse();
    }

    [Test]
    public async Task TakeReap_returns_the_started_task() {
        var gate = Gate();
        var tcs  = new TaskCompletionSource();
        gate.TryStartReap("r", () => tcs.Task);

        // object cast: TUnit's Assert.That special-cases a Task-typed value as something to await,
        // which would hang here since tcs.Task is never completed by this test.
        await Assert.That((object?)gate.TakeReap()).IsSameReferenceAs(tcs.Task);
    }

    [Test]
    public async Task The_reason_is_sanitised_to_one_line() {
        var gate = Gate();
        gate.TryStartReap("line one\nline two", () => Task.CompletedTask);

        await Assert.That(gate.ReadVerdict()!.Reason).IsEqualTo("line one line two");
    }
}
