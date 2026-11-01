using Capacitor.Cli.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Services;

public class WindowsServiceManagerTests {
    const string Daemon = @"C:\Users\alice\AppData\Local\kcap\current\kcap-daemon.exe";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly FakeServicePipe _pipe = new();
    readonly FakeServiceEnrollmentStore _store = new();
    readonly FakeTaskManager _tasks = new();
    readonly Dictionary<string, string> _written = [];

    WindowsServiceManager Manager() => new(Config.Root, _pipe, _store, _tasks,
        stopDaemon: id => {
            _tasks.Calls.Add($"stop {id}");
            return !_tasks.DaemonStopFails;
        },
        writeFile: (path, content, _) => _written[path] = content);

    static ServiceSpec Spec(string id = "laptop", int? maxAgents = null) => new(
        id, Daemon, @"C:\Users\alice\.config\kcap\daemon-laptop.log",
        new Dictionary<string, string> { ["KCAP_PROFILE"] = "work", ["PATH"] = @"C:\nvm" },
        maxAgents is { } n ? ["--max-agents", n.ToString(System.Globalization.CultureInfo.InvariantCulture)] : []);

    [Test]
    public async Task Install_writes_the_environment_and_enrollment_then_enrolls() {
        var manager = Manager();

        var ok = manager.Install(Spec(maxAgents: 3), startNow: true, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        var file = WindowsServiceManager.EnvironmentFilePath(Config.Root, "laptop");
        await Assert.That(_store.Current).IsEqualTo(new ServiceEnrollment("laptop", Daemon, "work", 3, file));
        await Assert.That(_written[file]).Contains("\"PATH\":\"C:\\\\nvm\"");
        await Assert.That(_pipe.Verbs).IsEquivalentTo(["enroll"]);
    }

    /// A rename keeps the old name's environment until the old name is retired, which happens once the new one is ready
    [Test]
    public async Task Retiring_a_displaced_name_deletes_its_environment_file() {
        var old = Config.CreateFile("daemon-service-old.env.json", "{}");
        _store.Current = new ServiceEnrollment("old", Daemon, "work", null, old);
        var manager = Manager();

        manager.Install(Spec(), startNow: true, out _);
        var keptThroughInstall = File.Exists(old);
        var ok = manager.Uninstall("old", out _);

        await Assert.That(keptThroughInstall).IsTrue();
        await Assert.That(ok).IsTrue();
        await Assert.That(File.Exists(old)).IsFalse();
        await Assert.That(_store.Current!.Name).IsEqualTo("laptop");
    }

    /// The verify rollback uninstalls the new name; unenrolling instead could empty the service and remove it
    [Test]
    public async Task Uninstalling_a_name_that_displaced_another_enrolls_the_other_again() {
        var old = Config.CreateFile("daemon-service-old.env.json", "{}");
        var displaced = new ServiceEnrollment("old", Daemon, "work", null, old);
        _store.Current = displaced;
        var manager = Manager();
        manager.Install(Spec(), startNow: true, out _);

        var ok = manager.Uninstall("laptop", out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(_store.Current).IsEqualTo(displaced);
        await Assert.That(_pipe.Verbs).IsEquivalentTo(["enroll", "enroll"]);
    }

    [Test]
    public async Task Uninstalling_after_a_same_name_reinstall_unenrolls() {
        _store.Current = new ServiceEnrollment("laptop", Daemon, "work", null, WindowsServiceManager.EnvironmentFilePath(Config.Root, "laptop"));
        var manager = Manager();
        manager.Install(Spec(), startNow: true, out _);

        manager.Uninstall("laptop", out _);

        await Assert.That(_store.Current).IsNull();
        await Assert.That(_pipe.Verbs).IsEquivalentTo(["enroll", "unenroll"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Install_without_start_stops_after_enrolling() {
        Manager().Install(Spec(), startNow: false, out _);

        await Assert.That(_pipe.Verbs).IsEquivalentTo(["enroll", "stop"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Install_without_start_says_so_when_the_daemon_it_could_not_stop_still_runs() {
        _pipe.Answer = verb => verb == "stop"
            ? new ServicePipeResponse(false, "busy")
            : new ServicePipeResponse(true, Status: new ServicePipeStatus("running", Pid: 4242));

        var ok = Manager().Install(Spec(), startNow: false, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("its daemon is running");
        await Assert.That(_store.Current).IsNotNull();
    }

    /// The daemon is stopped before the task goes: retiring the task alone kills it, agents and all
    [Test]
    public async Task A_scheduled_task_for_the_name_is_stopped_then_retired_before_the_service_takes_it() {
        _tasks.Installed.Add("laptop");

        var ok = Manager().Install(Spec(), startNow: true, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(_tasks.Calls).IsEquivalentTo(["stop laptop", "uninstall laptop"], CollectionOrdering.Matching);
        await Assert.That(_tasks.Installed).IsEmpty();
    }

    [Test]
    public async Task Without_a_scheduled_task_no_daemon_is_stopped() {
        Manager().Install(Spec(), startNow: true, out _);

        await Assert.That(_tasks.Calls).IsEmpty();
    }

    /// Retiring the task now would be the kill the graceful stop exists to avoid
    [Test]
    public async Task A_task_whose_daemon_does_not_stop_gracefully_is_left_running_it() {
        _tasks.Installed.Add("laptop");
        _tasks.DaemonStopFails = true;

        var ok = Manager().Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("did not stop gracefully");
        await Assert.That(_tasks.Calls).IsEquivalentTo(["stop laptop"]);
        await Assert.That(_tasks.Installed).Contains("laptop");
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    [Test]
    public async Task A_refused_enroll_restores_the_previous_enrollment_and_the_task() {
        var previous = new ServiceEnrollment("old", Daemon, null, null, null);
        _store.Current = previous;
        _tasks.Installed.Add("laptop");
        _tasks.Running.Add("laptop");
        _pipe.Answer = _ => new ServicePipeResponse(false, "the daemon path does not exist");

        var ok = Manager().Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsEqualTo("the daemon path does not exist");
        await Assert.That(_store.Current).IsEqualTo(previous);
        await Assert.That(_tasks.Calls).IsEquivalentTo(["stop laptop", "uninstall laptop", "install laptop"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_task_restored_after_a_refused_enroll_starts_only_if_it_was_running() {
        _tasks.Installed.Add("laptop");
        _pipe.Answer = _ => new ServicePipeResponse(false, "refused");

        Manager().Install(Spec(), startNow: true, out _);

        await Assert.That(_tasks.Calls).IsEquivalentTo(["stop laptop", "uninstall laptop", "install laptop stopped"], CollectionOrdering.Matching);
    }

    /// The restored enrollment names the same file, so leaving the refused install's contents in it would hand
    /// them to the daemon at its next launch
    [Test]
    public async Task A_refused_reinstall_gives_the_environment_file_its_contents_back() {
        var file = Config.CreateFile("daemon-service-laptop.env.json", """{"KCAP_PROFILE":"old"}""");
        _store.Current = new ServiceEnrollment("laptop", Daemon, "old", null, file);
        _pipe.Answer = _ => new ServicePipeResponse(false, "refused");

        Manager().Install(Spec(), startNow: true, out _);

        await Assert.That(_written[file]).IsEqualTo("""{"KCAP_PROFILE":"old"}""");
    }

    /// A retire can fail after it has ended the task's daemon, as one the user may not delete does; the task is
    /// started again so the daemon is not left with no supervisor and nothing running
    [Test]
    public async Task A_task_that_will_not_go_is_started_again_and_nothing_is_enrolled() {
        _tasks.Installed.Add("laptop");
        _tasks.Running.Add("laptop");
        _tasks.RefuseUninstall = true;

        var ok = Manager().Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("task is busy");
        await Assert.That(_tasks.Calls).IsEquivalentTo(["stop laptop", "uninstall laptop", "start laptop"], CollectionOrdering.Matching);
        await Assert.That(_store.Current).IsNull();
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    /// The reverse of retiring a task: the daemon stops keeping its agents for the task's daemon to resume, and the
    /// task is registered only once the service has let go of the name
    [Test]
    public async Task Moving_to_a_task_stops_the_daemon_then_unenrolls_then_registers_and_starts_the_task() {
        var file = Config.CreateFile("daemon-service-laptop.env.json", "{}");
        _store.Current = new ServiceEnrollment("laptop", Daemon, "work", null, file);
        _pipe.Answer = verb => {
            _tasks.Calls.Add(verb);
            return new ServicePipeResponse(true, Status: new ServicePipeStatus("running", Pid: 4242));
        };

        var ok = Manager().MoveToTask(Spec(), out var started, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(started).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(_tasks.Calls).IsEquivalentTo(
            ["status", "stop laptop", "unenroll", "install laptop stopped", "start laptop"], CollectionOrdering.Matching);
        await Assert.That(_store.Current).IsNull();
        await Assert.That(File.Exists(file)).IsFalse();
    }

    [Test]
    public async Task A_daemon_the_service_holds_stopped_gets_its_task_without_a_start() {
        _store.Current = new ServiceEnrollment("laptop", Daemon, "work", null, null);
        _pipe.Answer = _ => new ServicePipeResponse(true, Status: new ServicePipeStatus("stopped"));

        var ok = Manager().MoveToTask(Spec(), out var started, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(started).IsFalse();
        await Assert.That(error).IsNull();
        await Assert.That(_tasks.Calls).IsEquivalentTo(["install laptop stopped"]);
        await Assert.That(_store.Current).IsNull();
    }

    /// Unenrolling now would have the service stop it with its agents ended
    [Test]
    public async Task A_daemon_that_does_not_stop_gracefully_stays_with_the_service() {
        var enrollment = new ServiceEnrollment("laptop", Daemon, "work", null, null);
        _store.Current = enrollment;
        _tasks.DaemonStopFails = true;

        var ok = Manager().MoveToTask(Spec(), out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("did not stop gracefully");
        await Assert.That(_pipe.Verbs).IsEquivalentTo(["status"]);
        await Assert.That(_store.Current).IsEqualTo(enrollment);
        await Assert.That(_tasks.Installed).IsEmpty();
    }

    [Test]
    public async Task A_service_that_does_not_answer_keeps_the_daemon_and_no_task_is_written() {
        var enrollment = new ServiceEnrollment("laptop", Daemon, "work", null, null);
        _store.Current = enrollment;
        _pipe.Answer = _ => null;

        var ok = Manager().MoveToTask(Spec(), out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsEqualTo("kcap-service did not answer");
        await Assert.That(_store.Current).IsEqualTo(enrollment);
        await Assert.That(_tasks.Calls).IsEmpty();
    }

    /// With no task to run it, the daemon goes back to the service with the environment it was enrolled with
    [Test]
    public async Task A_refused_task_registration_enrolls_the_daemon_with_the_service_again() {
        var file = Config.CreateFile("daemon-service-laptop.env.json", """{"KCAP_PROFILE":"work"}""");
        var enrollment = new ServiceEnrollment("laptop", Daemon, "work", null, file);
        _store.Current = enrollment;
        _tasks.RefuseInstall = true;

        var ok = Manager().MoveToTask(Spec(), out var started, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(started).IsFalse();
        await Assert.That(error).Contains("access is denied");
        await Assert.That(_store.Current).IsEqualTo(enrollment);
        await Assert.That(_written[file]).IsEqualTo("""{"KCAP_PROFILE":"work"}""");
        await Assert.That(_pipe.Verbs).IsEquivalentTo(["status", "unenroll", "enroll"], CollectionOrdering.Matching);
        await Assert.That(_tasks.Installed).IsEmpty();
    }

    /// An invalid cap must reach the install that reports it, not match an uncapped enrollment as current
    [Test]
    public async Task EnrolledAs_refuses_an_agent_cap_an_install_would_refuse() {
        _store.Current = new ServiceEnrollment("laptop", Daemon, "work", null, null);
        var manager = Manager();

        await Assert.That(manager.EnrolledAs("laptop", "work", null)).IsTrue();
        await Assert.That(() => manager.EnrolledAs("laptop", "work", "abc")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Query_maps_the_services_answer() {
        _store.Current = new ServiceEnrollment("laptop", Daemon, null, null, null);
        var manager = Manager();

        var running = manager.Query("laptop");
        _pipe.Answer = _ => new ServicePipeResponse(true, Status: new ServicePipeStatus("stopped"));
        var stopped = manager.Query("laptop");
        _pipe.Answer = _ => null;
        var silent = manager.Query("laptop");

        await Assert.That(running).IsEqualTo(new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, Daemon, 4242));
        await Assert.That(stopped).IsEqualTo(new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Installed, Daemon, null));
        await Assert.That(silent.Probe).IsEqualTo(LabelProbe.Unknown);
        await Assert.That(() => manager.Status("laptop")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Another_name_or_no_enrollment_is_not_installed_and_never_asks_the_service() {
        _store.Current = new ServiceEnrollment("other", Daemon, null, null, null);
        var manager = Manager();

        await Assert.That(manager.Query("laptop").State).IsEqualTo(ServiceState.NotInstalled);
        await Assert.That(manager.Start("laptop", out var error)).IsFalse();
        await Assert.That(error).Contains("not enrolled");
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    [Test]
    public async Task Uninstall_unenrolls_and_removes_the_enrollment_and_its_environment() {
        var file = Config.CreateFile("daemon-service-laptop.env.json", "{}");
        _store.Current = new ServiceEnrollment("laptop", Daemon, null, null, file);
        _pipe.Answer = _ => new ServicePipeResponse(true, Status: new ServicePipeStatus("stopped"));

        var ok = Manager().Uninstall("laptop", out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(_pipe.Verbs).IsEquivalentTo(["unenroll"]);
        await Assert.That(_store.Current).IsNull();
        await Assert.That(File.Exists(file)).IsFalse();
    }

    [Test]
    public async Task Uninstall_keeps_the_enrollment_when_the_service_does_not_answer() {
        _store.Current = new ServiceEnrollment("laptop", Daemon, null, null, null);
        _pipe.Answer = _ => null;

        var ok = Manager().Uninstall("laptop", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsEqualTo("kcap-service did not answer");
        await Assert.That(_store.Current).IsNotNull();
    }

    [Test]
    public async Task The_unit_environment_is_read_back_from_the_captured_file() {
        var file = Config.CreateFile("daemon-service-laptop.env.json", """{"KCAP_PROFILE":"work"}""");
        _store.Current = new ServiceEnrollment("laptop", Daemon, null, null, file);

        var env = Manager().UnitEnvironment("laptop");

        await Assert.That(env!["KCAP_PROFILE"]).IsEqualTo("work");
    }

    /// <summary>kcap-service reads the environment file at each launch, so setting a variable is a file write
    /// the next start picks up; the previous file comes back for a refresh that cannot apply it.</summary>
    [Test]
    public async Task SetEnvironment_rewrites_the_environment_file_and_returns_the_old_one() {
        const string previous = """{"KCAP_PROFILE":"work"}""";
        var file = Config.CreateFile("daemon-service-laptop.env.json", previous);
        _store.Current = new ServiceEnrollment("laptop", Daemon, null, null, file);

        var replaced = Manager().SetEnvironment("laptop", "PATH", @"C:\npm");

        await Assert.That(replaced).IsEqualTo(previous);
        await Assert.That(_written[file]).Contains("\"PATH\":\"C:\\\\npm\"");
        await Assert.That(_written[file]).Contains("\"KCAP_PROFILE\":\"work\"");
    }

    [Test]
    public async Task SetEnvironment_leaves_a_current_value_alone() {
        var file = Config.CreateFile("daemon-service-laptop.env.json", """{"PATH":"C:\\npm"}""");
        _store.Current = new ServiceEnrollment("laptop", Daemon, null, null, file);

        await Assert.That(Manager().SetEnvironment("laptop", "PATH", @"C:\npm")).IsNull();
        await Assert.That(_written).IsEmpty();
    }

    [Test]
    public async Task GenerateFiles_is_the_captured_environment_as_json() {
        var files = Manager().GenerateFiles(Spec());

        await Assert.That(files.Single().Path).IsEqualTo(WindowsServiceManager.EnvironmentFilePath(Config.Root, "laptop"));
        await Assert.That(files.Single().Content).StartsWith("{");
    }
}
