using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

// The local edition runs on macOS and Linux; its unit, modes and symlinks have no Windows counterpart yet.
[ExcludeOn(OS.Windows)]
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

    LocalInstaller Installer(FakeBundle? bundle = null, FakeOllama? ollama = null) =>
        new(Root, Config, Home, Service, (bundle ?? Bundle).Client(), new HttpClient(Server), (bundle ?? Bundle).Key, MacArm, _time, _ => []) {
            Version     = Version,
            SocketLimit = int.MaxValue,
            Ollama      = ollama?.Client(),
        };

    static readonly LocalInstallAnswers Yes = new(SingleUser: true, Confirmed: true);

    Task<LocalInstallEvent.Committed> Install(LocalInstallAnswers? answers = null, CancellationToken ct = default, FakeBundle? bundle = null,
        FakeOllama? ollama = null) =>
        Installer(bundle, ollama).InstallAsync(answers ?? Yes, _events, ct);

    static readonly LocalInstallAnswers YesWithOllama = Yes with { Embeddings = new LocalEmbeddingAnswer.Ollama(null) };

    static readonly LocalInstallAnswers ReuseWithOllama = new(false, false, new LocalEmbeddingAnswer.Ollama(null));

    static readonly byte[] ModelOnnx = RandomNumberGenerator.GetBytes(2_000);

    /// <summary>A publication of the installed version that also serves a built-in model set.</summary>
    FakeBundle Models => field ??= new(Tmp.PathTo("models.tar.gz"), Version) {
        ModelFiles = new Dictionary<string, byte[]> { ["model.onnx"] = ModelOnnx },
    };

    string ModelFile => Path.Combine(Root.Directory, "models", Models.ModelSet, "model.onnx");

    /// <summary>A reuse that answers the embeddings question, offering the built-in model set the way setup does.</summary>
    async Task ReuseOffering(LocalEmbeddingAnswer? answer, FakeOllama? ollama = null) {
        using var absent   = new FakeOllama();
        var       manifest = await Installer(Models).FetchEmbeddingManifestAsync(Version, CancellationToken.None);

        await Install(new(false, false, answer, manifest), bundle: Models, ollama: ollama ?? absent);
    }

    KeyValuePair<string, string>[] EmbeddingEnvironment() =>
        [.. Service.Installed!.Environment.Where(e => e.Key.StartsWith("Embedding__", StringComparison.Ordinal))];

    static readonly KeyValuePair<string, string>[] BuiltinEnvironment = [new("Embedding__Provider", "builtin")];

    int EventAt<T>() where T : LocalInstallEvent => _events.Reports.FindIndex(e => e is T);

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
    public async Task A_failure_after_a_cancel_reports_cancelled() {
        using var cts = new CancellationTokenSource();
        Service.InstallFailure = new InvalidOperationException("launchctl bootstrap failed");

        LocalInstallFailure failure;
        using (Record(p => { if (p == $"renamed:{Root.JournalPath}" && JournalPhase() == LocalSetupJournal.Unit) cts.Cancel(); }))
            failure = await Failure(ct: cts.Token);

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.Cancelled);
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

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.CommitFailed);
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
    public async Task An_install_without_ollama_commits_without_a_model_download() {
        using var ollama = new FakeOllama();
        var committed = await Install(YesWithOllama, ollama: ollama);

        await Assert.That(committed).IsNotNull();
        await Assert.That(Service.Installed!.Embedding).IsNull();
        await Assert.That(ollama.Requests("/api/pull")).IsEqualTo(0);
        await Assert.That(ollama.Requests("/api/embed")).IsEqualTo(0);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsSkipped>().Single().Code)
            .IsEqualTo(LocalEmbeddingSkipCode.OllamaNotFound);
        await Assert.That(EventAt<LocalInstallEvent.Downloading>()).IsLessThan(EventAt<LocalInstallEvent.EmbeddingsSkipped>());
        await Assert.That(EventAt<LocalInstallEvent.EmbeddingsSkipped>()).IsLessThan(EventAt<LocalInstallEvent.Committed>());
    }

    [Test]
    public async Task An_install_records_the_embedding_model_before_the_unit_first_starts() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);

        await Install(YesWithOllama, ollama: ollama);

        await Assert.That(Service.Installed!.Embedding?.Model).IsEqualTo(OllamaClient.DefaultModel);
        await Assert.That(_log.Count(e => e == "install")).IsEqualTo(1);
        await Assert.That(EventAt<LocalInstallEvent.Unpacked>()).IsLessThan(EventAt<LocalInstallEvent.EmbeddingsConfigured>());
        await Assert.That(EventAt<LocalInstallEvent.EmbeddingsConfigured>()).IsLessThan(EventAt<LocalInstallEvent.Starting>());
    }

    [Test]
    public async Task A_unit_that_fails_to_install_reports_no_configured_model() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        Service.InstallFailure = new LocalLifecycleException("launchctl bootstrap failed");

        await Assert.ThrowsAsync<LocalInstallFailure>(() => Install(YesWithOllama, ollama: ollama));

        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingModelReady>()).IsNotEmpty();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsConfigured>()).IsEmpty();
    }

    [Test]
    public async Task A_rolled_back_install_takes_its_embedding_model_with_it() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        Server.WhileFenced = FakeAuthConfig.Lifted;

        await Assert.ThrowsAsync<LocalInstallFailure>(() => Install(YesWithOllama, ollama: ollama));

        await Assert.That(File.Exists(LocalEmbeddingFile.PathFor(Root))).IsFalse();
    }

    [Test]
    public async Task Reuse_with_the_same_model_starts_the_unit_as_it_is() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        await Install(YesWithOllama, ollama: ollama);
        _log.Clear();

        await Install(ReuseWithOllama, ollama: ollama);

        await Assert.That(_log).Contains("start");
        await Assert.That(_log).DoesNotContain("install");
    }

    [Test]
    public async Task Reuse_with_a_changed_model_rewrites_the_unit_so_the_server_restarts_with_it() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        await Install();
        _log.Clear();
        _events.Reports.Clear();

        await Install(ReuseWithOllama, ollama: ollama);

        await Assert.That(_log).Contains("install");
        await Assert.That(_log).DoesNotContain("start");
        await Assert.That(Service.Installed!.Embedding?.Model).IsEqualTo(OllamaClient.DefaultModel);
        await Assert.That(EventAt<LocalInstallEvent.EmbeddingsConfigured>()).IsLessThan(EventAt<LocalInstallEvent.Starting>());
    }

    [Test]
    public async Task A_failed_rewrite_leaves_the_setting_pending_so_the_next_setup_rewrites_again() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        await Install();
        Service.InstallFailure = new LocalLifecycleException("systemctl restart failed");

        await Assert.ThrowsAsync<LocalInstallFailure>(() => Install(ReuseWithOllama, ollama: ollama));

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Ollama(OllamaClient.DefaultModel) with { Pending = true });

        Service.InstallFailure = null;
        _log.Clear();

        await Install(ReuseWithOllama, ollama: ollama);

        await Assert.That(_log).Contains("install");
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Ollama(OllamaClient.DefaultModel));
    }

    /// <summary>What a setup that stopped between marking the setting and rewriting the unit leaves behind; the
    /// recorded model alone would read as already served.</summary>
    [Test]
    public async Task A_pending_setting_is_applied_by_the_next_setup_even_one_that_decides_nothing() {
        await Install();
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama(OllamaClient.DefaultModel) with { Pending = true });
        _log.Clear();

        await Install(new(false, false));

        await Assert.That(_log).Contains("install");
        await Assert.That(Service.Installed!.Embedding?.Model).IsEqualTo(OllamaClient.DefaultModel);
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Ollama(OllamaClient.DefaultModel));
    }

    [Test]
    public async Task A_setting_that_cannot_be_marked_leaves_the_unit_as_it_was() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        await Install();
        Directory.CreateDirectory(LocalEmbeddingFile.PathFor(Root));
        _log.Clear();

        await Install(ReuseWithOllama, ollama: ollama);

        await Assert.That(_log).Contains("start");
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsSkipped>().Single().Code).IsEqualTo(LocalEmbeddingSkipCode.WriteFailed);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsConfigured>()).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_unrecognised_recorded_provider_is_kept_and_the_unit_is_not_replaced(bool builtin) {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        await Install();
        Directory.CreateDirectory(Path.GetDirectoryName(LocalEmbeddingFile.PathFor(Root))!);
        File.WriteAllText(LocalEmbeddingFile.PathFor(Root), """{"provider":"cohere"}""");
        _log.Clear();

        if (builtin) await ReuseOffering(new LocalEmbeddingAnswer.Builtin(), ollama);
        else await Install(ReuseWithOllama, ollama: ollama);

        await Assert.That(Models.Requests.Where(r => r.StartsWith("/local/models/", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(File.ReadAllText(LocalEmbeddingFile.PathFor(Root))).IsEqualTo("""{"provider":"cohere"}""");
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsSkipped>().Single().Code).IsEqualTo(LocalEmbeddingSkipCode.UnrecognisedProvider);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsConfigured>()).IsEmpty();
    }

    [Test]
    public async Task An_install_choosing_builtin_first_starts_the_unit_with_it() {
        using var absent   = new FakeOllama();
        var       manifest = await Installer(Models).FetchEmbeddingManifestAsync(Version, CancellationToken.None);

        await Install(Yes with { Embeddings = new LocalEmbeddingAnswer.Builtin(), BuiltinModel = manifest }, bundle: Models, ollama: absent);

        await Assert.That(_log.Count(e => e == "install")).IsEqualTo(1);
        await Assert.That(EmbeddingEnvironment()).IsEquivalentTo(BuiltinEnvironment);
        await Assert.That(EventAt<LocalInstallEvent.EmbeddingBuiltinConfigured>()).IsLessThan(EventAt<LocalInstallEvent.Starting>());
    }

    [Test]
    public async Task Choosing_builtin_over_no_recorded_choice_applies_it_to_the_unit() {
        await Install();
        _log.Clear();
        _events.Reports.Clear();

        await ReuseOffering(new LocalEmbeddingAnswer.Builtin());

        await Assert.That(_log).Contains("install");
        await Assert.That(EmbeddingEnvironment()).IsEquivalentTo(BuiltinEnvironment);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingBuiltinConfigured>().Single()).IsEqualTo(new LocalInstallEvent.EmbeddingBuiltinConfigured());
        await Assert.That(EventAt<LocalInstallEvent.EmbeddingBuiltinConfigured>()).IsLessThan(EventAt<LocalInstallEvent.Starting>());
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));
    }

    [Test]
    public async Task Switching_from_ollama_to_builtin_leaves_only_the_builtin_provider_in_the_unit() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        await Install(YesWithOllama, ollama: ollama);
        _log.Clear();
        _events.Reports.Clear();

        await ReuseOffering(new LocalEmbeddingAnswer.Builtin(), ollama);

        await Assert.That(_log).Contains("install");
        await Assert.That(EmbeddingEnvironment()).IsEquivalentTo(BuiltinEnvironment);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingBuiltinConfigured>().Single())
            .IsEqualTo(new LocalInstallEvent.EmbeddingBuiltinConfigured(OllamaClient.DefaultModel));
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));
    }

    /// <summary>An older CLI acts on a top-level pending flag by rewriting the record without the marker, so the
    /// in-progress state of a built-in change lives only inside the marker.</summary>
    [Test]
    public async Task Builtin_rewrite_marks_the_marker_pending_not_the_top_level() {
        await Install();
        JsonNode? during = null;
        Service.OnInstall = () => during = JsonNode.Parse(File.ReadAllText(LocalEmbeddingFile.PathFor(Root)));

        await ReuseOffering(new LocalEmbeddingAnswer.Builtin());

        await Assert.That(during!["provider"]!.GetValue<string>()).IsEqualTo("none");
        await Assert.That(during["pending"]).IsNull();
        await Assert.That(during["builtin"]!["pending"]?.GetValue<bool>()).IsTrue();

        var after = JsonNode.Parse(File.ReadAllText(LocalEmbeddingFile.PathFor(Root)))!;
        await Assert.That(after["pending"]).IsNull();
        await Assert.That(after["builtin"]!["pending"]).IsNull();
    }

    [Test]
    public async Task Failed_rewrite_leaves_the_builtin_marker_pending_so_the_next_setup_rewrites() {
        await Install();
        Service.InstallFailure = new LocalLifecycleException("systemctl restart failed");

        await Assert.ThrowsAsync<LocalInstallFailure>(() => ReuseOffering(new LocalEmbeddingAnswer.Builtin()));

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet) with { Pending = true });

        Service.InstallFailure = null;
        _log.Clear();

        await Install(new(false, false));

        await Assert.That(_log).Contains("install");
        await Assert.That(EmbeddingEnvironment()).IsEquivalentTo(BuiltinEnvironment);
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));
    }

    /// <summary>A recorded built-in choice follows the model set the manifest offers: the set it names is fetched and
    /// the unit rewritten, so the server restarts on it.</summary>
    [Test]
    public async Task A_recorded_builtin_model_set_moves_to_the_one_the_manifest_offers() {
        await Install();
        await ReuseOffering(new LocalEmbeddingAnswer.Builtin());
        var next = new FakeBundle(Tmp.PathTo("next.tar.gz"), Version) {
            ModelFiles = new Dictionary<string, byte[]> { ["model.onnx"] = RandomNumberGenerator.GetBytes(2_000) },
            ModelSet   = new('f', 64),
        };
        using var absent   = new FakeOllama();
        var       manifest = await Installer(next).FetchEmbeddingManifestAsync(Version, CancellationToken.None);
        _log.Clear();
        _events.Reports.Clear();

        await Install(new(false, false, new LocalEmbeddingAnswer.Default(), manifest), bundle: next, ollama: absent);

        await Assert.That(next.Requests).Contains($"/local/models/{next.ModelSet}/model.onnx");
        await Assert.That(_log).Contains("install");
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(next.ModelSet));
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingBuiltinConfigured>().Single()).IsEqualTo(new LocalInstallEvent.EmbeddingBuiltinConfigured());
        await Assert.That(EmbeddingEnvironment()).IsEquivalentTo(BuiltinEnvironment);
    }

    /// <summary>The server does not retry a model load that failed, so files put back only reach it through a
    /// restart; a model set already in place gives it nothing new to load.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Repair_of_an_unchanged_builtin_setting_restarts_the_server_only_when_files_were_fetched(bool damaged) {
        await Install();
        await ReuseOffering(new LocalEmbeddingAnswer.Builtin());
        if (damaged) File.WriteAllBytes(ModelFile, [1, 2, 3]);
        _log.Clear();
        _events.Reports.Clear();

        await ReuseOffering(new LocalEmbeddingAnswer.Default());

        await Assert.That(_log.Contains("install")).IsEqualTo(damaged).Because(string.Join(", ", _log));
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingBuiltinConfigured>().Count()).IsEqualTo(1);
        await Assert.That(_log.Contains("start")).IsEqualTo(!damaged).Because(string.Join(", ", _log));
        await Assert.That(File.ReadAllBytes(ModelFile).SequenceEqual(ModelOnnx)).IsTrue();
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));
        await Assert.That(EmbeddingEnvironment()).IsEquivalentTo(BuiltinEnvironment);
    }

    /// <summary>A setup that stops once the repaired files are in place, before the restart, leaves the marker pending,
    /// so the next setup restarts the server although it finds nothing to fetch. An older CLI reads the marked record as
    /// a settled decline.</summary>
    [Test]
    public async Task A_repair_interrupted_before_the_restart_is_finished_by_the_next_setup() {
        await Install();
        await ReuseOffering(new LocalEmbeddingAnswer.Builtin());
        File.WriteAllBytes(ModelFile, [1, 2, 3]);
        var       models    = Path.Combine(Root.Directory, "models");
        JsonNode? published = null;
        _log.Clear();

        using (FaultPoints.Arm(point => {
                   if (point != $"sync:{models}" || !File.ReadAllBytes(ModelFile).SequenceEqual(ModelOnnx)) return;

                   published = JsonNode.Parse(File.ReadAllText(LocalEmbeddingFile.PathFor(Root)));
                   throw new InvalidOperationException("interrupted");
               }))
            await Assert.ThrowsAsync<LocalInstallFailure>(() => ReuseOffering(new LocalEmbeddingAnswer.Default()));

        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(published!["provider"]!.GetValue<string>()).IsEqualTo("none");
        await Assert.That(published["pending"]).IsNull();
        await Assert.That(published["builtin"]!["pending"]?.GetValue<bool>()).IsTrue();

        await Install(new(false, false));

        await Assert.That(_log).Contains("install");
        await Assert.That(EmbeddingEnvironment()).IsEquivalentTo(BuiltinEnvironment);
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));
    }

    [Test]
    public async Task A_repair_whose_restart_fails_leaves_the_marker_pending() {
        await Install();
        await ReuseOffering(new LocalEmbeddingAnswer.Builtin());
        File.WriteAllBytes(ModelFile, [1, 2, 3]);
        Service.InstallFailure = new LocalLifecycleException("systemctl restart failed");

        await Assert.ThrowsAsync<LocalInstallFailure>(() => ReuseOffering(new LocalEmbeddingAnswer.Default()));

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet) with { Pending = true });
        var left = JsonNode.Parse(File.ReadAllText(LocalEmbeddingFile.PathFor(Root)))!;
        await Assert.That(left["provider"]!.GetValue<string>()).IsEqualTo("none");
        await Assert.That(left["pending"]).IsNull();

        Service.InstallFailure = null;
        _log.Clear();

        await Install(new(false, false));

        await Assert.That(_log).Contains("install");
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));
    }

    /// <summary>Repaired files published without the pending marker would verify on the next setup, which then has no
    /// reason to restart a server still holding its failed load; so the damaged set stays until the marker is written.</summary>
    [Test]
    public async Task A_repair_whose_marker_cannot_be_written_is_not_published_and_the_next_setup_repairs() {
        await Install();
        await ReuseOffering(new LocalEmbeddingAnswer.Builtin());
        File.WriteAllBytes(ModelFile, [1, 2, 3]);
        var record = LocalEmbeddingFile.PathFor(Root);
        _log.Clear();
        _events.Reports.Clear();

        using (FaultPoints.Arm(point => {
                   if (point == $"rename:{record}") throw new IOException("read-only");
               }))
            await ReuseOffering(new LocalEmbeddingAnswer.Default());

        await Assert.That(File.ReadAllBytes(ModelFile)).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsSkipped>().Single().Code).IsEqualTo(LocalEmbeddingSkipCode.WriteFailed);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingBuiltinConfigured>()).IsEmpty();
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));

        await ReuseOffering(new LocalEmbeddingAnswer.Default());

        await Assert.That(File.ReadAllBytes(ModelFile).SequenceEqual(ModelOnnx)).IsTrue();
        await Assert.That(_log).Contains("install");
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Models.ModelSet));
    }

    [Test]
    public async Task Reuse_over_a_foreign_unit_pulls_no_model() {
        using var ollama = new FakeOllama().Running().Pulls("""{"status":"success"}""").Embeds(1024);
        await Install();
        Tmp.CreateDir("other");
        var other = new OwnerReading.Root(Tmp.GetResolvedPath("other"));
        Service.OwnerOverride = new(other, other);

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Install(ReuseWithOllama, ollama: ollama));

        await Assert.That(failure!.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await Assert.That(ollama.Requests("/api/pull")).IsEqualTo(0);
        await Assert.That(ollama.Requests("/api/version")).IsEqualTo(0);
    }

    [Test]
    public async Task Reuse_over_a_foreign_unit_leaves_the_embedding_setting_alone() {
        using var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024);
        await Install(YesWithOllama, ollama: ollama);
        Tmp.CreateDir("other");
        var other = new OwnerReading.Root(Tmp.GetResolvedPath("other"));
        Service.OwnerOverride = new(other, other);

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Install(new(false, false, new LocalEmbeddingAnswer.Skip()), ollama: ollama));

        await Assert.That(failure!.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Ollama(OllamaClient.DefaultModel));
    }

    [Test]
    public async Task Reuse_without_ollama_keeps_the_configured_model_and_the_unit() {
        using (var ollama = new FakeOllama().Running($"{OllamaClient.DefaultModel}:latest").Embeds(1024))
            await Install(YesWithOllama, ollama: ollama);
        _log.Clear();

        using var gone = new FakeOllama();
        await Install(ReuseWithOllama, ollama: gone);

        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(LocalEmbeddingFile.Read(Root)?.Model).IsEqualTo(OllamaClient.DefaultModel);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsSkipped>().Single().Code).IsEqualTo(LocalEmbeddingSkipCode.OllamaNotFound);
    }

    [Test]
    public async Task Reuse_commits_over_loopback_aliases_and_rewrites_the_target_to_canonical() {
        var first = await Install();
        var port  = first.Port;

        File.WriteAllText(ConfigPath,
            "{\"version\":2,\"active_profile\":\"default\",\"profiles\":{" +
            $"\"default\":{{\"server_url\":\"http://127.0.0.1:{port}\"}}," +
            $"\"other\":{{\"server_url\":\"http://[::1]:{port}\"}}}}}}");

        var second = await Install(new(false, false));

        var profiles = ConfigMutator.LoadPure(ConfigPath).Profiles;

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(profiles["default"].ServerUrl).IsEqualTo(first.ServerUrl);
        await Assert.That(profiles["other"].ServerUrl).IsEqualTo($"http://[::1]:{port}");
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
        File.WriteAllText(ConfigPath, File.ReadAllText(ConfigPath).Replace("\"default\":{", "\"default\":{\"note\":1,", StringComparison.Ordinal));

        var before = File.ReadAllBytes(ConfigPath);

        var failure = await Failure(new(false, false));

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.NotOurServer);
        await Assert.That(_log).DoesNotContain("start");
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(File.ReadAllBytes(ConfigPath).SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task Reuse_never_starts_or_publishes_over_a_unit_of_unknown_owner() {
        await Install();
        Service.OwnerOverride = new(new OwnerReading.Unreadable("launchctl failed"), OwnerReading.Absent.Instance);
        _log.Clear();
        File.WriteAllText(ConfigPath, File.ReadAllText(ConfigPath).Replace("\"default\":{", "\"default\":{\"note\":1,", StringComparison.Ordinal));

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
        File.WriteAllText(ConfigPath, File.ReadAllText(ConfigPath).Replace("\"default\":{", "\"default\":{\"note\":1,", StringComparison.Ordinal));

        var before = File.ReadAllBytes(ConfigPath);

        using var cts = new CancellationTokenSource();
        Server.OnRequest = path => { if (path == "/auth/config") cts.Cancel(); };

        var failure = await Failure(new(false, false), cts.Token);

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.Cancelled);
        await Assert.That(File.ReadAllBytes(ConfigPath).SequenceEqual(before)).IsTrue();
    }

    /// <summary>The desktop wizard tells another kcap at work from a failure by this code, so a held lifecycle lock must
    /// end in it, not in a start failure.</summary>
    [Test]
    public async Task A_lifecycle_lock_held_past_the_wait_fails_as_lifecycle_busy() {
        using var held = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        _time.OnTimerCreated = _ => Task.Run(() => _time.Advance(TimeSpan.FromSeconds(11)));

        var failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.LifecycleBusy);
        await Assert.That(Capacitor.Cli.Commands.LocalSetupJsonLines.Failed(failure.Code, failure.Message)).Contains("\"code\":\"lifecycle_busy\"");
        await Assert.That(_log).DoesNotContain("install");
    }

    /// <summary>The unit is per user, so another root's setup holding its lock is the same busy, not a failure.</summary>
    [Test]
    public async Task A_unit_lock_another_root_holds_fails_as_lifecycle_busy() {
        using var held = await LocalUnitLock.AcquireAsync(Home, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        var installer = new LocalInstaller(Root, Config, Home, Service, Bundle.Client(), new HttpClient(Server), Bundle.Key, MacArm, _time, _ => []) {
            Version      = Version,
            SocketLimit  = int.MaxValue,
            UnitLockWait = TimeSpan.Zero,
        };

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => installer.InstallAsync(Yes, _events, CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(LocalInstallFailureCode.LifecycleBusy);
        await Assert.That(_log).DoesNotContain("install");
        await Assert.That(LocalStateFiles.ReadJournal(Root)).IsTypeOf<StateRead<LifecycleJournalEntry>.Absent>();
    }

    [Test]
    public async Task The_embedding_manifest_comes_from_the_signed_bundle_manifest_and_a_bad_version_is_refused() {
        var set     = new string('c', 64);
        using var offered = new FakeBundle(Tmp.PathTo("offered.tar.gz"), Version) {
            EmbeddingModel = new JsonObject {
                ["model_id"]  = "mxbai-embed-large-v1",
                ["contract"]  = 1,
                ["model_set"] = set,
                ["files"]     = new JsonArray(new JsonObject { ["name"] = "model.onnx", ["sha256"] = new string('d', 64), ["size"] = 42 }),
            },
        };

        var model = await Installer(offered).FetchEmbeddingManifestAsync(Version, CancellationToken.None);

        await Assert.That(model?.ModelSet).IsEqualTo(set);
        await Assert.That(model?.TotalSize).IsEqualTo(42L);
        await Assert.That(await Installer().FetchEmbeddingManifestAsync(Version, CancellationToken.None)).IsNull();

        var refused = await Assert.ThrowsAsync<LocalInstallFailure>(() => Installer(offered).FetchEmbeddingManifestAsync("not a version", CancellationToken.None));
        await Assert.That(refused!.Code).IsEqualTo(LocalInstallFailureCode.InvalidArguments);
    }
}
