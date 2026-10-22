using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class WindowsServiceManagerTests {
    const string Daemon = @"C:\Users\alice\AppData\Local\kcap\current\kcap-daemon.exe";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly FakeServicePipe _pipe = new();
    readonly FakeServiceEnrollmentStore _store = new();
    readonly FakeTaskManager _tasks = new();
    readonly Dictionary<string, string> _written = [];

    WindowsServiceManager Manager() => new(Config.Root, _pipe, _store, _tasks, (path, content, _) => _written[path] = content);

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

    [Test]
    public async Task Install_without_start_stops_after_enrolling() {
        Manager().Install(Spec(), startNow: false, out _);

        await Assert.That(_pipe.Verbs).IsEquivalentTo(["enroll", "stop"]);
    }

    [Test]
    public async Task A_scheduled_task_for_the_name_is_retired_before_the_service_takes_it() {
        _tasks.Installed.Add("laptop");

        var ok = Manager().Install(Spec(), startNow: true, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(_tasks.Calls).IsEquivalentTo(["uninstall laptop"]);
        await Assert.That(_tasks.Installed).IsEmpty();
    }

    [Test]
    public async Task A_refused_enroll_restores_the_previous_enrollment_and_the_task() {
        var previous = new ServiceEnrollment("old", Daemon, null, null, null);
        _store.Current = previous;
        _tasks.Installed.Add("laptop");
        _pipe.Answer = _ => new ServicePipeResponse(false, "the daemon path does not exist");

        var ok = Manager().Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsEqualTo("the daemon path does not exist");
        await Assert.That(_store.Current).IsEqualTo(previous);
        await Assert.That(_tasks.Calls).IsEquivalentTo(["uninstall laptop", "install laptop"]);
    }

    /// A retire can fail after it has ended the task's daemon, as one the user may not delete does; the task is
    /// started again so the daemon is not left with no supervisor and nothing running
    [Test]
    public async Task A_task_that_will_not_go_is_started_again_and_nothing_is_enrolled() {
        _tasks.Installed.Add("laptop");
        _tasks.RefuseUninstall = true;

        var ok = Manager().Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("task is busy");
        await Assert.That(_tasks.Calls).IsEquivalentTo(["uninstall laptop", "start laptop"]);
        await Assert.That(_store.Current).IsNull();
        await Assert.That(_pipe.Verbs).IsEmpty();
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

    [Test]
    public async Task GenerateFiles_is_the_captured_environment_as_json() {
        var files = Manager().GenerateFiles(Spec());

        await Assert.That(files.Single().Path).IsEqualTo(WindowsServiceManager.EnvironmentFilePath(Config.Root, "laptop"));
        await Assert.That(files.Single().Content).StartsWith("{");
    }
}
