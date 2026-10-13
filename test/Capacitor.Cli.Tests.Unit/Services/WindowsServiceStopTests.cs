using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Which daemon a service stop or uninstall may kill, and what an uninstall that cannot kill it
/// leaves behind.</summary>
public class WindowsServiceStopTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const int DaemonPid = 100;

    sealed class FakeHost {
        public readonly Dictionary<int, WindowsProcessEntry> Table = [];
        public readonly List<string>                         Verbs = [];
        public readonly List<int>                            Killed = [];
        public string?                                       KillError;

        public void TaskRunsTheDaemon() {
            Table[40]        = new(1, "conhost.exe");
            Table[50]        = new(40, "cmd.exe");
            Table[DaemonPid] = new(50, "kcap-daemon.exe");
        }

        public void DaemonStartedByHand() {
            Table[60]        = new(1, "explorer.exe");
            Table[DaemonPid] = new(60, "kcap-daemon.exe");
        }

        public (int, string, string) Schtasks(string[] args) {
            Verbs.Add(args[0]);
            // Ending the task takes its console host and wrapper with it; the daemon outlives them.
            if (args[0] == "/End") { Table.Remove(40); Table.Remove(50); }
            return (0, "", "");
        }

        public string? Kill(int pid) { Killed.Add(pid); return KillError; }
    }

    WindowsScheduledTaskServiceManager Manager(FakeHost host) =>
        new(Config.Root, daemonPid: _ => DaemonPid, schtasks: host.Schtasks,
            processTable: () => new Dictionary<int, WindowsProcessEntry>(host.Table), killTree: host.Kill);

    [Test]
    public async Task Stop_kills_the_daemon_the_task_was_running_when_asked() {
        var host = new FakeHost();
        host.TaskRunsTheDaemon();

        var ok = Manager(host).Stop("laptop", out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(host.Killed).IsEquivalentTo(new[] { DaemonPid });
    }

    [Test]
    public async Task Stop_leaves_a_daemon_started_by_hand_running() {
        var host = new FakeHost();
        host.DaemonStartedByHand();

        var ok = Manager(host).Stop("laptop", out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(host.Killed).IsEmpty();
    }

    [Test]
    public async Task Uninstall_leaves_a_daemon_started_by_hand_running() {
        var host = new FakeHost();
        host.DaemonStartedByHand();

        var ok = Manager(host).Uninstall("laptop", out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(host.Killed).IsEmpty();
        await Assert.That(host.Verbs).Contains("/Delete");
    }

    [Test]
    public async Task Uninstall_that_cannot_stop_the_daemon_keeps_the_task_and_reports_why() {
        var host = new FakeHost { KillError = "access is denied" };
        host.TaskRunsTheDaemon();
        var wrapper = WindowsTaskUnit.WrapperPath(Config.Root, "laptop");
        await File.WriteAllTextAsync(wrapper, "@echo off\r\n");

        var ok = Manager(host).Uninstall("laptop", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsEqualTo("access is denied");
        await Assert.That(host.Verbs).DoesNotContain("/Delete");
        await Assert.That(File.Exists(wrapper)).IsTrue();
    }

    [Test]
    public async Task Uninstall_removes_the_task_and_wrapper_once_the_daemon_is_stopped() {
        var host = new FakeHost();
        host.TaskRunsTheDaemon();
        var wrapper = WindowsTaskUnit.WrapperPath(Config.Root, "laptop");
        await File.WriteAllTextAsync(wrapper, "@echo off\r\n");

        var ok = Manager(host).Uninstall("laptop", out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(host.Killed).IsEquivalentTo(new[] { DaemonPid });
        await Assert.That(host.Verbs).Contains("/Delete");
        await Assert.That(File.Exists(wrapper)).IsFalse();
    }
}
