using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// A task's job pid is its running daemon only when the task started it: the action is
/// `conhost --headless cmd /c wrapper`, so the daemon sits under a live cmd.exe under conhost.exe.
public class WindowsTaskOwnedPidTests {
    static Dictionary<int, WindowsProcessEntry> Table(params (int Pid, int Parent, string Exe)[] rows) =>
        rows.ToDictionary(r => r.Pid, r => new WindowsProcessEntry(r.Parent, r.Exe));

    [Test]
    public async Task A_daemon_under_the_wrapper_and_its_console_host_is_the_job() {
        var table = Table((10, 1, "conhost.exe"), (20, 10, "cmd.exe"), (30, 20, "kcap-daemon.exe"));

        await Assert.That(WindowsScheduledTaskServiceManager.TaskOwnedPid(30, table)).IsEqualTo(30);
    }

    [Test]
    public async Task Image_names_compare_case_insensitively() {
        var table = Table((10, 1, "CONHOST.EXE"), (20, 10, "Cmd.exe"), (30, 20, "kcap-daemon.exe"));

        await Assert.That(WindowsScheduledTaskServiceManager.TaskOwnedPid(30, table)).IsEqualTo(30);
    }

    [Test]
    public async Task A_daemon_started_by_hand_is_not_the_job() {
        var table = Table((5, 1, "explorer.exe"), (6, 5, "kcap.exe"), (30, 6, "kcap-daemon.exe"));

        await Assert.That(WindowsScheduledTaskServiceManager.TaskOwnedPid(30, table)).IsNull();
    }

    [Test]
    public async Task A_daemon_whose_wrapper_is_gone_is_not_the_job() {
        var table = Table((30, 20, "kcap-daemon.exe"));

        await Assert.That(WindowsScheduledTaskServiceManager.TaskOwnedPid(30, table)).IsNull();
    }

    [Test]
    public async Task A_cmd_not_hosted_by_conhost_is_not_the_wrapper() {
        var table = Table((10, 1, "WindowsTerminal.exe"), (20, 10, "cmd.exe"), (30, 20, "kcap-daemon.exe"));

        await Assert.That(WindowsScheduledTaskServiceManager.TaskOwnedPid(30, table)).IsNull();
    }

    [Test]
    public async Task No_daemon_pid_or_a_dead_one_is_no_job() {
        var table = Table((10, 1, "conhost.exe"), (20, 10, "cmd.exe"));

        await Assert.That(WindowsScheduledTaskServiceManager.TaskOwnedPid(null, table)).IsNull();
        await Assert.That(WindowsScheduledTaskServiceManager.TaskOwnedPid(30, table)).IsNull();
    }
}
