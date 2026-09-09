using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.SignalRequestsShutdown"/>: which POSIX signals become a cooperative shutdown.
/// SIGHUP is a hangup only when a standard stream is a terminal. Under launchd or systemd none is, and
/// the kernel still delivers SIGHUP to every member of the daemon's own process group when that group
/// is orphaned while one member is stopped. Shutting down on it exits 0, which launchd's
/// <c>KeepAlive SuccessfulExit=false</c> reads as deliberate and never restarts.
/// </summary>
public class DaemonRunnerSignalPolicyTests {
    [Test]
    public async Task SIGHUP_is_ignored_without_a_terminal() {
        await Assert.That(DaemonRunner.SignalRequestsShutdown(PosixSignal.SIGHUP, attachedToTerminal: false)).IsFalse();
    }

    [Test]
    public async Task SIGHUP_stops_a_daemon_attached_to_a_terminal() {
        await Assert.That(DaemonRunner.SignalRequestsShutdown(PosixSignal.SIGHUP, attachedToTerminal: true)).IsTrue();
    }

    [Test]
    [Arguments(PosixSignal.SIGINT,  false)]
    [Arguments(PosixSignal.SIGTERM, false)]
    [Arguments(PosixSignal.SIGQUIT, false)]
    [Arguments(PosixSignal.SIGINT,  true)]
    [Arguments(PosixSignal.SIGTERM, true)]
    [Arguments(PosixSignal.SIGQUIT, true)]
    public async Task Every_other_signal_stops_the_daemon_with_or_without_a_terminal(PosixSignal signal, bool attachedToTerminal) {
        await Assert.That(DaemonRunner.SignalRequestsShutdown(signal, attachedToTerminal)).IsTrue();
    }
}
