namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.StartupPhase"/> is the pre-host breadcrumb: before the host (and its
/// <c>ILogger</c>) exists, a stall between lock acquisition and host construction would otherwise
/// leave no record of how far the boot got. Each call must land one greppable, phase-named line on
/// stderr so the last one printed names the last phase reached.
/// </summary>
public class DaemonRunnerStartupPhaseTests {
    [Test]
    [NotInParallel]   // Console is process-global
    public async Task StartupPhase_WritesTheNamedPhaseToStderr() {
        using var capture = ConsoleOutput.StartErrorCapture();

        DaemonRunner.StartupPhase("lock acquired");

        await Assert.That(capture.GetCapturedError()).Contains("[startup] lock acquired");
    }

    [Test]
    [NotInParallel]
    public async Task StartupPhase_KeepsPhasesInTheOrderTheyWereReached() {
        using var capture = ConsoleOutput.StartErrorCapture();

        DaemonRunner.StartupPhase("lock acquired");
        DaemonRunner.StartupPhase("boot checks done");

        var lines = capture.GetCapturedError();
        await Assert.That(lines.IndexOf("lock acquired", StringComparison.Ordinal))
            .IsLessThan(lines.IndexOf("boot checks done", StringComparison.Ordinal));
    }
}
