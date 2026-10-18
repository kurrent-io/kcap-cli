using System.Net;
using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalInstallerTests {
    const string Version = "1.2.3";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];
    readonly TimerSignallingTimeProvider _time = new();
    readonly RecordingProgress<LocalInstallEvent> _events = new();

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(null, Home);

    ConfigRoot Config => field ??= new(Tmp.PathTo("cfg"));

    string ConfigPath => AppConfig.GetConfigPath(Config);

    string Bin => Path.Combine(Root.Directory, "bin");

    string LaunchAgents => Path.Combine(Home.Path, "Library", "LaunchAgents");

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

    LocalInstaller Installer(FakeBundle? bundle = null) =>
        new(Root, Config, Home, Service, (bundle ?? Bundle).Client(), new HttpClient(Server), (bundle ?? Bundle).Key, MacArm, _time, _ => []) {
            Version     = Version,
            SocketLimit = int.MaxValue,
        };

    static readonly LocalInstallAnswers Yes = new(SingleUser: true, Confirmed: true);

    Task<LocalInstallEvent.Committed> Install(LocalInstallAnswers? answers = null, CancellationToken ct = default, FakeBundle? bundle = null) =>
        Installer(bundle).InstallAsync(answers ?? Yes, _events, ct);

    async Task<LocalInstallFailure> Failure(LocalInstallAnswers? answers = null, CancellationToken ct = default, FakeBundle? bundle = null) {
        try {
            await Install(answers, ct, bundle);
        } catch (LocalInstallFailure failure) {
            return failure;
        }

        throw new InvalidOperationException("The install committed.");
    }

    /// <summary>Records journal phases, the registry, the profile and the rollback's deletions into the shared log,
    /// then runs <paramref name="also"/>.</summary>
    IDisposable Record(Action<string>? also = null) =>
        FaultPoints.Arm(point => {
            if (point == $"renamed:{Root.JournalPath}") _log.Add($"journal:{JournalPhase()}");
            else if (point == $"unlink:{Root.JournalPath}") _log.Add("journal:cleared");
            else if (point == $"renamed:{Root.RegistryPath}") _log.Add("registry");
            else if (point == $"renamed:{ConfigPath}") _log.Add("profile");
            else if (point.StartsWith($"unlink:{Root.Directory}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                _log.Add($"delete:{Path.GetRelativePath(Root.Directory, point["unlink:".Length..])}");

            also?.Invoke(point);
        });

    string? JournalPhase() =>
        LocalStateFiles.ReadJournal(Root) is StateRead<LifecycleJournalEntry>.Present(var entry) ? entry.Phase : null;

    bool JournalAbsent() => LocalStateFiles.ReadJournal(Root) is StateRead<LifecycleJournalEntry>.Absent;

    async Task AssertOrder(params string[] entries) {
        var at = entries.Select(e => _log.IndexOf(e)).ToArray();

        await Assert.That(at.All(i => i >= 0)).IsTrue().Because(string.Join(", ", _log));
        await Assert.That(at.Zip(at.Skip(1)).All(p => p.First < p.Second)).IsTrue().Because(string.Join(", ", _log));
    }

    [Test]
    public async Task Install_runs_the_contract_order() {
        LocalInstallEvent.Committed committed;

        using (Record()) committed = await Install();

        await Assert.That(_log).IsEquivalentTo([
            "journal:pending",
            "journal:unit",
            "install",
            "GET /healthz/ready",
            "GET /auth/config",
            "journal:committing",
            "registry",
            "profile",
            "journal:allowed",
            "GET /auth/config",
            "journal:cleared",
        ], CollectionOrdering.Matching);

        await Assert.That(committed.ServerUrl).IsEqualTo($"http://localhost:{committed.Port}");
        await Assert.That(committed.Version).IsEqualTo(Version);
        await Assert.That(committed.InstallationId).Matches("^[0-9a-f]{32}$");

        var registry = LocalStateFiles.ReadRegistry(Root) as StateRead<InstallRegistry>.Present;
        await Assert.That(registry?.Value.Active).IsEqualTo(new LocalInstallation(committed.InstallationId, committed.Port, committed.ServerUrl, Version));

        var profile = ConfigMutator.LoadPure(ConfigPath).Profiles["default"];
        await Assert.That(profile.ServerUrl).IsEqualTo(committed.ServerUrl);
        await Assert.That(profile.AuthProvider).IsEqualTo(new AuthProviderStamp(AuthProvider.None, committed.ServerUrl));

        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(File.Exists(Path.Combine(Bin, "current", "kcap-server"))).IsTrue();
        await Assert.That(Directory.EnumerateDirectories(Bin, ".staging-*")).IsEmpty();

        await Assert.That(_events.Reports.Where(e => e is not LocalInstallEvent.DownloadProgress).Select(e => e.GetType().Name)).IsEquivalentTo([
            nameof(LocalInstallEvent.PreflightPassed),
            nameof(LocalInstallEvent.PortChosen),
            nameof(LocalInstallEvent.Downloading),
            nameof(LocalInstallEvent.Verified),
            nameof(LocalInstallEvent.Unpacked),
            nameof(LocalInstallEvent.Starting),
            nameof(LocalInstallEvent.Ready),
            nameof(LocalInstallEvent.Committing),
            nameof(LocalInstallEvent.Committed),
        ], CollectionOrdering.Matching);
        await Assert.That(_events.Reports[^1]).IsEqualTo(committed);
    }

    [Test]
    public async Task Nothing_but_ready_and_auth_config_reaches_the_server_before_commit() {
        using (Record()) await Install();

        var beforeCommit = _log.TakeWhile(e => e != "journal:committing").Where(e => e.StartsWith("GET ", StringComparison.Ordinal)).ToList();

        await Assert.That(_log).Contains("journal:committing");
        await Assert.That(beforeCommit).IsNotEmpty();
        await Assert.That(beforeCommit.All(e => e is "GET /healthz/ready" or "GET /auth/config")).IsTrue().Because(string.Join(", ", beforeCommit));
    }

    [Test]
    public async Task A_manifest_404_rolls_back_without_touching_LaunchAgents() {
        Bundle.ManifestMissing = true;

        var failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.BundleNotPublished);
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(Directory.Exists(Bin)).IsFalse();
        await Assert.That(Directory.Exists(LaunchAgents)).IsFalse();
        await Assert.That(_events.Reports[^1]).IsEqualTo(new LocalInstallEvent.RolledBack());
    }

    [Test]
    public async Task Not_our_server_rolls_back() {
        Server.WhileFenced = FakeAuthConfig.Lifted;

        LocalInstallFailure failure;
        using (Record()) failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await AssertOrder("uninstall", "delete:bin", "journal:cleared");
        await Assert.That(Directory.Exists(Bin)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(LaunchAgents, LocalServerUnit.ProductionLabel + ".plist"))).IsFalse();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.RollingBack>()).IsNotEmpty();
        await Assert.That(_events.Reports[^1]).IsEqualTo(new LocalInstallEvent.RolledBack());
    }

    [Test]
    public async Task Cancel_during_readiness_unloads_before_deleting() {
        using var cts = new CancellationTokenSource();
        Server.Ready     = HttpStatusCode.ServiceUnavailable;
        Server.OnRequest = path => { if (path == "/healthz/ready") cts.Cancel(); };

        LocalInstallFailure failure;
        using (Record()) failure = await Failure(ct: cts.Token);

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.Cancelled);
        await AssertOrder("uninstall", "delete:bin", "journal:cleared");
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_failed_unload_keeps_the_unit_journal_and_deletes_nothing() {
        Server.WhileFenced       = FakeAuthConfig.Lifted;
        Service.UninstallFailure = new LocalLifecycleException("the label is still loaded");

        var failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Unit);
        await Assert.That(Directory.Exists(Bin)).IsTrue();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.RolledBack>()).IsEmpty();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.RollingBack>()).IsEmpty();
    }

    [Test]
    public async Task A_foreign_unit_is_left_untouched_on_rollback() {
        Server.WhileFenced = FakeAuthConfig.Lifted;
        Tmp.CreateDir("other");
        var other = new OwnerReading.Root(Tmp.GetResolvedPath("other"));

        LocalInstallFailure failure;
        using (Record(p => { if (p == "rollback:unit") Service.OwnerOverride = new(other, other); }))
            failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await Assert.That(_log).DoesNotContain("uninstall");
        await Assert.That(Directory.Exists(Bin)).IsFalse();
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task An_unknown_owner_deletes_nothing() {
        Server.WhileFenced = FakeAuthConfig.Lifted;

        LocalInstallFailure failure;
        using (Record(p => {
                   if (p == "rollback:unit")
                       Service.OwnerOverride = new(new OwnerReading.Unreadable("launchctl failed"), OwnerReading.Absent.Instance);
               }))
            failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(_log).DoesNotContain("uninstall");
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Unit);
        await Assert.That(Directory.Exists(Bin)).IsTrue();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.RollingBack>()).IsEmpty();
    }

    [Test]
    public async Task A_cancel_after_the_point_of_no_return_still_commits() {
        using var cts = new CancellationTokenSource();

        using (Record(p => { if (p == "setup:after-committing") cts.Cancel(); }))
            await Install(ct: cts.Token);

        await Assert.That(cts.IsCancellationRequested).IsTrue();
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(LocalStateFiles.ReadRegistry(Root) is StateRead<InstallRegistry>.Present { Value.Active: not null }).IsTrue();
    }

    [Test]
    public async Task A_throwing_reporter_after_the_journal_is_cleared_still_commits() {
        var progress = new SyncProgress<LocalInstallEvent>(e => {
            _events.Report(e);
            if (e is LocalInstallEvent.Committed) throw new IOException("stdout is closed");
        });

        var committed = await Installer().InstallAsync(Yes, progress, CancellationToken.None);

        await Assert.That(committed.Version).IsEqualTo(Version);
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task Lifted_on_a_later_poll_needs_no_restart() {
        Server.WhileAllowed = FakeAuthConfig.Pending;

        // The answer is chosen after OnRequest runs, so the switch on the second poll makes that poll the first lifted
        // one. Between them the clock moves exactly past the poll gap, once that delay is registered.
        var polls = 0;
        Server.OnRequest = path => {
            if (path != "/auth/config" || JournalPhase() != LocalSetupJournal.Allowed) return;

            if (++polls == 1)
                _time.OnTimerCreated = due => {
                    _time.OnTimerCreated = null;
                    _ = Task.Run(() => _time.Advance(due));
                };
            else
                Server.WhileAllowed = FakeAuthConfig.Lifted;
        };

        using (Record()) await Install();

        await Assert.That(JournalAbsent()).IsTrue();

        var afterAllowed = _log.SkipWhile(e => e != "journal:allowed").ToList();
        await Assert.That(afterAllowed).IsEquivalentTo([
            "journal:allowed", "GET /auth/config", "GET /auth/config", "journal:cleared",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_staged_tree_with_an_escaping_link_is_deleted_without_following_it() {
        var outside = Tmp.CreateFile("outside/keep.txt", "keep");
        using var escaping = new FakeBundle(Tmp.PathTo("escaping.tar.gz"), Version, "osx-arm64",
            TestBundles.RegularFile("./kcap-server", "#!/bin/sh\n"),
            // From bin/.staging-<id>/tree up past the home to the temp directory.
            TestBundles.Symlink("./escape", "../../../../../../../outside"));

        // Otherwise the test proves nothing: the refused unpack must leave the link for the rollback to meet.
        var linkAtDelete = false;

        LocalInstallFailure failure;
        using (Record(p => {
                   if (p == "rollback:delete")
                       linkAtDelete = Directory.EnumerateDirectories(Bin, ".staging-*")
                           .Any(s => File.Exists(Path.Combine(s, "tree", "escape", "keep.txt")));
               }))
            failure = await Failure(bundle: escaping);

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.VerificationFailed);
        await Assert.That(linkAtDelete).IsTrue();
        await Assert.That(Directory.Exists(Bin)).IsFalse();
        await Assert.That(File.ReadAllText(outside)).IsEqualTo("keep");
    }

    [Test]
    [Arguments("rename:", LocalSetupJournal.Unit)]
    [Arguments("renamed:", LocalSetupJournal.Committing)]
    [Arguments("sync:", LocalSetupJournal.Committing)]
    public async Task A_throw_at_the_committing_write_deletes_nothing(string point, string onDisk) {
        var target = point == "sync:" ? $"sync:{Root.Directory}" : $"{point}{Root.JournalPath}";
        var armed  = false;

        LocalInstallFailure failure;
        using (Record(p => {
                   if (p == "setup:before-committing") armed = true;
                   else if (armed && p == target) {
                       armed = false;
                       throw new SimulatedCrash();
                   }
               }))
            failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.CommitFailed);
        await Assert.That(JournalPhase()).IsEqualTo(onDisk);
        await Assert.That(Directory.Exists(Bin)).IsTrue();
        await Assert.That(_log).DoesNotContain("uninstall");
    }

    [Test]
    public async Task A_conflicting_profile_at_commit_keeps_committing() {
        const string conflicting = """{"version":2,"active_profile":"default","profiles":{"default":{},"cloud":{"server_url":"https://cloud.example"}}}""";

        LocalInstallFailure failure;
        using (Record(p => { if (p == "setup:after-registry") File.WriteAllText(ConfigPath, conflicting); }))
            failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.CommitFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Committing);
        await Assert.That(File.ReadAllText(ConfigPath)).IsEqualTo(conflicting);
    }

    [Test]
    public async Task A_profile_flush_failure_never_writes_allowed() {
        LocalInstallFailure failure;
        using (Record(p => { if (p == $"sync:{Config.Directory}") throw new SimulatedCrash(); }))
            failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.CommitFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Committing);
        await Assert.That(_log).DoesNotContain("journal:allowed");
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Answers_are_required_for_an_install(bool singleUser, bool confirmed) {
        var failure = await Failure(new(singleUser, confirmed));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.AnswerRequired);
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(Directory.Exists(Bin)).IsFalse();
        await Assert.That(_log).IsEmpty();
        await Assert.That(Bundle.Requests).IsEmpty();
    }

    /// <summary>Each lift probe after <c>allowed</c> spends the whole ten-second poll, so the test never sleeps.</summary>
    void LiftProbesExhaustThePoll() =>
        Server.OnRequest = path => {
            if (path == "/auth/config" && JournalPhase() == LocalSetupJournal.Allowed) _time.Advance(TimeSpan.FromSeconds(11));
        };

    [Test]
    [Arguments(FakeAuthConfig.Forbidden)]
    [Arguments(FakeAuthConfig.Pending)]
    public async Task A_4xx_or_pending_lift_restarts_then_requires_proof_again(FakeAuthConfig afterAllow) {
        Server.WhileAllowed = afterAllow;
        LiftProbesExhaustThePoll();

        LocalInstallFailure failure;
        using (Record()) failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.CommitFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Allowed);

        var afterAllowed = _log.SkipWhile(e => e != "journal:allowed").ToList();
        await Assert.That(afterAllowed).IsEquivalentTo([
            "journal:allowed", "GET /auth/config", "stop", "start", "GET /healthz/ready", "GET /auth/config",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_unit_not_provably_ours_is_never_restarted() {
        Server.WhileAllowed = FakeAuthConfig.Forbidden;
        LiftProbesExhaustThePoll();
        Tmp.CreateDir("other");
        var other = new OwnerReading.Root(Tmp.GetResolvedPath("other"));

        LocalInstallFailure failure;
        using (Record(p => { if (p == "setup:after-allowed") Service.OwnerOverride = new(other, other); }))
            failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.RecoveryFailed);
        await Assert.That(JournalPhase()).IsEqualTo(LocalSetupJournal.Allowed);
        await Assert.That(_log).DoesNotContain("stop");
        await Assert.That(_log).DoesNotContain("start");
    }

    [Test]
    public async Task A_lift_proven_after_restart_completes() {
        Server.WhileAllowed = FakeAuthConfig.Forbidden;
        Service.OnStart     = () => Server.WhileAllowed = FakeAuthConfig.Lifted;
        LiftProbesExhaustThePoll();

        using (Record()) await Install();

        await Assert.That(JournalAbsent()).IsTrue();

        var afterAllowed = _log.SkipWhile(e => e != "journal:allowed").ToList();
        await Assert.That(afterAllowed).IsEquivalentTo([
            "journal:allowed", "GET /auth/config", "stop", "start", "GET /healthz/ready", "GET /auth/config", "journal:cleared",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reuse_downloads_nothing_and_asks_nothing() {
        var first = await Install();

        Bundle.Requests.Clear();
        _events.Reports.Clear();
        _log.Clear();

        var second = await Install(new(SingleUser: false, Confirmed: false));

        await Assert.That(Bundle.Requests).IsEmpty();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(_log).Contains("start");
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(_events.Reports.Select(e => e.GetType().Name)).IsEquivalentTo([
            nameof(LocalInstallEvent.PreflightPassed),
            nameof(LocalInstallEvent.Starting),
            nameof(LocalInstallEvent.Ready),
            nameof(LocalInstallEvent.Reused),
            nameof(LocalInstallEvent.Committed),
        ], CollectionOrdering.Matching);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.Reused>().Single()).IsEqualTo(new LocalInstallEvent.Reused(first.ServerUrl, Version));
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles["default"].ServerUrl).IsEqualTo(first.ServerUrl);
    }

    [Test]
    public async Task Reuse_reinstalls_a_missing_unit_file_of_its_own_installation() {
        await Install();
        File.Delete(Path.Combine(LaunchAgents, LocalServerUnit.ProductionLabel + ".plist"));
        _log.Clear();

        await Install(new(false, false));

        await Assert.That(_log).Contains("install");
        await Assert.That(File.Exists(Path.Combine(LaunchAgents, LocalServerUnit.ProductionLabel + ".plist"))).IsTrue();
    }

    [Test]
    public async Task Reuse_never_starts_or_publishes_over_a_foreign_unit() {
        await Install();
        Tmp.CreateDir("other");
        var other = new OwnerReading.Root(Tmp.GetResolvedPath("other"));
        Service.OwnerOverride = new(other, other);
        _log.Clear();

        var before = File.ReadAllBytes(ConfigPath);

        var failure = await Failure(new(false, false));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await Assert.That(_log).DoesNotContain("start");
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(File.ReadAllBytes(ConfigPath).SequenceEqual(before)).IsTrue();
    }

    [Test]
    [Arguments("foreign")]
    [Arguments("redirect")]
    [Arguments("malformed")]
    [Arguments("pending")]
    public async Task Reuse_without_lift_proof_never_touches_the_profile(string answer) {
        await Install();
        File.WriteAllText(ConfigPath, File.ReadAllText(ConfigPath).Replace("\"default\":{", "\"default\":{\"note\":1,", StringComparison.Ordinal));

        var before = File.ReadAllBytes(ConfigPath);

        Server.Override = path => path != "/auth/config" ? null : answer switch {
            "foreign"   => new(HttpStatusCode.OK) { Content = new StringContent("""{"provider":"WorkOS"}""") },
            "redirect"  => new(HttpStatusCode.Found) { Headers = { Location = new("http://elsewhere.test/") } },
            "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("{nope") },
            _           => PendingAnswer(),
        };

        var failure = await Failure(new(false, false));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await Assert.That(File.ReadAllBytes(ConfigPath).SequenceEqual(before)).IsTrue();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.Reused>()).IsEmpty();
    }

    static HttpResponseMessage PendingAnswer() {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"provider":"None"}""") };
        response.Headers.Add(LocalServerProbe.PendingHeader, "pending");
        return response;
    }

    [Test]
    public async Task Reuse_reports_a_version_difference() {
        var first = await Install();

        _events.Reports.Clear();

        var other = new LocalInstaller(Root, Config, Home, Service, Bundle.Client(), new HttpClient(Server), Bundle.Key, MacArm, _time, _ => []) {
            Version     = "9.9.9",
            SocketLimit = int.MaxValue,
        };

        var committed = await other.InstallAsync(new(false, false), _events, CancellationToken.None);

        await Assert.That(_events.Reports.OfType<LocalInstallEvent.Reused>().Single().InstalledVersion).IsEqualTo(Version);
        await Assert.That(committed).IsEqualTo(first);
    }

    [Test]
    public async Task A_cancel_before_the_reuse_publish_changes_nothing() {
        await Install();

        var before = File.ReadAllBytes(ConfigPath);

        using var cts = new CancellationTokenSource();
        Server.OnRequest = path => { if (path == "/auth/config") cts.Cancel(); };

        var failure = await Failure(new(false, false), cts.Token);

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.Cancelled);
        await Assert.That(File.ReadAllBytes(ConfigPath).SequenceEqual(before)).IsTrue();
    }
}
