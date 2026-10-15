using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap daemon status</c> must distinguish a daemon that is actually serving from one whose PID is
/// live but whose local control socket does not yet accept a connection — it has taken its lock but
/// has not finished binding. <see cref="DaemonCommands.DescribeRunningDaemon"/> is the pure classifier
/// that maps the two liveness signals (validated PID, reachable control socket) to the reported line,
/// so the distinction is testable without a real socket (the socket leg — including that an older
/// daemon which cannot answer Hello is still reachable — is <c>HelloProbeTests</c>).
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

    [Test]
    public async Task DescribePriority_names_the_word_and_the_daemon_for_the_background_band() {
        await Assert.That(DaemonCommands.DescribePriority("alexey", "adaptive"))
            .IsEqualTo("  priority: loaded as adaptive — background priority; run `kcap daemon service refresh --name alexey --force` to reload (ends this daemon's hosted agents)");
        await Assert.That(DaemonCommands.DescribePriority("alexey", "background")).IsNotNull();
    }

    [Test]
    public async Task DescribePriority_is_silent_for_positive_and_unknown_words() {
        await Assert.That(DaemonCommands.DescribePriority("alexey", "daemon")).IsNull();
        await Assert.That(DaemonCommands.DescribePriority("alexey", "interactive")).IsNull();
        await Assert.That(DaemonCommands.DescribePriority("alexey", "app")).IsNull();
        await Assert.That(DaemonCommands.DescribePriority("alexey", null)).IsNull();
    }
}
