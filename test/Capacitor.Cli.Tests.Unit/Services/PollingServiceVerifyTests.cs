using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// The polling <c>--verify</c> transaction reports success only once the supervisor's own daemon
/// answers as the right name, protocol and version, and undoes its mutation otherwise — the evidence
/// the desktop app's lane classifies, with launchd's exit codes.
public class PollingServiceVerifyTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    const string Id = "win-verify-svc";
    const int DaemonPid = 4242;
    const int OldDaemonPid = 1111;
    static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(600);

    sealed class FakeManager : IServiceManager {
        public bool Registered;
        public bool UnitPresent;
        public bool Running;
        public int? JobPid;
        public int? JobPidOnStart = DaemonPid;
        public bool OldRegistered;
        public bool OldRunning;
        public int? OldJobPid = OldDaemonPid;
        public bool OldUninstallFails;
        public IReadOnlyDictionary<string, string>? OldEnv;
        public readonly List<string> Calls = [];

        public string Describe() => "fake task";
        public string UnitDirectory => "";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => Registered ? [Id] : [];
        public ServiceStatus Status(string serviceId) => new(State, null);
        public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => serviceId == Id ? null : OldEnv;
        ServiceState State => !Registered ? ServiceState.NotInstalled : Running ? ServiceState.Running : ServiceState.Installed;

        public ServiceQuery Query(string serviceId) => serviceId == Id
            ? new(Registered ? LabelProbe.Loaded : LabelProbe.Absent, UnitPresent, State, null, Running ? JobPid : null)
            : new(OldRegistered ? LabelProbe.Loaded : LabelProbe.Absent, OldRegistered,
                !OldRegistered ? ServiceState.NotInstalled : OldRunning ? ServiceState.Running : ServiceState.Installed,
                null, OldRunning ? OldJobPid : null);

        public bool Install(ServiceSpec spec, bool startNow, out string? error) {
            Calls.Add("install");
            Registered = UnitPresent = true;
            if (startNow) Launch();
            error = null;
            return true;
        }

        public void WriteAndBootstrap(ServiceSpec spec) => Install(spec, true, out _);

        public bool Uninstall(string serviceId, out string? error) {
            Calls.Add($"uninstall:{serviceId}");
            if (serviceId != Id && OldUninstallFails) {
                error = "Access is denied.";
                return false;
            }
            if (serviceId == Id) Registered = UnitPresent = Running = false;
            else OldRegistered = OldRunning = false;
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

    PollingServiceVerify Verify(FakeManager manager, Func<HelloProbeResult>? hello = null, bool viable = true,
            IReadOnlyDictionary<string, string>? retired = null, FakeFence? fence = null) {
        manager.OldEnv = retired;
        return new(Daemons.Store, manager,
            id => id == Id ? (manager.Running ? DaemonPid : null) : manager.OldRunning ? OldDaemonPid : null,
            (_, _) => Task.FromResult(manager.Running ? (hello ?? (() => Hello()))() : new HelloProbeResult(false, null, null, null)),
            TimeProvider.System, () => viable, ShortBudget, (fence ?? new FakeFence(manager)).AcquireAsync);
    }

    /// The old daemon's fence, recording into the manager's calls so ordering against install and
    /// uninstall is visible.
    sealed class FakeFence(FakeManager manager) {
        public AdmissionFenceOutcome Outcome = AdmissionFenceOutcome.Acquired;
        public int? Pid = OldDaemonPid;
        public bool CommitAnswers = true;
        readonly List<string> _calls = manager.Calls;

        public Task<AdmissionFenceAcquireResult> AcquireAsync(string name, CancellationToken _) {
            _calls.Add($"fence-acquire:{name}");
            return Task.FromResult(new AdmissionFenceAcquireResult(Outcome, Outcome == AdmissionFenceOutcome.Acquired ? new Session(this) : null));
        }

        sealed class Session(FakeFence fence) : IAdmissionFenceSession {
            public int? Pid => fence.Pid;

            public Task<bool> CommitAsync(TimeSpan timeout, CancellationToken ct) {
                fence._calls.Add("fence-commit");
                return Task.FromResult(fence.CommitAnswers);
            }

            public Task<bool> AbortAsync(TimeSpan timeout, CancellationToken ct) {
                fence._calls.Add("fence-abort");
                return Task.FromResult(true);
            }

            public ValueTask DisposeAsync() {
                fence._calls.Add("fence-close");
                return ValueTask.CompletedTask;
            }
        }
    }

    static ServiceSpec Spec() => new(Id, @"C:\kcap\kcap-daemon.exe", @"C:\kcap\daemon.log",
        new Dictionary<string, string> { ["KCAP_PROFILE"] = "work" }, []);

    static Dictionary<string, string> Retired(string? profile) =>
        profile is null ? [] : new() { ["KCAP_PROFILE"] = profile };

    /// Hello and the pid are separate readings, so a single agreeing reading can pair a hello from one
    /// daemon with its replacement's pid. A pid file that changes on every read must never be confirmed.
    [Test]
    public async Task A_pid_that_changes_between_readings_is_never_confirmed() {
        var manager = new FakeManager();
        var reads   = 0;
        var verify  = new PollingServiceVerify(Daemons.Store, manager,
            _ => manager.Running ? (reads++ % 2 == 0 ? DaemonPid : DaemonPid + 1) : null,
            (_, _) => Task.FromResult(manager.Running ? Hello() : new HelloProbeResult(false, null, null, null)),
            TimeProvider.System, () => true, ShortBudget);

        var exit = await verify.InstallVerifiedAsync(Spec(), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
    }

    [Test]
    [Arguments("personal")]
    [Arguments(null)]
    public async Task A_rename_refuses_to_retire_a_unit_not_pinned_to_the_installing_profile_and_touches_nothing(string? profile) {
        var manager = new FakeManager { OldRegistered = true };

        var exit = await Verify(manager, retired: Retired(profile))
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls).IsEmpty();
    }

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
        var manager = new FakeManager { OldRegistered = true };

        var exit = await Verify(manager, retired: Retired("work"))
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["install", "uninstall:old-name"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// The uninstall kills the old daemon's process tree, so its fence is held from before the install
    /// and committed only once the new daemon is ready.
    [Test]
    public async Task A_rename_fences_a_running_old_daemon_and_commits_before_the_uninstall() {
        var manager = new FakeManager { OldRegistered = true, OldRunning = true };

        var exit = await Verify(manager, retired: Retired("work"))
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(
            ["fence-acquire:old-name", "install", "fence-commit", "uninstall:old-name", "fence-close"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_rename_refuses_to_retire_a_busy_old_daemon_and_touches_nothing() {
        var manager = new FakeManager { OldRegistered = true, OldRunning = true };
        var fence   = new FakeFence(manager) { Outcome = AdmissionFenceOutcome.Busy };

        var exit = await Verify(manager, retired: Retired("work"), fence: fence)
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls).IsEquivalentTo(["fence-acquire:old-name"]);
    }

    [Test]
    public async Task A_rename_refuses_when_the_fence_answers_for_another_process() {
        var manager = new FakeManager { OldRegistered = true, OldRunning = true };
        var fence   = new FakeFence(manager) { Pid = OldDaemonPid + 1 };

        var exit = await Verify(manager, retired: Retired("work"), fence: fence)
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls).DoesNotContain("install");
    }

    [Test]
    public async Task A_rename_keeps_the_old_service_when_its_fence_commit_goes_unanswered() {
        var manager = new FakeManager { OldRegistered = true, OldRunning = true };
        var fence   = new FakeFence(manager) { CommitAnswers = false };

        var exit = await Verify(manager, retired: Retired("work"), fence: fence)
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls).DoesNotContain("uninstall:old-name");
        await Assert.That(manager.OldRegistered).IsTrue();
    }

    /// A failed uninstall leaves the old daemon running, so the fence it committed is undone.
    [Test]
    public async Task A_rename_whose_old_task_cannot_be_uninstalled_aborts_the_committed_fence() {
        var manager = new FakeManager { OldRegistered = true, OldRunning = true, OldUninstallFails = true };

        var exit = await Verify(manager, retired: Retired("work"))
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls).IsEquivalentTo(
            ["fence-acquire:old-name", "install", "fence-commit", "uninstall:old-name", "fence-abort", "fence-close"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// The task manager finds an installed task without the wrapper under this config root, so a task
    /// whose profile cannot be read could be another profile's.
    [Test]
    public async Task A_rename_refuses_an_installed_old_task_whose_wrapper_cannot_be_read() {
        var manager = new FakeManager { OldRegistered = true };

        var exit = await Verify(manager)
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls).IsEmpty();
    }

    [Test]
    public async Task A_rename_whose_old_task_is_gone_installs_without_retiring() {
        var manager = new FakeManager();

        var exit = await Verify(manager)
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: "old-name");

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["install"]);
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
