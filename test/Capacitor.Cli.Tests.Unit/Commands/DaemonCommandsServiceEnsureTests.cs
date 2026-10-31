using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Dispatch rows of <c>kcap daemon service ensure</c> that need no real service manager: the
/// already-enabled and fail-closed-attention rows are pure classification reads. (The install/start
/// arms on launchd run the ServiceVerify transaction — covered by the engine's own suites; the
/// plain arms need the daemon binary present, which a unit test host does not have.)
/// </summary>
/// <remarks>Bare <c>[NotInParallel]</c>: <c>Ensure</c> writes to the process-global
/// <c>Console.Out</c>, so these cannot run beside a test capturing console streams.</remarks>
[NotInParallel]
public class DaemonCommandsServiceEnsureTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    sealed class FakeManager : IServiceManager {
        public ServiceQuery QueryResult { get; init; } =
            new(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
        public string Describe() => "fake";
        // Never read: ListInstalled is empty, so doctor's directory audit is not reached.
        public string UnitDirectory => "(fake)";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);
        public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => null;
        public ServiceQuery Query(string serviceId) => QueryResult;
        public bool Install(ServiceSpec spec, bool startNow, out string? error) { error = null; return true; }
        public void WriteAndBootstrap(ServiceSpec spec) { }
        public bool Uninstall(string serviceId, out string? error) { error = null; return true; }
        public bool Start(string serviceId, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, out string? error) { error = null; return true; }
    }

    [Test]
    public async Task Unknown_probe_fails_closed_with_reason() {
        var manager = new FakeManager {
            QueryResult = new ServiceQuery(LabelProbe.Unknown, false, ServiceState.NotInstalled, null, null)
        };
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "test-id", Home, TimeProvider.System).Ensure(["--json"]);
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Active_transaction_reports_attention_without_mutating() {
        var manager = new FakeManager {
            QueryResult = new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/b/kcap-daemon", 42)
        };
        // Ensure reads the lock via ServiceTxnLock.IsHeld; hold it for real.
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "test-id", TimeSpan.FromSeconds(1), TimeProvider.System);
        await Assert.That(held).IsNotNull();

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "test-id", Home, TimeProvider.System).Ensure(["--json"]);
        await Assert.That(exit).IsEqualTo(1);
    }

    /// <summary>kcap-service holds one enrollment per user and stops the daemon of the one an install replaces, so
    /// ensure installing a name with no unit, inside an agent the enrolled daemon hosts, refuses before the
    /// service is asked anything and leaves the enrollment as it was.</summary>
    [Test]
    public async Task Ensure_install_refuses_inside_an_agent_the_enrollment_it_replaces_hosts() {
        using var err = ConsoleOutput.StartErrorCapture();
        var pipe        = new FakeServicePipe();
        var enrollment  = new ServiceEnrollment("work", @"C:\kcap\kcap-daemon.exe", null, null, null);
        var enrollments = new FakeServiceEnrollmentStore { Current = enrollment };
        var manager     = new WindowsServiceManager(Config.Root, pipe, enrollments, tasks: null, stopDaemon: _ => true);

        var (result, exit) = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "personal", Home, TimeProvider.System) {
            HostsCaller = id => id == "work",
        }.EvaluateEnsureAsync("default");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(result.Action).IsEqualTo("install");
        await Assert.That(result.Reason).IsEqualTo("hosted_caller");
        await Assert.That(err.GetCapturedError()).Contains("daemon 'work' hosts");
        await Assert.That(err.GetCapturedError()).Contains("`kcap daemon service ensure --name personal --profile default`");
        await Assert.That(pipe.Verbs).IsEmpty();
        await Assert.That(enrollments.Current).IsEqualTo(enrollment);
    }

    // F1: on launchd a unit baked without a resolvable profile can never pass the start gate's
    // identity half (it demands a non-empty invoking KCAP_PROFILE), so the install/start arms must
    // fail closed up front rather than report a success that can never be gated-started. The
    // predicate is pure (takes isLaunchd, not the manager) so it runs on every CI leg.
    [Test]
    [Arguments(true, null, EnsureAction.Install, true)]
    [Arguments(true, "", EnsureAction.Start, true)]
    [Arguments(true, "acme", EnsureAction.Install, false)]
    [Arguments(true, null, EnsureAction.AlreadyEnabled, false)]
    [Arguments(false, null, EnsureAction.Install, false)]
    public async Task Launchd_profile_refusal_is_conditional_on_platform_profile_and_action(
        bool isLaunchd, string? profileName, EnsureAction action, bool expected) {
        await Assert.That(DaemonServiceCommands.LaunchdProfileRefusal(isLaunchd, profileName, action)).IsEqualTo(expected);
    }
}
