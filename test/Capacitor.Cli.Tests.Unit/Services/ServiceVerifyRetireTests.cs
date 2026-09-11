using Capacitor.Cli.Core;
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
        public bool Bootstrapped;
        public int? RunningPid = 4242;

        /// <summary>When set (and the new id isn't bootstrapped yet), the new id's own Query answers
        /// Loaded at this pid — a daemon that claimed the new name ahead of this install's own write.</summary>
        public int? NewLabelLoadedPid;

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [new GeneratedFile("/fake/new.plist", OwnPlistContent)];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            Calls.Add($"query:{serviceId}");
            if (serviceId == OldId)
                return OldUnitInstalled
                    ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", 1111)
                    : new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
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

    string ViableDaemonPath() => Tmp.CreateFile("kcap-daemon");

    static ServiceSpec Spec(string daemonPath, string profile) =>
        new(NewId, daemonPath, Path.ChangeExtension(daemonPath, ".log"),
            new Dictionary<string, string> { ["KCAP_PROFILE"] = profile }, []);

    /// <summary>The new daemon answers hello only once bootstrapped; the old one never does.</summary>
    static Func<string, TimeSpan, Task<HelloProbeResult>> Hello(FakeServiceManager manager) =>
        (id, _) => Task.FromResult(id == NewId && manager.Bootstrapped
            ? new HelloProbeResult(true, 1, ExpectedVersion, NewId)
            : new HelloProbeResult(false, null, null, null));

    ServiceVerify Sut(FakeServiceManager manager, string? oldPlist, Func<string, int?>? validatedPid = null) =>
        new(Daemons.Store, Config.Root, manager,
            validatedPid ?? (id => id == NewId && manager.Bootstrapped ? 4242 : null),
            Hello(manager), TimeProvider.System,
            readPlist: path => path == manager.UnitPath(OldId) ? oldPlist : OwnPlistContent,
            plistExists: path => path == manager.UnitPath(OldId) ? oldPlist is not null : true);

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
            plistExists: _ => true);

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
            plistExists: _ => true);

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
            plistExists: _ => true);

        var task = sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.StopUnconfirmed);
        await Assert.That(manager.Calls).Contains($"uninstall:{OldId}");
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
    }
}
