using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi.PiRpcRuntimeFakes;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// The review-flow-only guards on <see cref="PiRpcHostedAgentRuntime"/>: a blocking dialog, a round
/// that outlives its ceiling, and a command-shaped prompt all reap the runtime for a coded reason.
/// Every guard is gated on <c>reviewerGuards</c> being non-null, so an interactive launch (the
/// default) must see none of this behaviour — each guard test has an interactive sibling proving
/// that.
/// </summary>
public class PiReviewerRuntimeGuardTests {
    static readonly PiReviewerGuards Guards = new(TimeSpan.FromSeconds(600), TimeSpan.FromMilliseconds(100));

    static async Task<TerminationVerdict> VerdictAsync(PiRpcHostedAgentRuntime runtime) {
        for (var i = 0; i < 200 && runtime.ReadVerdict() is null; i++) await Task.Delay(10);
        return runtime.ReadVerdict() ?? throw new TimeoutException("no verdict was published");
    }

    [Test]
    [Arguments("select")]
    [Arguments("confirm")]
    [Arguments("input")]
    [Arguments("editor")]
    public async Task A_blocking_dialog_reaps_a_reviewer(string method) {
        var (runtime, process) = NewRuntime(reviewerGuards: Guards);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        process.Push(DialogRequest(method));

        await Assert.That((await VerdictAsync(runtime)).Reason).IsEqualTo("pi_reviewer_unexpected_dialog");
        await runtime.DisposeAsync();
        await Assert.That(process.TerminateCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task A_display_only_dialog_does_not_reap() {
        var (runtime, process) = NewRuntime(reviewerGuards: Guards);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        process.Push(DialogRequest("notify"));
        await Task.Delay(100);

        await Assert.That(runtime.ReadVerdict()).IsNull();
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task A_dialog_does_not_reap_an_interactive_launch() {
        var (runtime, process) = NewRuntime();
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        process.Push(DialogRequest("confirm"));
        await Task.Delay(100);

        await Assert.That(runtime.ReadVerdict()).IsNull();
        await runtime.DisposeAsync();
    }

    [Test]
    [Arguments("/llama")]
    [Arguments("  /skill:x")]
    public async Task A_command_prompt_is_never_written_and_reaps_a_reviewer(string prompt) {
        var (runtime, process) = NewRuntime(reviewerGuards: Guards);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        await Assert.That(async () => await runtime.SendUserInputAsync(prompt)).Throws<InvalidOperationException>();

        await Assert.That(process.Writes.Any(w => w.Contains("\"type\":\"prompt\""))).IsFalse();
        await Assert.That((await VerdictAsync(runtime)).Reason).IsEqualTo("pi_reviewer_prompt_is_command");
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task A_command_prompt_is_sent_as_usual_by_an_interactive_launch() {
        var (runtime, process) = NewRuntime();
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        await runtime.SendUserInputAsync("/llama");

        await Assert.That(process.Writes.Any(w => w.Contains("\"type\":\"prompt\""))).IsTrue();
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task A_round_that_outlives_the_limit_reaps_with_the_timeout_reason() {
        var time = new FakeTimeProvider();
        var (runtime, process) = NewRuntime(reviewerGuards: Guards, time: time);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);
        await runtime.SendUserInputAsync("review this");

        time.Advance(Guards.RoundLimit + TimeSpan.FromSeconds(1));

        await Assert.That((await VerdictAsync(runtime)).Reason).IsEqualTo("pi_reviewer_turn_timeout");
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task The_verdict_is_inside_the_launch_window_until_the_first_settle() {
        var (runtime, process) = NewRuntime(reviewerGuards: Guards);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        process.Push(DialogRequest("confirm"));

        await Assert.That((await VerdictAsync(runtime)).ReapedInsideLaunchWindow).IsTrue();
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task The_verdict_is_outside_the_launch_window_after_the_first_settle() {
        var (runtime, process) = NewRuntime(reviewerGuards: Guards);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);
        process.Push(AgentStart);
        process.Push(AgentSettled);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None);

        process.Push(DialogRequest("confirm"));

        await Assert.That((await VerdictAsync(runtime)).ReapedInsideLaunchWindow).IsFalse();
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task An_abort_write_that_never_completes_still_reaches_termination() {
        var (runtime, process) = NewRuntime(reviewerGuards: Guards);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);
        process.WriteOverride = json => json.Contains("\"type\":\"abort\"") ? new TaskCompletionSource().Task : Task.CompletedTask;

        process.Push(DialogRequest("confirm"));
        await VerdictAsync(runtime);
        await runtime.DisposeAsync();

        await Assert.That(process.TerminateCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task An_abort_write_that_throws_still_reaches_termination() {
        var (runtime, process) = NewRuntime(reviewerGuards: Guards);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);
        process.WriteOverride = json => json.Contains("\"type\":\"abort\"")
            ? Task.FromException(new IOException("stdin is gone")) : Task.CompletedTask;

        process.Push(DialogRequest("confirm"));
        await VerdictAsync(runtime);
        await runtime.DisposeAsync();

        await Assert.That(process.TerminateCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task A_child_that_exits_on_its_own_publishes_no_timeout_verdict() {
        var time = new FakeTimeProvider();
        var (runtime, process) = NewRuntime(reviewerGuards: Guards, time: time);
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);
        await runtime.SendUserInputAsync("review this");

        process.EndOfStream(exitCode: 0);
        await Task.Delay(100);
        time.Advance(Guards.RoundLimit + TimeSpan.FromSeconds(1));

        await Assert.That(runtime.ReadVerdict()).IsNull();
        await runtime.DisposeAsync();
    }
}
