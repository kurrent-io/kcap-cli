using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>A first setup is stopped at a named point, as a crash would stop it; the next setup's recovery must then
/// finish or undo it from the journal alone.</summary>
[ExcludeOn(OS.Windows)]
public class LocalSetupRecoveryTests {
    const string Version = "1.2.3";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];
    readonly List<string> _points = [];
    readonly FakeTimeProvider _time = new();
    readonly RecordingProgress<LocalInstallEvent> _events = new();

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(null, Home);

    ConfigRoot Config => field ??= new(Tmp.PathTo("cfg"));

    string ConfigPath => AppConfig.GetConfigPath(Config);

    string Bin => Path.Combine(Root.Directory, "bin");

    string LaunchAgents => Path.Combine(Home.Path, "Library", "LaunchAgents");

    string Plist => Path.Combine(LaunchAgents, LocalServerUnit.ProductionLabel + ".plist");

    FakeLocalServerService Service => field ??= new(_log, Home);

    FakeLocalServer Server => field ??= new(_log, Root);

    FakeBundle Bundle => field ??= new(Tmp.PathTo("bundle.tar.gz"), Version);

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    [Before(Test)]
    public void WriteConfig() =>
        Tmp.CreateFile("cfg/config.json", """{"version":2,"active_profile":"default","profiles":{"default":{}}}""");

    LocalInstaller Installer(LocalRoot? root = null, ConfigRoot? config = null, FakeLocalServer? server = null, FakeBundle? bundle = null,
        FakeTimeProvider? time = null) {
        var clock = time ?? _time;
        var http  = new HttpClient(server ?? Server);

        return new(root ?? Root, config ?? Config, Home, Service, (bundle ?? Bundle).Client(), http, (bundle ?? Bundle).Key, MacArm, clock,
            installer => [new LocalSetupRecovery(Service, Home, http, clock, installer.Progress, installer.RolledForward)]) {
            Version     = Version,
            SocketLimit = int.MaxValue,
        };
    }

    static readonly LocalInstallAnswers Yes = new(SingleUser: true, Confirmed: true);

    /// <summary>The failure, or null when the setup committed. Once a roll-forward has committed, the setup goes on to
    /// reuse that installation; only the recovery is under test here.</summary>
    async Task<LocalInstallFailure?> Run(LocalInstaller? installer = null) {
        try {
            await (installer ?? Installer()).InstallAsync(Yes, _events, CancellationToken.None);
            return null;
        } catch (LocalInstallFailure failure) {
            return failure;
        }
    }

    /// <summary>The first setup, stopped at <paramref name="trigger"/> once <paramref name="after"/> has been passed
    /// and <paramref name="when"/> holds. The process is gone from there on, so its in-process rollback dies too.</summary>
    async Task CrashAt(string trigger, Func<bool>? when = null, LocalInstaller? installer = null, string? after = null) {
        var crashed = false;
        var armed   = after is null;

        using (FaultPoints.Arm(point => {
                   if (point == after) armed = true;

                   if (armed && !crashed && point == trigger && (when?.Invoke() ?? true)) {
                       crashed = true;
                       throw new SimulatedCrash();
                   }

                   if (crashed && point.StartsWith("rollback:", StringComparison.Ordinal)) throw new SimulatedCrash();
               }))
            await Run(installer);

        await Assert.That(crashed).IsTrue();
    }

    /// <summary>A crash after the unit was installed and before the point of no return.</summary>
    async Task CrashAtUnitAfterInstall() {
        await CrashAt("setup:before-committing");

        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Unit);
        await Assert.That(File.Exists(Plist)).IsTrue();
    }

    /// <summary>Runs the next setup, recording every fault point it passes and the journal, registry, profile and
    /// deletions into the shared log.</summary>
    async Task<LocalInstallFailure?> Recover(LocalInstaller? installer = null) {
        _log.Add("--- recovery");

        using (FaultPoints.Arm(point => {
                   _points.Add(point);

                   if (point == $"renamed:{Root.JournalPath}") _log.Add($"journal:{JournalPhase()}");
                   else if (point == $"unlink:{Root.JournalPath}") _log.Add("journal:cleared");
                   else if (point == $"renamed:{Root.RegistryPath}") _log.Add("registry");
                   else if (point == $"renamed:{ConfigPath}") _log.Add("profile");
                   else if (point.StartsWith($"unlink:{Root.Directory}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                       _log.Add($"delete:{Path.GetRelativePath(Root.Directory, point["unlink:".Length..])}");
               }))
            return await Run(installer);
    }

    List<string> RecoveryLog => [.. _log.SkipWhile(e => e != "--- recovery").Skip(1)];

    string? JournalPhase() => Journal(Root)?.Phase;

    static LifecycleJournalEntry? Journal(LocalRoot root) =>
        LocalStateFiles.ReadJournal(root) is StateRead<LifecycleJournalEntry>.Present(var entry) ? entry : null;

    bool JournalAbsent() => JournalAbsent(Root);

    static bool JournalAbsent(LocalRoot root) => LocalStateFiles.ReadJournal(root) is StateRead<LifecycleJournalEntry>.Absent;

    static bool Registered(LocalRoot root) => LocalStateFiles.ReadRegistry(root) is StateRead<InstallRegistry>.Present { Value.Active: not null };

    int PointAt(string point) => _points.IndexOf(point);

    async Task AssertBefore(string first, string second) {
        await Assert.That(PointAt(first)).IsGreaterThanOrEqualTo(0).Because(string.Join(", ", _points));
        await Assert.That(PointAt(second)).IsGreaterThan(PointAt(first)).Because(string.Join(", ", _points));
    }

    async Task AssertRecovered(string phase) {
        var recovering = _events.Reports.IndexOf(new LocalInstallEvent.Recovering(LocalSetupJournal.Operation, phase));
        var recovered  = _events.Reports.IndexOf(new LocalInstallEvent.Recovered(LocalSetupJournal.Operation, phase));

        await Assert.That(recovering).IsGreaterThanOrEqualTo(0);
        await Assert.That(recovered).IsGreaterThan(recovering);
    }

    /// <summary>The installation the interrupted setup recorded is active, the profile points at it, and the journal is
    /// gone, which the recovery reaches only through a proven lift.</summary>
    async Task AssertCommitted(LifecycleJournalEntry interrupted, ConfigRoot? config = null) {
        var url = interrupted.Details[LocalSetupJournal.ServerUrlKey];

        await Assert.That(JournalAbsent()).IsTrue();

        var registry = LocalStateFiles.ReadRegistry(Root) as StateRead<InstallRegistry>.Present;
        await Assert.That(registry?.Value.Active?.Id).IsEqualTo(interrupted.InstallationId);

        var profile = ConfigMutator.LoadPure(AppConfig.GetConfigPath(config ?? Config)).Profiles["default"];
        await Assert.That(profile.ServerUrl).IsEqualTo(url);
        await Assert.That(profile.AuthProvider).IsEqualTo(new AuthProviderStamp(AuthProvider.None, url));

        await Assert.That(RecoveryLog).Contains("GET /auth/config");
    }

    [Test]
    [Arguments("setup:after-committing", LocalSetupJournal.Committing)]
    [Arguments("setup:after-registry", LocalSetupJournal.Committing)]
    [Arguments("setup:after-profile", LocalSetupJournal.Committing)]
    [Arguments("rename:<journal>", LocalSetupJournal.Committing)]
    [Arguments("renamed:<journal>", LocalSetupJournal.Allowed)]
    public async Task A_crash_past_the_point_of_no_return_is_rolled_forward(string point, string phase) {
        // A journal point counts only in the allowed write, which follows the profile.
        if (point.EndsWith("<journal>", StringComparison.Ordinal))
            await CrashAt(point.Replace("<journal>", Root.JournalPath, StringComparison.Ordinal), after: "setup:after-profile");
        else
            await CrashAt(point);

        var interrupted = Journal(Root)!;
        await Assert.That(interrupted.Phase).IsEqualTo(phase);

        var failure = await Recover();

        await Assert.That(failure).IsNull();
        await AssertCommitted(interrupted);
        await AssertRecovered(phase);
        await Assert.That(RecoveryLog).DoesNotContain("stop");
    }

    [Test]
    public async Task A_cancel_during_a_roll_forward_returns_the_recovered_commit() {
        await CrashAt("setup:after-committing");
        var interrupted = Journal(Root)!;

        using var cts = new CancellationTokenSource();

        LocalInstallEvent.Committed committed;

        using (FaultPoints.Arm(point => { if (point == "setup:after-registry") cts.Cancel(); }))
            committed = await Installer().InstallAsync(Yes, _events, cts.Token);

        await Assert.That(committed.InstallationId).IsEqualTo(interrupted.InstallationId);
        await Assert.That(_events.Reports[^1]).IsEqualTo(committed);
        await AssertRecovered(LocalSetupJournal.Committing);
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_cancel_during_the_reuse_after_a_roll_forward_returns_the_recovered_commit() {
        await CrashAt("setup:after-committing");
        var interrupted = Journal(Root)!;

        using var cts     = new CancellationTokenSource();
        var       reports = new List<LocalInstallEvent>();
        var       events  = new SyncProgress<LocalInstallEvent>(e => {
            reports.Add(e);
            if (e is LocalInstallEvent.PreflightPassed) cts.Cancel();
        });

        var committed = await Installer().InstallAsync(Yes, events, cts.Token);

        await Assert.That(committed.InstallationId).IsEqualTo(interrupted.InstallationId);
        await Assert.That(reports.OfType<LocalInstallEvent.PreflightPassed>()).IsNotEmpty();
        await Assert.That(reports[^1]).IsEqualTo(committed);
        await Assert.That(reports.OfType<LocalInstallEvent.Committed>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task A_reuse_failure_after_a_successful_recovery_is_a_failed_start() {
        await CrashAt("setup:after-committing");
        Service.OnStart = () => throw new LocalLifecycleException("launchctl kickstart failed");

        var failure = await Recover();

        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.StartFailed);
    }

    [Test]
    public async Task A_restart_refused_during_recovery_is_a_failed_recovery() {
        await CrashAt($"renamed:{Root.JournalPath}", after: "setup:after-profile");
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Allowed);

        Tmp.CreateDir("other");
        var other = new OwnerReading.Root(Tmp.GetResolvedPath("other"));
        Server.WhileAllowed = FakeAuthConfig.Forbidden;
        Server.OnRequest = path => {
            if (path != "/auth/config") return;
            Service.OwnerOverride = new(other, other);
            _time.Advance(TimeSpan.FromSeconds(11));
        };

        var failure = await Recover();

        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Allowed);
        await Assert.That(RecoveryLog).DoesNotContain("stop");
    }

    [Test]
    public async Task Registry_renamed_then_crash_is_republished_before_allowed() {
        await CrashAt($"renamed:{Root.RegistryPath}");

        await Assert.That(Registered(Root)).IsTrue();
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Committing);

        var interrupted = Journal(Root)!;

        await Recover();

        await AssertBefore($"rename:{Root.RegistryPath}", $"rename:{Root.JournalPath}");
        await AssertCommitted(interrupted);
    }

    [Test]
    public async Task Mkdir_then_crash_resyncs_the_parent_before_allowed() {
        var parent = Tmp.PathTo("fresh", "a");
        var config = new ConfigRoot(Path.Combine(parent, "b"));

        await CrashAt($"mkdir:{parent}", installer: Installer(config: config));

        await Assert.That(Directory.Exists(parent)).IsTrue();
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Committing);

        var interrupted = Journal(Root)!;

        await Recover(Installer(config: config));

        await AssertBefore($"sync:{Path.GetDirectoryName(parent)}", $"rename:{Root.JournalPath}");
        await AssertCommitted(interrupted, config);
    }

    [Test]
    public async Task A_missing_unit_file_is_reinstalled_before_the_lift_is_confirmed() {
        await CrashAt("setup:after-committing");

        File.Delete(Plist);
        Service.LoadedRoot = null;

        var interrupted = Journal(Root)!;

        await Recover();

        await AssertCommitted(interrupted);

        var log = RecoveryLog;
        await Assert.That(log.IndexOf("install")).IsGreaterThanOrEqualTo(0);
        await Assert.That(log.IndexOf("GET /healthz/ready")).IsGreaterThan(log.IndexOf("install"));
        await Assert.That(log.IndexOf("registry")).IsGreaterThan(log.IndexOf("GET /healthz/ready"));
        await Assert.That(log.LastIndexOf("GET /auth/config")).IsGreaterThan(log.IndexOf("journal:allowed"));
        await Assert.That(File.Exists(Plist)).IsTrue();
    }

    /// <summary>The plist was replaced outside kcap by another root's, serving the same port. Its unfenced answer
    /// must not count as this installation's lift, nor its profile be pointed there.</summary>
    [Test]
    public async Task A_foreign_unit_at_committing_publishes_nothing() {
        await CrashAt("setup:after-committing");

        Tmp.CreateDir("other");
        var other = Tmp.GetResolvedPath("other");
        File.WriteAllText(Plist, other);
        Service.LoadedRoot = other;

        var journal = File.ReadAllBytes(Root.JournalPath);
        var config  = File.ReadAllBytes(ConfigPath);

        var failure = await Recover();

        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(File.ReadAllBytes(Root.JournalPath)).IsEquivalentTo(journal);
        await Assert.That(File.ReadAllBytes(ConfigPath)).IsEquivalentTo(config);
        await Assert.That(Registered(Root)).IsFalse();
        await Assert.That(RecoveryLog).IsEmpty();
        await Assert.That(File.ReadAllText(Plist)).IsEqualTo(other);
    }

    [Test]
    public async Task A_missing_server_binary_fails_and_keeps_the_journal() {
        await CrashAt("setup:after-committing");

        File.Delete(Path.Combine(Bin, "current", "kcap-server"));

        var failure = await Recover();

        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Committing);
        await Assert.That(Registered(Root)).IsFalse();
        await Assert.That(RecoveryLog).DoesNotContain("install");
    }

    [Test]
    public async Task Pending_with_data_and_corrupt_current_config_rolls_back() {
        await CrashAt($"renamed:{Root.JournalPath}");

        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Pending);

        Tmp.CreateFile("home/.config/kcap/local/data/base/1", "rows");
        File.WriteAllText(ConfigPath, "{ not json");

        var failure = await Recover();

        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.ConfigUnreadable);
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(Root.Directory, "data"))).IsFalse();
        await Assert.That(RecoveryLog).DoesNotContain("uninstall");
        await AssertRecovered(LocalSetupJournal.Pending);
    }

    [Test]
    public async Task A_failed_unload_retries_with_another_verified_unload() {
        if (OperatingSystem.IsWindows()) return;

        await CrashAtUnitAfterInstall();

        // The instance lock held: the stack outlives the unload, as a postmaster that ignored the stop would.
        var serverLock = Tmp.CreateFile($"home/.config/kcap/local/run/{LocalStackProbe.ServerLockName}");

        LocalInstallFailure? first;
        using (new FileStream(serverLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            first = await Recover();

        await Assert.That(first?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Unit);
        await Assert.That(Directory.Exists(Bin)).IsTrue();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.RolledBack>()).IsEmpty();

        var second = await Recover();

        await Assert.That(second).IsNull();

        var log = _log.SkipWhile(e => e != "--- recovery").ToList();
        await Assert.That(log.Count(e => e == "uninstall")).IsEqualTo(2);
        await Assert.That(log.LastIndexOf("uninstall")).IsLessThan(log.IndexOf("delete:bin"));
        await AssertRecovered(LocalSetupJournal.Unit);
    }

    [Test]
    public async Task Plist_unlinked_then_crash_syncs_LaunchAgents_before_clearing() {
        await CrashAtUnitAfterInstall();

        LocalInstallFailure? first;
        using (FaultPoints.Arm(p => { if (p == $"unlink:{Plist}") throw new SimulatedCrash(); }))
            first = await Run();

        await Assert.That(first?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(File.Exists(Plist)).IsFalse();
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Unit);

        await Recover();

        await AssertBefore($"sync:{LaunchAgents}", $"unlink:{Root.JournalPath}");
        await AssertRecovered(LocalSetupJournal.Unit);
    }

    [Test]
    public async Task A_foreign_root_unit_is_left_untouched() {
        await CrashAt($"renamed:{Root.JournalPath}", () => JournalPhase() == LocalSetupJournal.Unit);

        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Unit);
        await Assert.That(File.Exists(Plist)).IsFalse();

        // Setup B, under another root, installs and commits while A's journal waits.
        var rootB = LocalRoot.Resolve(Path.Combine(Home.Path, "rootB"), Home);
        await Assert.That(await Run(Installer(rootB, new(Tmp.PathTo("cfgB")), new FakeLocalServer([], rootB),
            new FakeBundle(Tmp.PathTo("bundleB.tar.gz"), Version)))).IsNull();

        var plistB = File.ReadAllText(Plist);
        var owner  = Service.Owner(LocalServerUnit.ForInstallation(rootB, 0));

        await Recover();

        await Assert.That(RecoveryLog).DoesNotContain("uninstall");
        await Assert.That(File.ReadAllText(Plist)).IsEqualTo(plistB);
        await Assert.That(Service.Owner(LocalServerUnit.ForInstallation(rootB, 0))).IsEqualTo(owner);
        await Assert.That(Directory.Exists(Bin)).IsFalse();
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(rootB.Directory, "bin"))).IsTrue();
    }

    [Test]
    public async Task An_alias_root_unit_is_ours() {
        await CrashAtUnitAfterInstall();

        var alias = Tmp.PathTo("alias");
        Directory.CreateSymbolicLink(alias, Root.Directory);
        File.WriteAllText(Plist, alias);
        Service.LoadedRoot = alias;

        await Recover();

        var log = RecoveryLog;
        await Assert.That(log).Contains("uninstall");
        await Assert.That(log.IndexOf("uninstall")).IsLessThan(log.IndexOf("delete:bin"));
        await AssertRecovered(LocalSetupJournal.Unit);
    }

    [Test]
    [Arguments("loaded, unreadable plist")]
    [Arguments("loaded and plist name different roots")]
    [Arguments("unreadable plist, absent label")]
    public async Task Unknown_owner_keeps_everything(string evidence) {
        await CrashAtUnitAfterInstall();

        Tmp.CreateDir("other");
        var ours       = new OwnerReading.Root(Root.Directory);
        var unreadable = new OwnerReading.Unreadable("the plist does not parse");

        Service.OwnerOverride = evidence switch {
            "loaded, unreadable plist"              => new(ours, unreadable),
            "loaded and plist name different roots" => new(ours, new OwnerReading.Root(Tmp.GetResolvedPath("other"))),
            _                                       => new(OwnerReading.Absent.Instance, unreadable),
        };

        var journal = File.ReadAllBytes(Root.JournalPath);

        var failure = await Recover();

        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(File.ReadAllBytes(Root.JournalPath)).IsEquivalentTo(journal);
        await Assert.That(Directory.Exists(Bin)).IsTrue();
        await Assert.That(RecoveryLog).DoesNotContain("uninstall");
    }

    [Test]
    [Arguments("installation_id")]
    [Arguments("version")]
    [Arguments("port 0")]
    [Arguments("port 70000")]
    [Arguments("server_url")]
    [Arguments("config_dir")]
    [Arguments("profile")]
    [Arguments("committing/allow")]
    [Arguments("allowed/fenced")]
    public async Task A_malformed_setup_journal_changes_nothing(string malformed) {
        var details = new Dictionary<string, string>(StringComparer.Ordinal) {
            [LocalSetupJournal.PortKey]      = "9473",
            [LocalSetupJournal.VersionKey]   = Version,
            [LocalSetupJournal.ServerUrlKey] = "http://localhost:9473",
            [LocalSetupJournal.ConfigDirKey] = Config.Directory,
            [LocalSetupJournal.ProfileKey]   = "default",
        };

        var id          = Convert.ToHexStringLower(new byte[16]);
        var phase       = LocalSetupJournal.Unit;
        var serverStart = ServerStart.Fenced;
        string field;

        switch (malformed) {
            case "installation_id": id = "xyz"; field = "installation_id"; break;
            case "version": details[LocalSetupJournal.VersionKey] = "../x"; field = "version"; break;
            case "port 0" or "port 70000":
                var port = malformed[5..];
                details[LocalSetupJournal.PortKey]      = port;
                details[LocalSetupJournal.ServerUrlKey] = $"http://localhost:{port}";
                field                                   = "port";
                break;
            case "server_url": details[LocalSetupJournal.ServerUrlKey] = "http://127.0.0.1:9473"; field = "server_url"; break;
            case "config_dir": details[LocalSetupJournal.ConfigDirKey] = "relative/cfg"; field = "config_dir"; break;
            case "profile": details[LocalSetupJournal.ProfileKey] = "../x"; field = "profile"; break;
            case "committing/allow": phase = LocalSetupJournal.Committing; serverStart = ServerStart.Allow; field = "server_start"; break;
            default: phase = LocalSetupJournal.Allowed; field = "server_start"; break;
        }

        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            lease.Begin(LocalSetupJournal.Operation, phase, serverStart, id, details);

        Tmp.CreateFile("home/.config/kcap/local/bin/current/kcap-server", "#!/bin/sh\n");
        Tmp.CreateFile("home/.config/kcap/local/data/base/1", "rows");
        Service.Install(LocalServerUnit.ForInstallation(Root, 9473));
        _log.Clear();

        var installer = Installer();
        var before    = Snapshot();

        var failure = await Recover(installer);

        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(failure!.Message).StartsWith($"{field}: ");
        await Assert.That(Snapshot()).IsEquivalentTo(before);
        await Assert.That(RecoveryLog).IsEmpty();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.Recovering>()).IsEmpty();
    }

    [Test]
    public async Task An_unknown_phase_needs_a_newer_kcap() {
        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            lease.Begin(LocalSetupJournal.Operation, "migrating", ServerStart.Fenced, Convert.ToHexStringLower(new byte[16]));

        var journal = File.ReadAllBytes(Root.JournalPath);

        var failure = await Recover();

        await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(failure!.Message).Contains("newer kcap");
        await Assert.That(File.ReadAllBytes(Root.JournalPath)).IsEquivalentTo(journal);
    }

    /// <summary>Every directory and file under the temp directory, with each file's bytes; lock files are created empty
    /// by whoever takes them.</summary>
    Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFileSystemEntries(Tmp.Path, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(f => f, f => Directory.Exists(f) ? "<dir>" : Convert.ToBase64String(File.ReadAllBytes(f)), StringComparer.Ordinal);

    [Test]
    [NotInParallel]
    public async Task Recovery_publishes_to_the_recorded_absolute_config_dir() {
        // The installer resolves a relative config directory against the process's own, which is what this pins.
#pragma warning disable RS0030
        var original = Directory.GetCurrentDirectory();

        Tmp.CreateDir("cwdA");
        Tmp.CreateDir("cwdB");
        Tmp.CreateFile("cwdA/rel-cfg/config.json", """{"version":2,"active_profile":"default","profiles":{"default":{}}}""");
        var broken = Tmp.CreateFile("broken/config.json", "{ not json");

        ConfigRoot recorded;

        try {
            Directory.SetCurrentDirectory(Tmp.PathTo("cwdA"));
            recorded = new(Path.GetFullPath("rel-cfg"));

            await CrashAt("setup:after-committing", installer: Installer(config: new ConfigRoot("rel-cfg")));

            var interrupted = Journal(Root)!;
            await Assert.That(interrupted.Details[LocalSetupJournal.ConfigDirKey]).IsEqualTo(recorded.Directory);

            Directory.SetCurrentDirectory(Tmp.PathTo("cwdB"));

            var failure = await Recover(Installer(config: new ConfigRoot(Path.GetDirectoryName(broken)!)));

            await Assert.That(failure?.Code).IsEqualTo(LocalInstallFailureCode.ConfigUnreadable);
            await AssertCommitted(interrupted, recorded);
            await Assert.That(File.ReadAllText(broken)).IsEqualTo("{ not json");
            await Assert.That(Directory.Exists(Tmp.PathTo("cwdB", "rel-cfg"))).IsFalse();
        } finally {
            Directory.SetCurrentDirectory(original);
        }
#pragma warning restore RS0030
    }

    [Test]
    public async Task Two_roots_set_up_concurrently_commit_exactly_one() {
        var rootB = LocalRoot.Resolve(Path.Combine(Home.Path, "rootB"), Home);

        var a = Installer(Root, Config, new FakeLocalServer([], Root), new FakeBundle(Tmp.PathTo("a.tar.gz"), Version), new FakeTimeProvider());
        var b = Installer(rootB, new(Tmp.PathTo("cfgB")), new FakeLocalServer([], rootB), new FakeBundle(Tmp.PathTo("b.tar.gz"), Version),
            new FakeTimeProvider());

        var outcomes = await Task.WhenAll(Outcome(a), Outcome(b));

        await Assert.That(outcomes.Count(o => o is null)).IsEqualTo(1).Because(string.Join(", ", outcomes));
        await Assert.That(outcomes.Single(o => o is not null)).IsEqualTo(LocalInstallFailureCode.LeftoverState);
        await Assert.That(Registered(Root) ^ Registered(rootB)).IsTrue();
    }

    static async Task<LocalInstallFailureCode?> Outcome(LocalInstaller installer) {
        await Task.Yield();

        try {
            await installer.InstallAsync(Yes, new RecordingProgress<LocalInstallEvent>(), CancellationToken.None);
            return null;
        } catch (LocalInstallFailure failure) {
            return failure.Code;
        }
    }
}
