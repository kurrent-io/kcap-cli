namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>A host-built daemon's last line names why it exits, so a stop reads differently from a crash.</summary>
public class DaemonRunnerExitReasonTests {
    [Test]
    public async Task A_recorded_stop_cause_is_the_reason() =>
        await Assert.That(DaemonRunner.ExitReason("received SIGTERM", supervisedRestart: false)).IsEqualTo("received SIGTERM");

    [Test]
    public async Task A_supervised_restart_names_the_update_over_any_stop_cause() =>
        await Assert.That(DaemonRunner.ExitReason("received SIGTERM", supervisedRestart: true)).Contains("restarting after an update");

    [Test]
    public async Task A_stop_with_no_recorded_cause_says_so() =>
        await Assert.That(DaemonRunner.ExitReason(null, supervisedRestart: false)).Contains("without a recorded stop request");
}
