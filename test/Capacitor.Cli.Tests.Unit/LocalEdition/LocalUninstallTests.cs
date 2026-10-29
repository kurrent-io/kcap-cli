using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>An installation made by a real setup against the fakes, then uninstalled. Nothing may be deleted before
/// every stop is verified, and a purge deletes nothing before the retirement is durable.</summary>
[ExcludeOn(OS.Windows)]
public class LocalUninstallTests {
    const string Version = "1.2.3";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];
    readonly FakeTimeProvider _time = new();
    readonly RecordingProgress<LocalInstallEvent> _events = new();

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(null, Home);

    ConfigRoot Config => field ??= new(Tmp.PathTo("cfg"));

    FakeLocalServerService Service => field ??= new(_log, Home);

    FakeLocalServer Server => field ??= new(_log, Root);

    FakeBundle Bundle => field ??= new(Tmp.PathTo("bundle.tar.gz"), Version);

    FakeOutgoingRecorders Recorders => field ??= new(_log);

    LocalStack Stack => field ??= new(Root, Home, Service, new HttpClient(Server), _time, Recorders, Config);

    string Dir(string name) => Path.Combine(Root.Directory, name);

    string Plist => Service.UnitFile(LocalServerUnit.ForInstallation(Root, 0));

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    [Before(Test)]
    public void WriteConfig() =>
        Tmp.CreateFile("cfg/config.json", """{"version":2,"active_profile":"default","profiles":{"default":{}}}""");

    LocalInstaller Installer() {
        var http = new HttpClient(Server);

        return new(Root, Config, Home, Service, Bundle.Client(), http, Bundle.Key, MacArm, _time,
            installer => [
                new LocalSetupRecovery(Service, Home, http, _time, installer.Progress, installer.RolledForward),
                new LocalPurgeRecovery(Service, Home, Recorders, Config, mayStop: false, force: false, installer.Progress),
            ]) {
            Version     = Version,
            SocketLimit = int.MaxValue,
        };
    }

    /// <summary>A committed installation with a store's data in place, as a server that ran would leave it.</summary>
    async Task<LocalInstallEvent.Committed> InstallAsync() {
        var committed = await Installer().InstallAsync(new(SingleUser: true, Confirmed: true), _events, CancellationToken.None);

        WriteUnderRoot(Path.Combine("data", "postgres", "18", "PG_VERSION"), "18");
        Directory.CreateDirectory(Dir("run"));
        Directory.CreateDirectory(Dir("logs"));

        return committed;
    }

    /// <summary>The root is the default one under the test's home.</summary>
    void WriteUnderRoot(string relative, string content) =>
        Tmp.CreateFile(Path.Combine("home", ".config", "kcap", "local", relative), content);

    Task<LocalUninstallOutcome> UninstallAsync(bool purge, bool force = false) =>
        Stack.RunAsync(_events,
            (lease, state, ct) => new LocalUninstall(Service, Home, Recorders).RunAsync(lease, state, purge, force, Config.Directory, ct),
            CancellationToken.None);

    InstallRegistry Registry() => ((StateRead<InstallRegistry>.Present)LocalStateFiles.ReadRegistry(Root)).Value;

    bool JournalAbsent() => LocalStateFiles.ReadJournal(Root) is StateRead<LifecycleJournalEntry>.Absent;

    Profile DefaultProfile() => ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config)).Profiles["default"];

    /// <summary>Everything an uninstall removes is still there.</summary>
    async Task AssertUntouched(LocalInstallEvent.Committed installed) {
        await Assert.That(Directory.Exists(Dir("bin"))).IsTrue();
        await Assert.That(Directory.Exists(Dir("run"))).IsTrue();
        await Assert.That(Directory.Exists(Dir("data"))).IsTrue();
        await Assert.That(File.Exists(Plist)).IsTrue();
        await Assert.That(Registry().Active?.Id).IsEqualTo(installed.InstallationId);
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task Running_agents_refuse_the_uninstall_before_anything_is_stopped() {
        var installed = await InstallAsync();
        Recorders.Busy.Add("daemon 'me': 2 agent(s) running");
        _log.Clear();

        var refused = await Assert.ThrowsAsync<LocalLifecycleException>(() => UninstallAsync(purge: true));

        await Assert.That(refused!.Message).Contains("2 agent(s) running");
        await Assert.That(_log).IsEquivalentTo(["recorders:busy?"]);
        await AssertUntouched(installed);
    }

    [Test]
    public async Task Force_uninstalls_while_agents_run() {
        await InstallAsync();
        Recorders.Busy.Add("daemon 'me': 1 agent(s) running");

        await Assert.That(await UninstallAsync(purge: false, force: true)).IsEqualTo(LocalUninstallOutcome.Uninstalled);
        await Assert.That(_log).Contains("recorders:stop");
    }

    [Test]
    public async Task A_recorder_that_cannot_be_stopped_leaves_the_installation_as_it_is() {
        var installed = await InstallAsync();
        Recorders.StopSucceeds = false;
        _log.Clear();

        await Assert.ThrowsAsync<LocalLifecycleException>(() => UninstallAsync(purge: true));

        await Assert.That(_log).DoesNotContain("uninstall");
        await AssertUntouched(installed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_unit_stop_that_fails_deletes_nothing(bool purge) {
        var installed = await InstallAsync();
        Service.UninstallFailure = new LocalLifecycleException("launchctl bootout failed");
        Service.StopFailure      = new LocalLifecycleException("launchctl bootout failed");

        var refused = await Assert.ThrowsAsync<LocalLifecycleException>(() => UninstallAsync(purge));

        await Assert.That(refused!.Message).Contains("bootout");
        await AssertUntouched(installed);
    }

    [Test]
    public async Task A_store_still_alive_after_the_stop_deletes_nothing() {
        var installed = await InstallAsync();
        await using var held = new FileStream(Path.Combine(Dir("run"), LocalStackProbe.ServerLockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<LocalLifecycleException>(() => UninstallAsync(purge: true));

        await Assert.That(Directory.Exists(Dir("bin"))).IsTrue();
        await Assert.That(Directory.Exists(Dir("data"))).IsTrue();
        await Assert.That(Registry().Active?.Id).IsEqualTo(installed.InstallationId);
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_unit_whose_owner_cannot_be_told_is_left_alone_and_nothing_is_deleted() {
        var installed = await InstallAsync();
        Service.OwnerOverride = new(new OwnerReading.Unreadable("launchctl print failed"), OwnerReading.Absent.Instance);

        await Assert.ThrowsAsync<LocalLifecycleException>(() => UninstallAsync(purge: false));

        await Assert.That(_log).DoesNotContain("uninstall");
        await AssertUntouched(installed);
    }

    [Test]
    public async Task A_unit_serving_another_root_is_left_loaded() {
        await InstallAsync();
        var other = Tmp.CreateDir("other-root");
        Service.OwnerOverride = new(new OwnerReading.Root(other), new OwnerReading.Root(other));

        await Assert.That(await UninstallAsync(purge: false)).IsEqualTo(LocalUninstallOutcome.Uninstalled);

        await Assert.That(_log).DoesNotContain("uninstall");
        await Assert.That(File.Exists(Plist)).IsTrue();
    }

    [Test]
    public async Task Uninstall_removes_the_unit_and_binaries_and_keeps_the_data_registry_and_profile() {
        var installed = await InstallAsync();

        await Assert.That(await UninstallAsync(purge: false)).IsEqualTo(LocalUninstallOutcome.Uninstalled);

        await Assert.That(File.Exists(Plist)).IsFalse();
        await Assert.That(Directory.Exists(Dir("bin"))).IsFalse();
        await Assert.That(Directory.Exists(Dir("run"))).IsFalse();
        await Assert.That(Directory.Exists(Dir("data"))).IsTrue();
        await Assert.That(Registry().Active?.Id).IsEqualTo(installed.InstallationId);
        await Assert.That(DefaultProfile().ServerUrl).IsEqualTo(installed.ServerUrl);
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task The_recorders_stop_before_the_unit_and_the_unit_before_any_deletion() {
        await InstallAsync();
        _log.Clear();
        var deleted = -1;

        using (FaultPoints.Arm(point => {
                   if (deleted < 0 && point.StartsWith($"unlink:{Root.Directory}", StringComparison.Ordinal)) deleted = _log.Count;
               }))
            await UninstallAsync(purge: false);

        await Assert.That(_log).IsEquivalentTo(["recorders:busy?", "recorders:stop", "uninstall"]);
        await Assert.That(deleted).IsEqualTo(3);
    }

    [Test]
    public async Task A_reinstall_over_retained_data_returns_as_the_same_installation_on_the_same_port() {
        var installed = await InstallAsync();
        await UninstallAsync(purge: false);
        Bundle.Requests.Clear();

        var again = await Installer().InstallAsync(new(SingleUser: true, Confirmed: false), _events, CancellationToken.None);

        await Assert.That(again.InstallationId).IsEqualTo(installed.InstallationId);
        await Assert.That(again.Port).IsEqualTo(installed.Port);
        await Assert.That(File.Exists(LocalServerUnit.ForInstallation(Root, again.Port).ServerBinary)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(Dir("data"), "postgres", "18", "PG_VERSION"))).IsTrue();
        await Assert.That(File.Exists(Plist)).IsTrue();
        await Assert.That(Bundle.Requests.Any(r => r.EndsWith(".tar.gz", StringComparison.Ordinal))).IsTrue();
        await Assert.That(_events.Reports).Contains(new LocalInstallEvent.Reused(installed.ServerUrl, Version));
    }

    [Test]
    public async Task A_reinstall_replaces_binaries_a_crashed_restage_left_behind() {
        var installed = await InstallAsync();
        await UninstallAsync(purge: false);
        WriteUnderRoot(Path.Combine("bin", ".staging-dead", "archive.tar.gz"), "partial");

        var again = await Installer().InstallAsync(new(SingleUser: true, Confirmed: false), _events, CancellationToken.None);

        await Assert.That(again.InstallationId).IsEqualTo(installed.InstallationId);
        await Assert.That(Directory.Exists(Path.Combine(Dir("bin"), ".staging-dead"))).IsFalse();
    }

    [Test]
    public async Task A_reinstall_refused_for_a_foreign_unit_leaves_the_binaries_alone() {
        await InstallAsync();
        await UninstallAsync(purge: false);
        WriteUnderRoot(Path.Combine("bin", ".staging-dead", "archive.tar.gz"), "partial");
        var other = Tmp.CreateDir("other-root");
        Service.OwnerOverride = new(new OwnerReading.Root(other), new OwnerReading.Root(other));
        Bundle.Requests.Clear();

        var refused = await Assert.ThrowsAsync<LocalInstallFailure>(
            () => Installer().InstallAsync(new(SingleUser: true, Confirmed: false), _events, CancellationToken.None));

        await Assert.That(refused!.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await Assert.That(File.Exists(Path.Combine(Dir("bin"), ".staging-dead", "archive.tar.gz"))).IsTrue();
        await Assert.That(Bundle.Requests).IsEmpty();
    }

    [Test]
    public async Task Purge_forgets_caches_kept_under_any_loopback_spelling() {
        var installed = await InstallAsync();
        var alias = $"http://127.0.0.1:{installed.Port}";
        ServerVersionStore.Set(alias, "1.2.3", Config, TimeProvider.System);

        await UninstallAsync(purge: true);

        await Assert.That(ServerVersionStore.Get(alias, Config)).IsNull();
    }

    [Test]
    public async Task A_registry_entry_a_purge_could_not_recover_is_refused_before_the_journal() {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty.WithActive(new("not-hex", 9473, "http://localhost:9473", Version)));
        WriteUnderRoot(Path.Combine("data", "PG_VERSION"), "18");

        var refused = await Assert.ThrowsAsync<LocalLifecycleException>(() => UninstallAsync(purge: true));

        await Assert.That(refused!.Message).Contains("installation_id");
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(Directory.Exists(Dir("data"))).IsTrue();
        await Assert.That(Registry().Active?.Id).IsEqualTo("not-hex");
    }

    [Test]
    public async Task Purge_retires_the_installation_and_deletes_what_belonged_to_it() {
        var installed = await InstallAsync();
        Tmp.CreateFile("cfg/spool/session.jsonl", "{}");
        Tmp.CreateFile("cfg/transcript-spool/session.jsonl", "{}");
        WriteUnderRoot(Path.Combine("models", new string('a', 64), "model.onnx"), "onnx");
        ServerVersionStore.Set(installed.ServerUrl, "1.2.3", Config, TimeProvider.System);

        await Assert.That(await UninstallAsync(purge: true)).IsEqualTo(LocalUninstallOutcome.Purged);

        var registry = Registry();
        await Assert.That(registry.Active).IsNull();
        await Assert.That(registry.Retired).IsEquivalentTo([new RetiredInstallation(installed.InstallationId, installed.Port, installed.ServerUrl)]);

        foreach (var name in new[] { "bin", "run", "data", "logs", "models" })
            await Assert.That(Directory.Exists(Dir(name))).IsFalse().Because(name);

        await Assert.That(Directory.Exists(Config.Path("spool"))).IsFalse();
        await Assert.That(Directory.Exists(Config.Path("transcript-spool"))).IsFalse();
        await Assert.That(ServerVersionStore.Get(installed.ServerUrl, Config)).IsNull();

        var profile = DefaultProfile();
        await Assert.That(profile.ServerUrl).IsNull();
        await Assert.That(profile.AuthProvider).IsNull();
        await Assert.That(File.Exists(Plist)).IsFalse();
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(new LocalEndpoints(LocalStateFiles.ReadRegistry(Root)).Classify(installed.ServerUrl)).IsTypeOf<LocalEndpointClass.Retired>();
    }

    [Test]
    public async Task Purge_of_an_installation_already_uninstalled_finishes_the_job() {
        var installed = await InstallAsync();
        await UninstallAsync(purge: false);

        await Assert.That(await UninstallAsync(purge: true)).IsEqualTo(LocalUninstallOutcome.Purged);

        await Assert.That(Registry().Retired.Select(r => r.Id)).IsEquivalentTo([installed.InstallationId]);
        await Assert.That(Directory.Exists(Dir("data"))).IsFalse();
    }

    [Test]
    public async Task Purge_then_setup_creates_a_new_installation_on_a_port_that_was_not_retired() {
        var installed = await InstallAsync();
        await UninstallAsync(purge: true);

        var fresh = await Installer().InstallAsync(new(SingleUser: true, Confirmed: true), _events, CancellationToken.None);

        await Assert.That(fresh.InstallationId).IsNotEqualTo(installed.InstallationId);
        await Assert.That(fresh.Port).IsNotEqualTo(installed.Port);
        await Assert.That(Registry().Active?.Id).IsEqualTo(fresh.InstallationId);
        await Assert.That(Registry().Retired.Select(r => r.Port)).IsEquivalentTo([installed.Port]);
        await Assert.That(DefaultProfile().ServerUrl).IsEqualTo(fresh.ServerUrl);
    }

    [Test]
    public async Task With_nothing_installed_there_is_nothing_to_do() {
        await Assert.That(await UninstallAsync(purge: true)).IsEqualTo(LocalUninstallOutcome.NotInstalled);
        await Assert.That(_log).IsEmpty();
    }

    /// <summary>The process dies at <paramref name="point"/>. Until the retirement is durable nothing may be deleted,
    /// and at no moment is the origin neither active nor retired; the next lifecycle command then finishes the purge.</summary>
    [Test]
    [Arguments("purge:begun")]
    [Arguments("rename:<registry>")]
    [Arguments("renamed:<registry>")]
    [Arguments("purge:after-retire")]
    [Arguments("purge:after-retired")]
    [Arguments("unlink:<data>")]
    [Arguments("purge:after-data")]
    [Arguments("purge:after-caches")]
    [Arguments("purge:after-profile")]
    [Arguments("unlink:<journal>")]
    public async Task A_crash_at_any_step_of_a_purge_is_finished_by_the_next_lifecycle_command(string point) {
        var installed = await InstallAsync();
        var trigger = point
            .Replace("<registry>", Root.RegistryPath, StringComparison.Ordinal)
            .Replace("<data>", Dir("data"), StringComparison.Ordinal)
            .Replace("<journal>", Root.JournalPath, StringComparison.Ordinal);
        var crashed = false;

        using (FaultPoints.Arm(p => {
                   // Only the purge's own registry write counts: setup wrote the registry too.
                   if (!crashed && p == trigger && (point != "rename:<registry>" && point != "renamed:<registry>" || !JournalAbsent())) {
                       crashed = true;
                       throw new SimulatedCrash();
                   }
               }))
            await Assert.ThrowsAsync<SimulatedCrash>(() => UninstallAsync(purge: true));

        await Assert.That(crashed).IsTrue();

        var classified = new LocalEndpoints(LocalStateFiles.ReadRegistry(Root)).Classify(installed.ServerUrl);
        await Assert.That(classified is LocalEndpointClass.Local or LocalEndpointClass.Retired).IsTrue();

        if (classified is LocalEndpointClass.Local) {
            await Assert.That(Directory.Exists(Dir("bin"))).IsTrue();
            await Assert.That(Directory.Exists(Dir("data"))).IsTrue();
            await Assert.That(File.Exists(Plist)).IsTrue();
        }

        var state = await Stack.RunAsync(_events, (_, s, _) => Task.FromResult(s), CancellationToken.None);

        await Assert.That(state).IsEqualTo(LocalStackState.NotInstalled.Instance);
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(Registry().Retired.Select(r => r.Id)).IsEquivalentTo([installed.InstallationId]);
        await Assert.That(Directory.Exists(Dir("data"))).IsFalse();
        await Assert.That(DefaultProfile().ServerUrl).IsNull();
    }

    [Test]
    public async Task An_interrupted_purge_is_finished_before_setup_installs_anew() {
        var installed = await InstallAsync();

        using (FaultPoints.Arm(p => { if (p == "purge:after-retired") throw new SimulatedCrash(); }))
            await Assert.ThrowsAsync<SimulatedCrash>(() => UninstallAsync(purge: true));

        var fresh = await Installer().InstallAsync(new(SingleUser: true, Confirmed: true), _events, CancellationToken.None);

        await Assert.That(fresh.InstallationId).IsNotEqualTo(installed.InstallationId);
        await Assert.That(fresh.Port).IsNotEqualTo(installed.Port);
        await Assert.That(_events.Reports).Contains(new LocalInstallEvent.Recovered(LocalPurgeJournal.Operation, LocalPurgeJournal.Retired));
    }

    /// <summary>The journal denies the server a start, not a daemon or a watcher; one started since the crash is
    /// stopped before anything more is deleted.</summary>
    [Test]
    public async Task A_purge_recovered_by_a_local_command_stops_recorders_started_since_the_crash() {
        await InstallAsync();

        using (FaultPoints.Arm(p => { if (p == "purge:after-retired") throw new SimulatedCrash(); }))
            await Assert.ThrowsAsync<SimulatedCrash>(() => UninstallAsync(purge: true));

        Recorders.Alive.Add("Watcher abc");
        _log.Clear();

        await Stack.RunAsync(_events, (_, s, _) => Task.FromResult(s), CancellationToken.None);

        await Assert.That(_log.Take(2)).IsEquivalentTo(["recorders:busy?", "recorders:stop"]);
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(Directory.Exists(Dir("data"))).IsFalse();
    }

    async Task CrashAfterRetiredAsync() {
        using (FaultPoints.Arm(p => { if (p == "purge:after-retired") throw new SimulatedCrash(); }))
            await Assert.ThrowsAsync<SimulatedCrash>(() => UninstallAsync(purge: true));

        _log.Clear();
    }

    [Test]
    public async Task A_purge_recovery_will_not_end_running_agents_without_force() {
        await InstallAsync();
        await CrashAfterRetiredAsync();
        Recorders.Busy.Add("daemon 'me': 1 agent(s) running or starting");

        await Assert.ThrowsAsync<LocalLifecycleException>(() => Stack.RunAsync(_events, (_, s, _) => Task.FromResult(s), CancellationToken.None));

        await Assert.That(_log).DoesNotContain("recorders:stop");
        await Assert.That(JournalAbsent()).IsFalse();
        await Assert.That(Directory.Exists(Dir("data"))).IsTrue();

        await Stack.RunAsync(_events, (_, s, _) => Task.FromResult(s), CancellationToken.None, force: true);

        await Assert.That(JournalAbsent()).IsTrue();
    }

    /// <summary>The spools it would delete belong to the config root the purge began under, whose watchers another
    /// root's kcap cannot see.</summary>
    [Test]
    public async Task A_purge_begun_under_another_config_root_is_left_for_that_root() {
        await InstallAsync();
        await CrashAfterRetiredAsync();
        var elsewhere = new LocalStack(Root, Home, Service, new HttpClient(Server), _time, Recorders, new ConfigRoot(Tmp.CreateDir("other-cfg")));

        var refused = await Assert.ThrowsAsync<LocalLifecycleException>(
            () => elsewhere.RunAsync(_events, (_, s, _) => Task.FromResult(s), CancellationToken.None));

        await Assert.That(refused!.Message).Contains(Config.Directory);
        await Assert.That(_log).IsEmpty();
        await Assert.That(JournalAbsent()).IsFalse();
    }

    [Test]
    public async Task A_purge_whose_watchers_were_in_another_directory_is_left_for_that_directory() {
        await InstallAsync();
        await CrashAfterRetiredAsync();
        Recorders.WatcherDirectory = "/elsewhere/watchers";

        var refused = await Assert.ThrowsAsync<LocalLifecycleException>(
            () => Stack.RunAsync(_events, (_, s, _) => Task.FromResult(s), CancellationToken.None));

        await Assert.That(refused!.Message).Contains("KCAP_WATCHER_DIR=/watchers");
        await Assert.That(_log).IsEmpty();
        await Assert.That(JournalAbsent()).IsFalse();
    }

    /// <summary>Setup cannot stop them without printing into its JSON events, so it leaves the purge to the command
    /// that can.</summary>
    [Test]
    public async Task Setup_leaves_an_interrupted_purge_alone_while_a_recorder_is_alive() {
        await InstallAsync();

        using (FaultPoints.Arm(p => { if (p == "purge:after-retired") throw new SimulatedCrash(); }))
            await Assert.ThrowsAsync<SimulatedCrash>(() => UninstallAsync(purge: true));

        Recorders.Alive.Add("Watcher abc");
        _log.Clear();

        var refused = await Assert.ThrowsAsync<LocalInstallFailure>(
            () => Installer().InstallAsync(new(SingleUser: true, Confirmed: true), _events, CancellationToken.None));

        await Assert.That(refused!.Message).Contains("Watcher abc");
        await Assert.That(JournalAbsent()).IsFalse();
        await Assert.That(Directory.Exists(Dir("data"))).IsTrue();
        await Assert.That(_log).DoesNotContain("recorders:stop");
    }

    [Test]
    public async Task A_purge_journal_this_build_did_not_write_is_left_as_it_is() {
        var installed = await InstallAsync();

        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            lease.Begin(LocalPurgeJournal.Operation, LocalPurgeJournal.Retiring, ServerStart.Deny, installed.InstallationId,
                new Dictionary<string, string> { [LocalPurgeJournal.PortKey] = "0" });

        var refused = await Assert.ThrowsAsync<LocalLifecycleException>(
            () => Stack.RunAsync(_events, (_, s, _) => Task.FromResult(s), CancellationToken.None));

        await Assert.That(refused!.Message).Contains("port:");
        await Assert.That(_log).DoesNotContain("recorders:stop");
        await Assert.That(JournalAbsent()).IsFalse();
        await Assert.That(Registry().Active?.Id).IsEqualTo(installed.InstallationId);
        await Assert.That(Directory.Exists(Dir("data"))).IsTrue();
    }
}
