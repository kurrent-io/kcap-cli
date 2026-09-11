using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap daemon status</c> must distinguish a daemon that is actually serving from one whose PID is
/// live but whose local control socket does not yet answer — it has taken its lock but has not
/// finished binding and connecting. <see cref="DaemonCommands.DescribeRunningDaemon"/> is the pure
/// classifier that maps the two liveness signals (validated PID, well-formed Hello) to the reported
/// line, so the distinction is testable without a real socket (the socket leg is
/// <c>HelloProbeTests</c>).
/// </summary>
public class DaemonStatusServingTests {
    [Test]
    public async Task DescribeRunningDaemon_SocketAnswers_ReportsPlainRunning() {
        await Assert.That(DaemonCommands.DescribeRunningDaemon(12345, serving: true))
            .IsEqualTo("running (PID 12345)");
    }

    [Test]
    public async Task DescribeRunningDaemon_SocketSilent_ReportsStartingNotYetServing() {
        await Assert.That(DaemonCommands.DescribeRunningDaemon(12345, serving: false))
            .IsEqualTo("running (PID 12345, starting — not yet serving)");
    }
}
