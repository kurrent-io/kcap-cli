using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// The Windows <c>--verify</c> transaction reports success only once the task's own daemon answers
/// as the right name, protocol and version, and undoes its mutation otherwise — the evidence the
/// desktop app's lane classifies, with launchd's exit codes.
public class WindowsServiceVerifyTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    const string Id = "win-verify-svc";
    const int DaemonPid = 4242;
    static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(600);

    sealed class FakeManager : IServiceManager {
        public bool Registered;
        public bool UnitPresent;
        public bool Running;
        public int? JobPid;
        public int? JobPidOnStart = DaemonPid;
        public readonly List<string> Calls = [];

        public string Describe() => "fake task";
        public string UnitDirectory => "";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => Registered ? [Id] : [];
        public ServiceStatus Status(string serviceId) => new(State, null);
        ServiceState State => !Registered ? ServiceState.NotInstalled : Running ? ServiceState.Running : ServiceState.Installed;

        public ServiceQuery Query(string serviceId) =>
            new(Registered ? LabelProbe.Loaded : LabelProbe.Absent, UnitPresent, State, null, Running ? JobPid : null);

        public void Install(ServiceSpec spec, bool startNow) {
            Calls.Add("install");
            Registered = UnitPresent = true;
            if (startNow) Launch();
        }

        public void WriteAndBootstrap(ServiceSpec spec) => Install(spec, true);

        public bool Uninstall(string serviceId, out string? error) {
            Calls.Add($"uninstall:{serviceId}");
            if (serviceId == Id) Registered = UnitPresent = Running = false;
            error = null;
            return true;
        }

        public bool Start(string serviceId, out string? error) {
            Calls.Add("start");
            Launch();
            error = null;
            return true;
        }

        public bool Stop(string serviceId, out string? error) {
            Calls.Add("stop");
            Running = false;
            error = null;
            return true;
        }

        void Launch() {
            Running = true;
            JobPid = JobPidOnStart;
        }
    }

    static HelloProbeResult Hello(string name = Id, string? version = "1.0.0") =>
        new(true, HelloProtocol.CurrentVersion, version, name);

    WindowsServiceVerify Verify(FakeManager manager, Func<HelloProbeResult>? hello = null, bool viable = true) =>
        new(Daemons.Store, manager,
            _ => manager.Running ? DaemonPid : null,
            (_, _) => Task.FromResult(manager.Running ? (hello ?? (() => Hello()))() : new HelloProbeResult(false, null, null, null)),
            TimeProvider.System, () => viable, ShortBudget);

    static ServiceSpec Spec() => new(Id, @"C:\kcap\kcap-daemon.exe", @"C:\kcap\daemon.log", new Dictionary<string, string>(), []);

    [Test]
    public async Task A_fresh_install_succeeds_once_the_tasks_daemon_answers() {
        var manager = new FakeManager();

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: false, expectedVersion: "1.0.0");

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["install"]);
    }

    [Test]
    public async Task An_install_over_an_existing_unit_without_replace_is_contended() {
        var manager = new FakeManager { Registered = true, UnitPresent = true };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
        await Assert.That(manager.Calls).IsEmpty();
    }

    [Test]
    public async Task A_replace_stops_the_running_daemon_before_reinstalling() {
        var manager = new FakeManager { Registered = true, UnitPresent = true, Running = true, JobPid = DaemonPid };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["stop", "install"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// A daemon that is not the task's own — started by hand, so no job pid — never counts as ready.
    [Test]
    public async Task A_daemon_the_task_did_not_start_is_not_ready_and_the_install_is_rolled_back() {
        var manager = new FakeManager { JobPidOnStart = null };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(manager.Calls).IsEquivalentTo(["install", $"uninstall:{Id}"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(manager.Registered).IsFalse();
    }

    [Test]
    public async Task A_daemon_of_another_version_is_not_ready() {
        var manager = new FakeManager();

        var exit = await Verify(manager, () => Hello(version: "0.9.0")).InstallVerifiedAsync(Spec(), replace: false, expectedVersion: "1.0.0");

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
    }

    [Test]
    public async Task A_profile_without_a_valid_server_is_refused_before_any_mutation() {
        var manager = new FakeManager();

        var exit = await Verify(manager, viable: false).InstallVerifiedAsync(Spec(), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.Viability);
        await Assert.That(manager.Calls).IsEmpty();
    }

    [Test]
    public async Task A_rename_retires_the_old_service_after_the_new_one_is_ready() {
        var manager = new FakeManager();

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["install", "uninstall:old-name"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_verified_start_of_a_stopped_service_starts_and_waits() {
        var manager = new FakeManager { Registered = true, UnitPresent = true };

        var exit = await Verify(manager).StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["start"]);
    }

    [Test]
    public async Task A_verified_start_of_a_ready_service_changes_nothing() {
        var manager = new FakeManager { Registered = true, UnitPresent = true, Running = true, JobPid = DaemonPid };

        var exit = await Verify(manager).StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEmpty();
    }

    [Test]
    public async Task A_verified_start_that_never_gets_ready_is_stopped_again() {
        var manager = new FakeManager { Registered = true, UnitPresent = true, JobPidOnStart = null };

        var exit = await Verify(manager).StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(manager.Calls).IsEquivalentTo(["start", "stop"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_verified_start_of_an_uninstalled_service_fails_without_mutating() {
        var manager = new FakeManager();

        var exit = await Verify(manager).StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Calls).IsEmpty();
    }
}
