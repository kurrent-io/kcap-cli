using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>The verify engines prove ownership against the job pid the manager reports, so a running
/// systemd unit must answer with its MainPID and a stopped one with none.</summary>
public class SystemdServiceManagerTests {
    [TempHome] public required TempHome Home { get; init; }

    static (int, string, string) Systemctl(string[] args, bool active) =>
        args.Contains("is-active") ? (active ? 0 : 3, active ? "active\n" : "inactive\n", "")
        : args.Contains("is-enabled") ? (0, "enabled\n", "")
        : args.Contains("--property=MainPID") ? (0, "4242\n", "")
        : (0, "", "");

    [Test]
    [Arguments(true, 4242)]
    [Arguments(false, null)]
    public async Task Query_reports_the_main_pid_only_while_running(bool active, int? expectedPid) {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ExecStart=/opt/kcap/kcap-daemon --name test\n");

        var query = new SystemdServiceManager(Home, runProcess: (_, args) => Systemctl(args, active)).Query("test");

        await Assert.That(query.State).IsEqualTo(active ? ServiceState.Running : ServiceState.Installed);
        await Assert.That(query.JobPid).IsEqualTo(expectedPid);
    }

    [Test]
    public async Task A_refused_start_or_stop_returns_false_with_the_error() {
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => (1, "", "denied"));

        await Assert.That(manager.Start("test", out var startError)).IsFalse();
        await Assert.That(startError).Contains("denied");
        await Assert.That(manager.Stop("test", out var stopError)).IsFalse();
        await Assert.That(stopError).Contains("denied");
    }

    [Test]
    [Arguments("active", false)]
    [Arguments("inactive", true)]
    public async Task A_refused_disable_removes_the_unit_only_once_it_is_inactive(string activeState, bool removed) {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ExecStart=/opt/kcap/kcap-daemon --name test\n");
        var manager = new SystemdServiceManager(Home, runProcess: (_, args) =>
            args.Contains("disable") ? (1, "", "timeout")
            : args.Contains("is-active") ? (activeState == "active" ? 0 : 3, activeState + "\n", "")
            : (0, "", ""));

        await Assert.That(manager.Uninstall("test", out _)).IsEqualTo(removed);
        await Assert.That(File.Exists(path)).IsEqualTo(!removed);
    }
}
