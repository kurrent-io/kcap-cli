using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// install --replace --verify --retire: the unit a rename leaves behind is removed inside the
/// same transaction, only when it is pinned to the same profile, and a live daemon under the new
/// name is a collision rather than a takeover.
/// </summary>
public class ServiceVerifyRetireTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string NewId           = "svc-retire-new";
    const string OldId           = "svc-retire-old";
    const string ExpectedVersion = "1.2.3";
    const string OwnPlistContent = "<plist>own-unit</plist>";

    static string OldPlist(string profile) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.{OldId}</string>
          <key>ProgramArguments</key><array><string>/x/kcap-daemon</string><string>--name</string><string>{OldId}</string></array>
          <key>EnvironmentVariables</key><dict><key>KCAP_PROFILE</key><string>{profile}</string></dict>
        </dict></plist>
        """;

    /// <summary>Same state machine as ServiceVerifyInstallTests' fake, plus the retired label's own
    /// presence flag so Query answers per id.</summary>
    sealed class FakeServiceManager(UserHome home) : IVerifyServiceManager {
        public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);
        public readonly List<string> Calls = [];
        public bool OldUnitInstalled;
        public bool OldLabelLoaded = true;
        public int? OldJobPid = 1111;
        public bool Bootstrapped;
        public int? RunningPid = 4242;

        /// <summary>When set (and the new id isn't bootstrapped yet), the new id's own Query answers
        /// Loaded at this pid — a daemon that claimed the new name ahead of this install's own write.</summary>
        public int? NewLabelLoadedPid;

        /// <summary>The new id already has a stopped-but-installed unit (another profile's service).</summary>
        public bool NewUnitStopped;

        /// <summary>The new id's stopped unit appears only once the old unit has been retired.</summary>
        public bool NewUnitStoppedAfterRetire;

        public bool NewProbeUnknown;

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [new GeneratedFile("/fake/new.plist", OwnPlistContent)];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            Calls.Add($"query:{serviceId}");
            if (serviceId == OldId)
                return !OldUnitInstalled ? new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null)
                    : OldLabelLoaded ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", OldJobPid)
                    : new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/x/kcap-daemon", null);
            if (NewProbeUnknown)
                return new ServiceQuery(LabelProbe.Unknown, false, ServiceState.NotInstalled, null, null);
            if (!Bootstrapped && (NewUnitStopped || (NewUnitStoppedAfterRetire && !OldUnitInstalled)))
                return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/x/kcap-daemon", null);
            if (!Bootstrapped && NewLabelLoadedPid is not null)
                return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", NewLabelLoadedPid);
            return Bootstrapped
                ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", RunningPid)
                : new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) {
            Calls.Add($"writeAndBootstrap:{spec.ServiceId}");
            Bootstrapped = true;
        }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add($"uninstall:{serviceId}");
            if (serviceId == OldId) OldUnitInstalled = false; else Bootstrapped = false;
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) => Start(serviceId, timeout, out error);
        public bool Stop(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
    }

    /// <summary>The old daemon's fence as the transaction sees it, recording what was asked of it.</summary>
    sealed class FakeFence(FakeServiceManager manager) {
        public readonly List<string> Calls = [];
        public AdmissionFenceOutcome Outcome = AdmissionFenceOutcome.Acquired;
        public int? Pid = 1111;
        public bool CommitAnswers = true;

        public Task<AdmissionFenceAcquireResult> AcquireAsync(string name, CancellationToken _) {
            Calls.Add($"acquire:{name}");
            manager.Calls.Add($"fence-acquire:{name}");
            return Task.FromResult(new AdmissionFenceAcquireResult(Outcome, Outcome == AdmissionFenceOutcome.Acquired ? new Session(this, manager) : null));
        }

        sealed class Session(FakeFence fence, FakeServiceManager manager) : IAdmissionFenceSession {
            public int? Pid => fence.Pid;

            public Task<bool> CommitAsync(TimeSpan timeout, CancellationToken ct) {
                fence.Calls.Add("commit");
                manager.Calls.Add("fence-commit");
                return Task.FromResult(fence.CommitAnswers);
            }

            public Task<bool> AbortAsync(TimeSpan timeout, CancellationToken ct) {
                fence.Calls.Add("abort");
                return Task.FromResult(true);
            }

            public ValueTask DisposeAsync() {
                fence.Calls.Add("close");
                return ValueTask.CompletedTask;
            }
        }
    }

    string ViableDaemonPath() => Tmp.CreateFile("kcap-daemon");

    static ServiceSpec Spec(string daemonPath, string profile) =>
        new(NewId, daemonPath, Path.ChangeExtension(daemonPath, ".log"),
            new Dictionary<string, string> { ["KCAP_PROFILE"] = profile }, []);

    /// <summary>The new daemon answers hello only once bootstrapped; the old one never does.</summary>
    static Func<string, TimeSpan, Task<HelloProbeResult>> Hello(FakeServiceManager manager) =>
        (id, _) => Task.FromResult(id == NewId && manager.Bootstrapped
            ? new HelloProbeResult(true, 1, ExpectedVersion, NewId)
            : new HelloProbeResult(false, null, null, null));

    ServiceVerify Sut(FakeServiceManager manager, string? oldPlist, Func<string, int?>? validatedPid = null, FakeFence? fence = null) =>
        new(Daemons.Store, Config.Root, manager,
            validatedPid ?? (id => id == NewId && manager.Bootstrapped ? 4242 : null),
            Hello(manager), TimeProvider.System,
            readPlist: path => path == manager.UnitPath(OldId) ? oldPlist : OwnPlistContent,
            plistExists: path => path == manager.UnitPath(OldId) ? oldPlist is not null : true,
            acquireFence: (fence ?? new FakeFence(manager)).AcquireAsync);

    /// <summary>Same drive loop as ServiceVerifyInstallTests: Task.Delay(interval, time, ct)'s
    /// continuation resumes synchronously inside Advance(), so a tight Advance-loop reliably steps
    /// a multi-iteration poll to completion without any real waiting.</summary>
    static async Task<int> Drive(Task<int> task, FakeTimeProvider time, TimeSpan step) {
        var guard = 0;
        while (!task.IsCompleted && guard++ < 500) time.Advance(step);
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Retiring_the_target_itself_throws() {
        var manager = new FakeServiceManager(Home);
        var sut = Sut(manager, oldPlist: null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: NewId));

        await Assert.That(manager.Calls).IsEmpty();
    }

    [Test]
    public async Task A_new_daemon_that_never_answers_rolls_back_without_touching_the_settled_retirement() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var time = new FakeTimeProvider();

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager,
            id => id == NewId && manager.Bootstrapped ? 4242 : null,
            (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)),
            time,
            forwardBudget: TimeSpan.FromSeconds(2),
            readPlist: path => path == manager.UnitPath(OldId) ? OldPlist("mine") : OwnPlistContent,
            plistExists: _ => true,
            acquireFence: new FakeFence(manager).AcquireAsync);

        var task = sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(manager.Calls.Count(c => c == $"uninstall:{OldId}")).IsEqualTo(1);
        await Assert.That(manager.Calls.Count(c => c == $"writeAndBootstrap:{NewId}")).IsEqualTo(1);
        await Assert.That(manager.Calls.IndexOf($"writeAndBootstrap:{NewId}")).IsLessThan(manager.Calls.IndexOf($"uninstall:{NewId}"));
        // The retirement is fully settled before the new unit's own install/poll/rollback begins —
        // none of that later work ever names the already-retired id again.
        await Assert.That(manager.Calls.SkipWhile(c => c != $"writeAndBootstrap:{NewId}")
            .Any(c => c.EndsWith($":{OldId}", StringComparison.Ordinal))).IsFalse();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, OldId)).IsFalse();
    }

    [Test]
    public async Task An_absent_retired_unit_is_a_no_op() {
        var manager = new FakeServiceManager(Home);
        var sut = Sut(manager, oldPlist: null);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).DoesNotContain($"uninstall:{OldId}");
        await Assert.That(manager.Calls).Contains($"writeAndBootstrap:{NewId}");
    }

    [Test, NotInParallel]
    public async Task A_unit_pinned_to_another_profile_is_refused_untouched() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("other"));

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls.Any(c => c.StartsWith("uninstall:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(manager.OldUnitInstalled).IsTrue();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).IsEquivalentTo(["retire_reason=foreign_profile", "verify_retire_refused"]);
    }

    [Test]
    public async Task A_unit_without_a_pinned_profile_is_refused_untouched() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"));
        var spec = new ServiceSpec(NewId, ViableDaemonPath(), "/x/log", new Dictionary<string, string>(), []);

        var exit = await sut.InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.OldUnitInstalled).IsTrue();
    }

    [Test]
    public async Task A_same_profile_unit_is_retired_before_the_new_unit_installs() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"));

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.OldUnitInstalled).IsFalse();
        await Assert.That(manager.Calls.IndexOf($"uninstall:{OldId}")).IsLessThan(manager.Calls.IndexOf($"writeAndBootstrap:{NewId}"));
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, OldId)).IsFalse();
    }

    [Test]
    public async Task A_live_daemon_under_the_new_name_is_contended_not_taken_over() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"), validatedPid: id => id == NewId ? 9999 : null);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
        await Assert.That(manager.OldUnitInstalled).IsTrue();
        await Assert.That(manager.Calls.Any(c => c.StartsWith("uninstall:", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task A_daemon_that_claims_the_new_name_during_the_retire_is_contended_after_it() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"), validatedPid: id => id == NewId && !manager.OldUnitInstalled ? 9999 : null);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
        await Assert.That(manager.Calls).Contains($"uninstall:{OldId}");
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task A_live_owner_seen_by_the_pre_query_is_contended_not_cleared() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true, NewLabelLoadedPid = 9999 };
        var sut = Sut(manager, OldPlist("mine"), validatedPid: id => id == NewId && manager.Calls.Contains($"query:{NewId}") ? 9999 : null);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
        await Assert.That(manager.Calls).DoesNotContain($"uninstall:{NewId}");
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>DaemonKill.KillValidatedOwner is never reached here — refuseLiveOwner returns
    /// Contended right after the null check, before the kill call it would otherwise guard.</summary>
    [Test]
    public async Task A_live_owner_without_a_label_is_contended_not_killed() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"), validatedPid: id => id == NewId && manager.Calls.Contains($"query:{NewId}") ? 9999 : null);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
    }

    [Test, NotInParallel]
    public async Task An_unreadable_retired_unit_is_refused_untouched() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager,
            id => id == NewId && manager.Bootstrapped ? 4242 : null, Hello(manager), TimeProvider.System,
            readPlist: path => path == manager.UnitPath(OldId) ? null : OwnPlistContent,
            plistExists: _ => true,
            acquireFence: new FakeFence(manager).AcquireAsync);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.OldUnitInstalled).IsTrue();
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).IsEquivalentTo(["retire_reason=unit_unreadable", "verify_retire_refused"]);
    }

    [Test]
    public async Task An_unconfirmed_stop_of_the_retired_unit_aborts_before_writing_the_new_unit() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var time = new FakeTimeProvider();

        // The retired daemon keeps answering hello even after its label clears (a hung process
        // still holding the socket) — WaitForStopConfirmedAsync(OldId, ...) can never confirm.
        static Task<HelloProbeResult> Hello(string id, TimeSpan _) =>
            Task.FromResult(id == OldId
                ? new HelloProbeResult(true, 1, ExpectedVersion, OldId)
                : new HelloProbeResult(false, null, null, null));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager,
            id => id == NewId && manager.Bootstrapped ? 4242 : null, Hello, time,
            forwardBudget: TimeSpan.FromSeconds(2),
            readPlist: path => path == manager.UnitPath(OldId) ? OldPlist("mine") : OwnPlistContent,
            plistExists: _ => true,
            acquireFence: new FakeFence(manager).AcquireAsync);

        var task = sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.StopUnconfirmed);
        await Assert.That(manager.Calls).Contains($"uninstall:{OldId}");
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
    }

    static async Task AssertRefusedUntouched(FakeServiceManager manager, ConsoleOutput err, int exit, string reason) {
        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls.Any(c => c.StartsWith("uninstall:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(manager.OldUnitInstalled).IsTrue();
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).IsEquivalentTo([$"retire_reason={reason}", "verify_retire_refused"]);
    }

    /// <summary>A stopped service has no live socket, so only the unit itself shows the name is taken.</summary>
    [Test, NotInParallel]
    public async Task A_stopped_unit_under_the_new_name_is_refused_before_anything_changes() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true, NewUnitStopped = true };
        var sut = Sut(manager, OldPlist("mine"));

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, "target_occupied");
        await Assert.That(manager.NewUnitStopped).IsTrue();
    }

    [Test, NotInParallel]
    public async Task An_unknown_target_state_is_refused_before_anything_changes() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true, NewProbeUnknown = true };
        var sut = Sut(manager, OldPlist("mine"));

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, "target_unknown");
    }

    /// <summary>A lock-unaware writer can install the new name while the old unit is retired; that unit
    /// is still someone else's and is refused rather than cleared.</summary>
    [Test, NotInParallel]
    public async Task A_unit_that_appears_under_the_new_name_during_the_retire_is_not_cleared() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true, NewUnitStoppedAfterRetire = true };
        var sut = Sut(manager, OldPlist("mine"));

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls).DoesNotContain($"uninstall:{NewId}");
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).IsEquivalentTo(["retire_reason=target_claimed", "verify_retire_refused"]);
    }

    [Test]
    public async Task A_live_old_daemon_is_fenced_before_recovery_and_committed_before_bootout() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var fence = new FakeFence(manager);
        var sut = Sut(manager, OldPlist("mine"), fence: fence);
        // The marker the old daemon wrote on commit; once it is confirmed gone nothing needs it.
        var marker = Daemons.Store.RetiringMarkerPath(OldId);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "{}");

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(fence.Calls).IsEquivalentTo(["acquire:" + OldId, "commit", "close"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var calls = manager.Calls;
        await Assert.That(calls.IndexOf($"fence-acquire:{OldId}")).IsLessThan(calls.IndexOf($"query:{NewId}"));
        await Assert.That(calls.IndexOf("fence-commit")).IsLessThan(calls.IndexOf($"uninstall:{OldId}"));
        await Assert.That(manager.OldUnitInstalled).IsFalse();
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test, NotInParallel]
    [Arguments(AdmissionFenceOutcome.Busy, "agents_active")]
    [Arguments(AdmissionFenceOutcome.Unsupported, "fence_unsupported")]
    [Arguments(AdmissionFenceOutcome.Unavailable, "fence_unavailable")]
    public async Task An_old_daemon_that_cannot_be_fenced_is_refused_before_anything_changes(AdmissionFenceOutcome outcome, string reason) {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var fence = new FakeFence(manager) { Outcome = outcome };
        var sut = Sut(manager, OldPlist("mine"), fence: fence);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, reason);
        await Assert.That(manager.Calls).DoesNotContain($"query:{NewId}");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
    }

    /// <summary>A fence granted by a process other than the one the bootout would kill protects nothing.</summary>
    [Test, NotInParallel]
    public async Task A_fence_granted_by_another_process_is_released_and_refused() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var fence = new FakeFence(manager) { Pid = 2222 };
        var sut = Sut(manager, OldPlist("mine"), fence: fence);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, "fence_unavailable");
        await Assert.That(fence.Calls).IsEquivalentTo(["acquire:" + OldId, "close"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test, NotInParallel]
    public async Task A_loaded_old_label_without_a_pid_is_refused_unfenced() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true, OldJobPid = null };
        var fence = new FakeFence(manager);
        var sut = Sut(manager, OldPlist("mine"), fence: fence);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, "fence_unavailable");
        await Assert.That(fence.Calls).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task A_target_refusal_after_the_fence_releases_it_without_committing() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true, NewUnitStopped = true };
        var fence = new FakeFence(manager);
        var sut = Sut(manager, OldPlist("mine"), fence: fence);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, "target_occupied");
        await Assert.That(fence.Calls).IsEquivalentTo(["acquire:" + OldId, "close"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test, NotInParallel]
    public async Task An_unanswered_commit_is_aborted_and_refused_without_bootout() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var fence = new FakeFence(manager) { CommitAnswers = false };
        var sut = Sut(manager, OldPlist("mine"), fence: fence);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, "fence_unavailable");
        await Assert.That(fence.Calls).IsEquivalentTo(["acquire:" + OldId, "commit", "abort", "close"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_old_unit_that_is_not_loaded_is_retired_without_a_fence() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true, OldLabelLoaded = false };
        var fence = new FakeFence(manager);
        var sut = Sut(manager, OldPlist("mine"), fence: fence);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(fence.Calls).IsEmpty();
        await Assert.That(manager.Calls).Contains($"uninstall:{OldId}");
    }

    /// <summary>launchd keeps a job loaded after its plist is gone; its profile cannot be verified, so it
    /// must not be left running beside the renamed daemon or booted out unchecked.</summary>
    [Test, NotInParallel]
    public async Task A_loaded_old_label_without_its_plist_is_refused() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var fence = new FakeFence(manager);
        var sut = Sut(manager, oldPlist: null, fence: fence);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await AssertRefusedUntouched(manager, err, exit, "unit_unreadable");
        await Assert.That(fence.Calls).IsEmpty();
    }
}
