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
}
