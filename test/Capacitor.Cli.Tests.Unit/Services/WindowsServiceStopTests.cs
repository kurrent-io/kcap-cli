using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Which daemon a service stop or uninstall may kill, and what an uninstall that cannot kill it
/// leaves behind.</summary>
public class WindowsServiceStopTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    const int DaemonPid = 100;

    sealed class FakeHost {
        public readonly Dictionary<int, WindowsProcessEntry> Table = [];
        public readonly List<string>                         Verbs = [];
        public readonly List<int>                            Killed = [];
        public string?                                       KillError;
        public int                                           DeleteExit;
        public bool                                          TaskExists = true;
        // Null: the definition kcap writes, for the wrapper under the test's config root.
        public string?                                       TaskXml;
        public bool                                          DefinitionUnreadable;
        public bool                                          ListingFails;

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
            if (args[0] == "/Delete") {
                if (DeleteExit != 0) return (DeleteExit, "", "ERROR: Access is denied.");
                TaskExists = false;
            }
            if (args[0] != "/Query") return (0, "", "");
            if (!args.Contains("/TN")) return ListingFails ? (1, "", "ERROR: Access is denied.") : (0, TaskExists ? "TaskName: \\kcap-daemon-laptop" : "", "");
            if (!TaskExists) return (1, "", "ERROR: The system cannot find the file specified.");
            if (args.Contains("/XML")) return DefinitionUnreadable ? (1, "", "ERROR: Access is denied.") : (0, TaskXml!, "");
            return (0, "TaskName: \\kcap-daemon-laptop", "");
        }

        public string? Kill(int pid) { Killed.Add(pid); return KillError; }
    }

    static readonly ServiceSpec Spec = new("laptop", "kcap-daemon.exe", "daemon.log", new Dictionary<string, string>(), []);

    WindowsScheduledTaskServiceManager Manager(FakeHost host) {
        host.TaskXml ??= WindowsTaskUnit.TaskXml(Spec, WindowsTaskUnit.WrapperPath(Config.Root, "laptop"));
        return new(Config.Root, daemonPid: _ => DaemonPid, schtasks: host.Schtasks,
            processTable: () => new Dictionary<int, WindowsProcessEntry>(host.Table), killTree: host.Kill);
    }

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

    [Test]
    public async Task Uninstall_whose_task_cannot_be_deleted_keeps_the_wrapper_and_reports_why() {
        var host = new FakeHost { DeleteExit = 1 };
        var wrapper = WindowsTaskUnit.WrapperPath(Config.Root, "laptop");
        await File.WriteAllTextAsync(wrapper, "@echo off\r\n");

        var ok = Manager(host).Uninstall("laptop", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("Access is denied");
        await Assert.That(File.Exists(wrapper)).IsTrue();
    }

    [Test]
    public async Task Uninstall_of_a_task_already_gone_succeeds() {
        var host = new FakeHost { DeleteExit = 1, TaskExists = false };

        var ok = Manager(host).Uninstall("laptop", out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
    }

    /// <summary>The task's own definition names its wrapper, so uninstalling removes that one: a task
    /// installed under another config root has it elsewhere, and a file left under this root is not it.</summary>
    [Test]
    public async Task Uninstall_removes_the_wrapper_the_task_runs_not_a_stray_one_under_this_root() {
        var stray = WindowsTaskUnit.WrapperPath(Config.Root, "laptop");
        await File.WriteAllTextAsync(stray, "@echo off\r\n");
        var elsewhere = Tmp.CreateFile("other-config/daemon-service-laptop.cmd", "@echo off\r\n");
        var host = new FakeHost { TaskXml = WindowsTaskUnit.TaskXml(Spec, elsewhere) };

        var ok = Manager(host).Uninstall("laptop", out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(File.Exists(elsewhere)).IsFalse();
        await Assert.That(File.Exists(stray)).IsTrue();
    }

    [Test]
    public async Task Uninstall_of_a_task_whose_definition_cannot_be_read_is_refused() {
        var host = new FakeHost { DefinitionUnreadable = true };

        var ok = Manager(host).Uninstall("laptop", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("Could not read the definition");
        await Assert.That(host.Verbs).DoesNotContain("/Delete");
    }

    /// <summary>A same-named task kcap did not write runs something else, so its wrapper is unknown and
    /// nothing under this config root is deleted on its behalf.</summary>
    [Test]
    public async Task Uninstall_of_a_task_with_an_action_kcap_did_not_write_is_refused() {
        var stray = WindowsTaskUnit.WrapperPath(Config.Root, "laptop");
        await File.WriteAllTextAsync(stray, "@echo off\r\n");
        var host = new FakeHost { TaskXml = "<Task><Actions><Exec><Command>notepad.exe</Command></Exec></Actions></Task>" };

        var ok = Manager(host).Uninstall("laptop", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("action kcap did not write");
        await Assert.That(host.Verbs).DoesNotContain("/Delete");
        await Assert.That(File.Exists(stray)).IsTrue();
    }

    /// <summary>A failed delete followed by a listing that fails says nothing about the task, so the
    /// wrapper it may still need is kept.</summary>
    [Test]
    public async Task Uninstall_whose_delete_and_listing_both_fail_keeps_the_wrapper() {
        var host = new FakeHost { DeleteExit = 1, ListingFails = true };
        var wrapper = WindowsTaskUnit.WrapperPath(Config.Root, "laptop");
        await File.WriteAllTextAsync(wrapper, "@echo off\r\n");

        var ok = Manager(host).Uninstall("laptop", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("Access is denied");
        await Assert.That(File.Exists(wrapper)).IsTrue();
    }
}
