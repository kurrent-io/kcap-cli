using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`install --verify` runs on launchd and on a Windows Scheduled Task; systemd reports no job
/// pid to verify against, so it gets a clear rejection rather than a deep failure inside a transaction.</summary>
internal sealed class DaemonCommandsServiceInstallTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task Verify_flag_is_rejected_on_a_non_launchd_manager() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--verify"], true);
        await Assert.That(exit).IsEqualTo(1);
    }

    /// <summary>--replace only has meaning inside the verify transaction engine (it selects
    /// ServiceVerify.InstallVerifiedAsync's ownership matrix) — a plain install has no transaction
    /// to hand it to, so the combination is rejected before even reaching the platform gate (asserted
    /// here on systemd, which would otherwise reject for a different reason).</summary>
    [Test]
    public async Task Replace_without_verify_is_rejected() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--replace"], true);
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task Retire_missing_a_value_at_the_end_of_args_is_rejected() {
        using var err = ConsoleOutput.StartErrorCapture();
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--replace", "--verify", "--retire"], true);
        await Assert.That(exit).IsEqualTo(1);
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Any(l => l.Contains("--retire"))).IsTrue();
    }

    [Test, NotInParallel]
    public async Task Retire_followed_by_another_flag_is_rejected() {
        using var err = ConsoleOutput.StartErrorCapture();
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--replace", "--verify", "--retire", "--verify"], true);
        await Assert.That(exit).IsEqualTo(1);
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Any(l => l.Contains("--retire"))).IsTrue();
    }

    [Test, NotInParallel]
    public async Task Retire_without_replace_and_verify_is_rejected() {
        using var err = ConsoleOutput.StartErrorCapture();
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--verify", "--retire", "old"], true);
        await Assert.That(exit).IsEqualTo(1);
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).Contains("install --retire requires --replace --verify.");
    }

    /// <summary>The comparison is on sanitized ids, so a differently-cased spelling of the target is still the target.</summary>
    [Test, NotInParallel]
    public async Task Retire_naming_the_target_itself_is_rejected() {
        using var err = ConsoleOutput.StartErrorCapture();
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--replace", "--verify", "--retire", "Test-ID"], true);
        await Assert.That(exit).IsEqualTo(1);
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).Contains("--retire names the service being installed; nothing to retire.");
    }

    [Test, NotInParallel]
    public async Task Retire_value_that_sanitizes_to_the_fallback_is_rejected() {
        using var err = ConsoleOutput.StartErrorCapture();
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--replace", "--verify", "--retire", "***"], true);
        await Assert.That(exit).IsEqualTo(1);
        var lines = err.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).Contains("--retire value '***' is not a service id.");
    }

    /// <summary>--no-start withholds the start; --verify's job is to prove the started daemon is
    /// ready. The two contradict and are rejected before anything runs (startNow=false models
    /// --no-start).</summary>
    [Test]
    public async Task No_start_with_verify_is_rejected() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Install(["--verify", "--no-start"], startNow: false);
        await Assert.That(exit).IsEqualTo(1);
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

    public static IEnumerable<Func<(string Case, LabelProbe Probe, string? Binary, bool VerifySupported, string? Detail)>> FailureCases() {
        yield return () => ("not loaded before", LabelProbe.Absent, Npm, true, null);
        yield return () => ("loaded, this install", LabelProbe.Loaded, Ours, true,
            $"The service was already installed and loaded, running this install's daemon ({Ours}). "
          + "To reinstall it in place, run `kcap daemon service install --replace --verify --name tony`.");
        yield return () => ("loaded, another install", LabelProbe.Loaded, Npm, true,
            $"The service was already installed and loaded, running {Npm} rather than this install's daemon ({Ours}). "
          + "To point it at this install, run `kcap daemon service install --replace --verify --name tony`.");
        yield return () => ("loaded, no verify on this platform", LabelProbe.Loaded, Npm, false,
            $"The service was already installed and loaded, running {Npm} rather than this install's daemon ({Ours}). "
          + "To point it at this install, run `kcap daemon service uninstall --name tony`, then `kcap daemon service install --name tony`.");
    }

    [Test]
    [MethodDataSource(nameof(FailureCases))]
    public async Task Install_failure_says_which_daemon_a_loaded_unit_runs(
            string @case, LabelProbe probe, string? binary, bool verifySupported, string? detail) {
        var spec   = new ServiceSpec("tony", Ours, "/x/log", new Dictionary<string, string>(), []);
        var before = new ServiceQuery(probe, true, ServiceState.Running, binary, null);

        var text = DaemonServiceCommands.InstallFailure(spec, "launchd LaunchAgent", "boom", before, verifySupported);

        var expected = "Could not install service 'tony' (launchd LaunchAgent): boom";
        await Assert.That(text).IsEqualTo(detail is null ? expected : $"{expected}{Environment.NewLine}  {detail}");
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
