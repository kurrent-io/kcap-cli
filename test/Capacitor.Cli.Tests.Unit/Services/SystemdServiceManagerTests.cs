using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>What the verify engine reads from a systemd manager: the daemon's pid from one query, and a
/// refused systemctl call reported as false rather than thrown.</summary>
public class SystemdServiceManagerTests {
    [TempHome] public required TempHome Home { get; init; }

    const string Id = "test";

    static readonly string UnitFile = Path.Combine(".config", "systemd", "user", SystemdUnit.UnitName(Id));

    [Test]
    public async Task Query_reports_the_main_pid_of_an_active_unit() {
        var calls = new List<string[]>();
        var mgr = new SystemdServiceManager(Home, runProcess: (_, args) => {
            calls.Add(args);
            return (0, "ActiveState=active\nUnitFileState=enabled\nMainPID=4242\n", "");
        });

        var q = mgr.Query(Id);

        await Assert.That(q.JobPid).IsEqualTo(4242);
        await Assert.That(q.State).IsEqualTo(ServiceState.Running);
        await Assert.That(calls).Count().IsEqualTo(1);
        await Assert.That(calls[0]).IsEquivalentTo(SystemdUnit.ShowStateArgs(Id));
    }

    [Test]
    public async Task Start_and_stop_report_a_refusal() {
        var mgr = new SystemdServiceManager(Home, runProcess: (_, _) => (5, "", "Unit not found."));

        await Assert.That(mgr.Start(Id, out var startError)).IsFalse();
        await Assert.That(startError).Contains("Unit not found.");
        await Assert.That(mgr.Stop(Id, out var stopError)).IsFalse();
        await Assert.That(stopError).Contains("Unit not found.");
    }

    [Test]
    public async Task Uninstall_keeps_the_unit_when_a_failed_disable_leaves_the_daemon_running() {
        var path = Home.CreateFile(UnitFile, "unit");
        var mgr = new SystemdServiceManager(Home, runProcess: (_, args) =>
            args.Contains("disable") ? (1, "", "Access denied")
            : args.Contains("show") ? (0, "ActiveState=active\nUnitFileState=enabled\nMainPID=4242\n", "")
            : (0, "", ""));

        var ok = mgr.Uninstall(Id, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("Access denied");
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task Uninstall_keeps_the_unit_when_systemd_cannot_be_asked() {
        var path = Home.CreateFile(UnitFile, "unit");
        var mgr = new SystemdServiceManager(Home, runProcess: (_, _) => (1, "", "Failed to connect to bus"));

        await Assert.That(mgr.Uninstall(Id, out _)).IsFalse();
        await Assert.That(File.Exists(path)).IsTrue();
    }

    /// <summary>Disabling a unit systemd never loaded fails, but there is no daemon to leave behind.</summary>
    [Test]
    public async Task Uninstall_removes_the_unit_when_the_daemon_is_not_running() {
        var path = Home.CreateFile(UnitFile, "unit");
        var mgr = new SystemdServiceManager(Home, runProcess: (_, args) =>
            args.Contains("disable") ? (1, "", "Unit file does not exist")
            : args.Contains("show") ? (0, "ActiveState=inactive\nUnitFileState=\nMainPID=0\n", "")
            : (0, "", ""));

        var ok = mgr.Uninstall(Id, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Uninstall_keeps_the_unit_when_a_failed_disable_leaves_it_deactivating() {
        var path = Home.CreateFile(UnitFile, "unit");
        var mgr = new SystemdServiceManager(Home, runProcess: (_, args) =>
            args.Contains("disable") ? (1, "", "Access denied")
            : args.Contains("show") ? (0, "ActiveState=deactivating\nUnitFileState=enabled\nMainPID=4242\n", "")
            : (0, "", ""));

        await Assert.That(mgr.Uninstall(Id, out _)).IsFalse();
        await Assert.That(File.Exists(path)).IsTrue();
    }
}
