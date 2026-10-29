using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class DaemonPidLivenessTests {
    const string Name = "d";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    void WritePid(string content) => Daemons.CreateFile(Path.GetFileName(Daemons.Store.PidPath(Name)), content);

    [Test]
    public async Task No_pid_file_is_not_running() =>
        await Assert.That(DaemonPidLiveness.IsRunning(Daemons.Store, Name)).IsFalse();

    [Test]
    public async Task An_unparseable_pid_file_is_not_running() {
        WritePid("not a pid\n");

        await Assert.That(DaemonPidLiveness.IsRunning(Daemons.Store, Name)).IsFalse();
    }

    [Test]
    public async Task A_live_pid_with_its_token_is_running() {
        WritePid($"{Environment.ProcessId}\n{ProcessStartToken.ForCurrent()}\n");

        await Assert.That(DaemonPidLiveness.IsRunning(Daemons.Store, Name)).IsTrue();
    }

    [Test]
    public async Task Live_daemons_lists_only_the_live_pids() {
        Daemons.CreateFile(Path.GetFileName(Daemons.Store.PidPath("live")), $"{Environment.ProcessId}\n{ProcessStartToken.ForCurrent()}\n");
        Daemons.CreateFile(Path.GetFileName(Daemons.Store.PidPath("stale")), "not a pid\n");

        await Assert.That(DaemonPidLiveness.LiveDaemons(Daemons.Store)).IsEquivalentTo(["live"]);
    }

    /// A same-scheme token that differs is a recycled pid, even though the pid itself is alive.
    [Test]
    public async Task A_live_pid_with_another_incarnations_token_is_not_running() {
        var token = ProcessStartToken.ForCurrent();
        await Assert.That(token).IsNotNull();
        WritePid($"{Environment.ProcessId}\n{token![..(token.LastIndexOf(':') + 1)]}0\n");

        await Assert.That(DaemonPidLiveness.IsRunning(Daemons.Store, Name)).IsFalse();
    }
}
