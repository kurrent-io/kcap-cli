using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Cross-runtime: a runtime that will not queue or write input fails with
/// <see cref="InputNotAdmittedException"/> on both send entry points, not just the acknowledging one
/// — the per-runtime suites (<c>AntigravityRuntimeLifecycleTests</c>,
/// <c>AcpHostedAgentRuntimeTests</c>) cover the rest of each runtime's own behavior.</summary>
public class InputAdmissionTests {
    [Test]
    public async Task Antigravity_full_queue_refuses_with_input_not_admitted_on_both_send_paths() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1);
        await rt.SendUserInputAsync("first");

        // Turn 1 must be genuinely executing (its init read, freeing the capacity-1 slot) before
        // "queued" is sent, or "queued" races the worker's own dequeue and is rejected instead.
        await rt.WaitForConversationIdAsync(CancellationToken.None);
        await rt.SendUserInputAsync("queued");

        await Assert.That(() => rt.SendUserInputAsync("refused")).Throws<InputNotAdmittedException>();
        await Assert.That(async () => await rt.SendUserInputAndWaitForWriteAsync("refused-ack")).Throws<InputNotAdmittedException>();
    }

    [Test]
    public async Task Antigravity_terminal_runtime_refuses_the_acknowledging_path_with_input_not_admitted() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime();
        await rt.SendUserInputAsync("first");
        await rt.TerminateAsync(TimeSpan.FromSeconds(1));
        await Assert.That(async () => await rt.SendUserInputAndWaitForWriteAsync("late")).Throws<InputNotAdmittedException>();
    }
}
