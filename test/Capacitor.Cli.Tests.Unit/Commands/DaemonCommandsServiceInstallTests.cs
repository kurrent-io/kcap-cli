using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`install` refuses flag combinations that have no meaning before any transaction starts.</summary>
internal sealed class DaemonCommandsServiceInstallTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    public static IEnumerable<Func<(string[] Args, string Refusal)>> RefusedFlags() {
        yield return () => (["--replace"], "install --replace requires --verify.");
        yield return () => (["--verify", "--no-start"], "--no-start is incompatible with --verify.");
        yield return () => (["--replace", "--verify", "--retire"], "--retire requires a value");
        yield return () => (["--replace", "--verify", "--retire", "--verify"], "--retire requires a value");
        yield return () => (["--verify", "--retire", "old"], "install --retire requires --replace --verify.");
        yield return () => (["--replace", "--verify", "--retire", "Test-ID"], "--retire names the service being installed; nothing to retire.");
        yield return () => (["--replace", "--verify", "--retire", "***"], "--retire value '***' is not a service id.");
    }

    /// <summary>Each refusal lands before any transaction: the manager is never asked anything. The
    /// retire target is compared sanitized, so a differently-cased spelling of the id is still the id.</summary>
    [Test, NotInParallel]
    [MethodDataSource(nameof(RefusedFlags))]
    public async Task Install_refuses_a_flag_combination_before_touching_the_service(string[] args, string refusal) {
        using var err = ConsoleOutput.StartErrorCapture();

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System)
            .Install(args, startNow: !args.Contains("--no-start"));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err.GetCapturedError()).Contains(refusal);
    }

    /// <summary>An agent the target daemon hosts would be ended by the replace it started, so the command
    /// refuses before resolving or touching anything, and names the command to run elsewhere. A retired
    /// daemon ends what it hosts too.</summary>
    [Test, NotInParallel]
    [Arguments("test-id", new[] { "--replace", "--verify" }, "`kcap daemon service install --replace --verify --name test-id`")]
    [Arguments("old", new[] { "--replace", "--verify", "--retire", "old" }, "`kcap daemon service install --replace --verify --retire old --name test-id`")]
    [Arguments("test-id", new string[0], "`kcap daemon service install --name test-id`")]
    public async Task Install_refuses_inside_an_agent_the_daemon_it_stops_hosts(string host, string[] args, string command) {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new CountingManager();
        var asked   = new List<string>();

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "test-id", Home, TimeProvider.System) {
            HostsCaller = id => { asked.Add(id); return id == host; },
        }.Install(args, startNow: true);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err.GetCapturedError()).Contains($"daemon '{host}' hosts");
        await Assert.That(err.GetCapturedError()).Contains(command);
        await Assert.That(manager.InstallCalls).IsEqualTo(0);
        await Assert.That(asked).Contains(host);
    }

    /// <summary>kcap-service holds one enrollment per user and stops the daemon of the one an install replaces, so
    /// installing another name inside an agent the enrolled daemon hosts is refused before the service is asked
    /// anything, and the enrollment is left as it was.</summary>
    [Test, NotInParallel]
    public async Task Install_refuses_inside_an_agent_the_enrollment_it_replaces_hosts() {
        using var err = ConsoleOutput.StartErrorCapture();
        var pipe        = new FakeServicePipe();
        var enrollment  = new ServiceEnrollment("work", @"C:\kcap\kcap-daemon.exe", null, null, null);
        var enrollments = new FakeServiceEnrollmentStore { Current = enrollment };
        var manager     = new WindowsServiceManager(Config.Root, pipe, enrollments, tasks: null, stopDaemon: _ => true);

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "personal", Home, TimeProvider.System) {
            HostsCaller = id => id == "work",
        }.Install([], startNow: true);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err.GetCapturedError()).Contains("daemon 'work' hosts");
        await Assert.That(err.GetCapturedError()).Contains("`kcap daemon service install --name personal`");
        await Assert.That(pipe.Verbs).IsEmpty();
        await Assert.That(enrollments.Current).IsEqualTo(enrollment);
    }

    /// <summary>The check ahead of the elevated step reads the retire target by the install's own rule: a
    /// malformed value names no target, so the install refuses the value instead of the default daemon
    /// being taken for the one to retire.</summary>
    [Test]
    [Arguments(new[] { "--replace", "--verify", "--retire", "old" }, "old")]
    [Arguments(new[] { "--retire", "daemon" }, "daemon")]
    [Arguments(new[] { "--retire", "***" }, null)]
    [Arguments(new[] { "--retire", "  " }, null)]
    [Arguments(new[] { "--retire" }, null)]
    [Arguments(new[] { "--replace", "--verify" }, null)]
    public async Task Retire_target_is_none_for_a_value_the_install_refuses(string[] args, string? target) {
        var (id, error) = DaemonServiceCommands.ParseRetire(args);

        await Assert.That(id).IsEqualTo(target);
        await Assert.That(error is null).IsEqualTo(target is not null || !args.Contains("--retire"));
    }

    /// <summary>A plain (non-verify) install serializes on the same per-label service lock as every
    /// other mutating verb: a held lock yields the coded-contention exit without ever calling
    /// Install.</summary>
    [Test]
    public async Task Plain_install_bails_on_a_held_service_lock_without_calling_install() {
        const string id = "svc-plain-install-lock";
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, id, TimeSpan.FromSeconds(1), TimeProvider.System);
        await Assert.That(held).IsNotNull();

        var manager = new CountingManager();
        var spec = new ServiceSpec(id, "/x/kcap-daemon", "/x/log", new Dictionary<string, string>(), []);

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "test-id", Home, TimeProvider.System).InstallPlain(spec, startNow: true);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.InstallCalls).IsEqualTo(0);
    }

    /// <summary>A supervisor that does not answer is reported on stderr with exit 1, never thrown.</summary>
    [Test, NotInParallel]
    public async Task Status_reports_a_supervisor_that_does_not_answer() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new RecordingServiceManager(new() { ["test-id"] = RecordingServiceManager.Unanswering });

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "test-id", Home, TimeProvider.System).Status();

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err.GetCapturedError()).Contains("kcap-service is not answering on its pipe");
    }

    /// <summary>A refused registration is reported on stderr with exit 1, never thrown.</summary>
    [Test, NotInParallel]
    public async Task Plain_install_reports_a_refused_registration() {
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new CountingManager { InstallError = "launchctl bootstrap failed (exit 5): Bootstrap failed: 5: Input/output error" };
        var spec = new ServiceSpec("svc-plain-install-refused", "/x/kcap-daemon", "/x/log", new Dictionary<string, string>(), []);

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "test-id", Home, TimeProvider.System).InstallPlain(spec, startNow: true);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err.GetCapturedError()).Contains(
            "Could not install service 'svc-plain-install-refused' (counting): launchctl bootstrap failed (exit 5)");
    }

    const string Ours = "/home/u/.local/share/kcap/current/bin/kcap-daemon";
    const string Npm  = "/opt/homebrew/lib/node_modules/@kurrent/kcap/bin/kcap-daemon";
    const string Fail = "Could not install service 'tony' (launchd LaunchAgent): boom";
    const string Replace = "`kcap daemon service install --replace --verify --name tony`";

    static ServiceQuery Q(LabelProbe probe, string? binary) => new(probe, true, ServiceState.Running, binary, null);

    public static IEnumerable<Func<(string Case, ServiceQuery Before, ServiceQuery? After, string Expected)>> FailureCases() {
        yield return () => ("not loaded before", Q(LabelProbe.Absent, Npm), Q(LabelProbe.Absent, Npm), Fail);
        yield return () => ("still loaded, this install", Q(LabelProbe.Loaded, Ours), Q(LabelProbe.Loaded, Ours),
            $"{Fail}\n  The service is still loaded, and its unit names this install's daemon ({Ours}). To reinstall it in place, run {Replace}.");
        yield return () => ("still loaded, another install", Q(LabelProbe.Loaded, Npm), Q(LabelProbe.Loaded, Npm),
            $"{Fail}\n  The service is still loaded, and its unit names {Npm}, not this install's daemon ({Ours}). To point it at this install, run {Replace}.");
        yield return () => ("stopped, unit rewritten to this install", Q(LabelProbe.Loaded, Npm), Q(LabelProbe.Absent, Ours),
            $"{Fail}\n  The service was loaded and is now stopped; its unit names this install's daemon ({Ours}). To install it again, run {Replace}.");
        yield return () => ("state unreadable after", Q(LabelProbe.Loaded, Npm), null,
            $"{Fail}\n  The service was loaded; kcap could not tell whether it still is (see `kcap daemon service status --name tony`). Its unit names {Npm}, not this install's daemon ({Ours}). To point it at this install, run {Replace}.");
    }

    /// <summary>The state after the failure decides the wording, since the install may already have
    /// stopped the old job; the unit it names is read from whichever query still sees the unit.</summary>
    [Test]
    [MethodDataSource(nameof(FailureCases))]
    public async Task Install_failure_says_what_state_the_service_is_left_in(
            string @case, ServiceQuery before, ServiceQuery? after, string expected) {
        var spec = new ServiceSpec("tony", Ours, "/x/log", new Dictionary<string, string>(), []);

        var text = DaemonServiceCommands.InstallFailure(spec, "launchd LaunchAgent", "boom", before, after);

        await Assert.That(text).IsEqualTo(expected.Replace("\n", Environment.NewLine));
    }

    sealed class CountingManager : IServiceManager {
        public int InstallCalls;
        public string? InstallError;
        public string Describe() => "counting";
        // Never read: ListInstalled is empty, so doctor's directory audit is not reached.
        public string UnitDirectory => "(fake)";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);
        public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => null;
        public ServiceQuery Query(string serviceId) => new(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
        public bool Install(ServiceSpec spec, bool startNow, out string? error) {
            InstallCalls++;
            error = InstallError;
            return error is null;
        }
        public void WriteAndBootstrap(ServiceSpec spec) { }
        public bool Uninstall(string serviceId, out string? error) { error = null; return true; }
        public bool Start(string serviceId, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, out string? error) { error = null; return true; }
    }
}
