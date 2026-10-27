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

    [Test]
    public async Task A_refused_reload_puts_the_previous_unit_back() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string unit = "[Service]\nEnvironment=PATH=/usr/bin\nExecStart=/opt/kcap/kcap-daemon --name test\n";
        File.WriteAllText(path, unit);
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => (1, "", "denied"));

        await Assert.That(() => manager.SetEnvironment("test", "PATH", "/usr/bin:/opt/agents")).Throws<InvalidOperationException>();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(unit);
    }

    [Test]
    public async Task SetEnvironment_returns_the_unit_it_replaced() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string unit = "[Service]\nEnvironment=PATH=/usr/bin\nExecStart=/opt/kcap/kcap-daemon --name test\n";
        File.WriteAllText(path, unit);
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => (0, "", ""));

        await Assert.That(manager.SetEnvironment("test", "PATH", "/usr/bin:/opt/agents")).IsEqualTo(unit);
        await Assert.That(SystemdUnit.EnvFromUnit(File.ReadAllText(path))["PATH"]).IsEqualTo("/usr/bin:/opt/agents");
        await Assert.That(manager.SetEnvironment("test", "PATH", "/usr/bin:/opt/agents")).IsNull();
    }

    /// <summary>A line kcap did not write is left alone, and that is a failure, not "already current".</summary>
    [Test]
    public async Task A_line_it_cannot_rewrite_is_reported_rather_than_read_as_current() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string unit = "[Service]\nEnvironment=PATH=/usr/bin KCAP_PROFILE=work\nExecStart=/opt/kcap/kcap-daemon --name test\n";
        File.WriteAllText(path, unit);
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => (0, "", ""));

        await Assert.That(() => manager.SetEnvironment("test", "PATH", "/usr/bin:/opt/agents")).Throws<InvalidOperationException>();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(unit);
    }

    [Test]
    public async Task A_systemctl_that_cannot_start_puts_the_previous_unit_back() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string unit = "[Service]\nEnvironment=PATH=/usr/bin\nExecStart=/opt/kcap/kcap-daemon --name test\n";
        File.WriteAllText(path, unit);
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => throw new System.ComponentModel.Win32Exception("no systemctl"));

        await Assert.That(() => manager.SetEnvironment("test", "PATH", "/usr/bin:/opt/agents")).Throws<System.ComponentModel.Win32Exception>();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(unit);
    }

    /// <summary>Without a user bus systemctl answers nothing, and the unit must read as unknown rather than
    /// absent, or a verify would install over a daemon that may be running.</summary>
    [Test]
    public async Task Query_without_a_reachable_systemd_is_unknown() {
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => (1, "", "Failed to connect to bus: No medium found"));

        var query = manager.Query("test");

        await Assert.That(query.Probe).IsEqualTo(LabelProbe.Unknown);
        await Assert.That(query.JobPid).IsNull();
    }

    /// <summary>The unit's binary comes from the file, so it stays known without systemd.</summary>
    [Test]
    public async Task Query_without_a_reachable_systemd_still_reads_the_units_binary() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ExecStart=/opt/kcap/kcap-daemon --name test\n");
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => (1, "", "Failed to connect to bus: No medium found"));

        var query = manager.Query("test");

        await Assert.That(query.Probe).IsEqualTo(LabelProbe.Unknown);
        await Assert.That(query.BinaryPath).IsEqualTo("/opt/kcap/kcap-daemon");
    }

    [Test]
    public async Task Query_of_a_disabled_unit_on_disk_is_loaded_and_installed() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ExecStart=/opt/kcap/kcap-daemon --name test\n");
        var manager = new SystemdServiceManager(Home, runProcess: (_, args) =>
            args.Contains("is-active") ? (3, "inactive\n", "") : args.Contains("is-enabled") ? (1, "disabled\n", "") : (0, "", ""));

        var query = manager.Query("test");

        await Assert.That(query.Probe).IsEqualTo(LabelProbe.Loaded);
        await Assert.That(query.State).IsEqualTo(ServiceState.Installed);
    }
}
