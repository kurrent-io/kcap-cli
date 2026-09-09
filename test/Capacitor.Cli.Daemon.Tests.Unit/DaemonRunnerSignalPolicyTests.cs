using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.SignalRequestsShutdown"/>: which POSIX signals become a cooperative shutdown.
/// SIGHUP is a hangup only for a foreground run, which has a terminal to lose. A supervised or detached
/// daemon has none, and the kernel still delivers SIGHUP to every member of the daemon's own process
/// group when that group is orphaned while one member is stopped. Shutting down on it exits 0, which
/// launchd's <c>KeepAlive SuccessfulExit=false</c> reads as deliberate and never restarts.
/// </summary>
public class DaemonRunnerSignalPolicyTests {
    [Test]
    [Arguments(SupervisionMode.Supervised)]
    [Arguments(SupervisionMode.Detached)]
    public async Task SIGHUP_is_ignored_by_a_daemon_without_a_terminal(SupervisionMode mode) {
        await Assert.That(DaemonRunner.SignalRequestsShutdown(PosixSignal.SIGHUP, mode)).IsFalse();
    }

    [Test]
    public async Task SIGHUP_stops_a_foreground_run() {
        await Assert.That(DaemonRunner.SignalRequestsShutdown(PosixSignal.SIGHUP, SupervisionMode.Foreground)).IsTrue();
    }

    [Test]
    [Arguments(PosixSignal.SIGINT,  SupervisionMode.Supervised)]
    [Arguments(PosixSignal.SIGTERM, SupervisionMode.Supervised)]
    [Arguments(PosixSignal.SIGQUIT, SupervisionMode.Supervised)]
    [Arguments(PosixSignal.SIGINT,  SupervisionMode.Detached)]
    [Arguments(PosixSignal.SIGTERM, SupervisionMode.Detached)]
    [Arguments(PosixSignal.SIGQUIT, SupervisionMode.Detached)]
    [Arguments(PosixSignal.SIGINT,  SupervisionMode.Foreground)]
    [Arguments(PosixSignal.SIGTERM, SupervisionMode.Foreground)]
    [Arguments(PosixSignal.SIGQUIT, SupervisionMode.Foreground)]
    public async Task Every_other_signal_stops_the_daemon_whatever_the_mode(PosixSignal signal, SupervisionMode mode) {
        await Assert.That(DaemonRunner.SignalRequestsShutdown(signal, mode)).IsTrue();
    }
}
