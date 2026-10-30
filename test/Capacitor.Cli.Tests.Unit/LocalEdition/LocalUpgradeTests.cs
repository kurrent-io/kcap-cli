using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

[ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
public class LocalUpgradeTests {
    const string Old = "1.2.3";
    const string New = "1.3.0";
    const int    Port = 47391;
    const string Id   = "0123456789abcdef0123456789abcdef";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];
    readonly RecordingProgress<LocalInstallEvent> _events = new();

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("r"), Home);

    string Bin => LocalBin.Directory(Root);

    FakeLocalServerService Service => field ??= new(_log, Home);

    FakeLocalServer Server => field ??= new(_log, Root);

    readonly Dictionary<string, FakeBundle> _bundles = [];

    FakeBundle Bundle(string version) =>
        _bundles.TryGetValue(version, out var bundle) ? bundle : _bundles[version] = new(Tmp.PathTo($"bundle-{version}.tar.gz"), version);

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    LocalStack Stack() =>
        new(Root, Home, Service, new HttpClient(Server), TimeProvider.System, new FakeOutgoingRecorders([]), new ConfigRoot(Tmp.PathTo("cfg"))) {
            FindHolder = _ => null,
        };

    LocalBundleStager Stager(string version) => new(Root, Bundle(version).Client(), Bundle(version).Key, MacArm);

    /// <summary>An installation at <paramref name="version"/>, its unit installed and running.</summary>
    void Installed(string version = Old) {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty.WithActive(new(Id, Port, $"http://localhost:{Port}", version)));

        Tmp.CreateFile(["r", "bin", version, "kcap-server"]);
        LocalBin.Switch(Root, version);

        Service.Install(LocalServerUnit.ForInstallation(Root, Port));
        _log.Clear();
    }

    Task<LocalInstallEvent> Upgrade(string version = New, CancellationToken ct = default, IProgress<LocalInstallEvent>? progress = null) =>
        new LocalUpgrade(Stack(), progress ?? _events).RunAsync(() => Stager(version), version, ct);

    static readonly string OldSet = new('d', 64);

    static readonly Dictionary<string, byte[]> ModelFiles = new() {
        ["model.onnx"] = Encoding.UTF8.GetBytes("onnx weights"),
        ["vocab.txt"]  = Encoding.UTF8.GetBytes("vocabulary"),
    };

    string Models => Tmp.PathTo(["r", "models"]);

    /// <summary><see cref="New"/>'s bundle names a built-in model set and serves <paramref name="files"/> under it.</summary>
    FakeBundle PublishModelSet(Dictionary<string, byte[]>? files = null, JsonObject? entry = null) =>
        _bundles[New] = new(Tmp.PathTo($"bundle-{New}.tar.gz"), New) { ModelFiles = files ?? ModelFiles, EmbeddingModel = entry };

    void Recorded(LocalEmbeddingSetting setting) => LocalEmbeddingFile.Write(Root, setting);

    bool HasModelSet(string set) => Directory.Exists(Path.Combine(Models, set));

    static bool RequestedModels(FakeBundle bundle) => bundle.Requests.Any(r => r.StartsWith("/local/models/", StringComparison.Ordinal));

    async Task<LocalUpgradeFailure> Failure(string version = New) {
        try {
            await Upgrade(version);
        } catch (LocalUpgradeFailure failure) {
            return failure;
        }

        throw new InvalidOperationException("The upgrade succeeded.");
    }

    IDisposable Record(Action<string>? also = null) =>
        FaultPoints.Arm(point => {
            if (point == $"renamed:{Root.JournalPath}") _log.Add($"journal:{JournalPhase()}");
            else if (point == $"unlink:{Root.JournalPath}") _log.Add("journal:cleared");
            else if (point == $"renamed:{Root.RegistryPath}") _log.Add($"registry:{RegistryVersion()}");

            also?.Invoke(point);
        });

    static IDisposable CrashAt(string at) => FaultPoints.Arm(point => { if (point == at) throw new SimulatedCrash(); });

    string? JournalPhase() =>
        LocalStateFiles.ReadJournal(Root) is StateRead<LifecycleJournalEntry>.Present(var entry) ? entry.Phase : null;

    bool JournalAbsent() => LocalStateFiles.ReadJournal(Root) is StateRead<LifecycleJournalEntry>.Absent;

    string? RegistryVersion() =>
        LocalStateFiles.ReadRegistry(Root) is StateRead<InstallRegistry>.Present { Value.Active: { } active } ? active.Version : null;

    bool Staged(string version) => Directory.Exists(Tmp.PathTo(["r", "bin", version]));

    [Test]
    public async Task An_upgrade_stages_stops_switches_starts_and_commits_in_that_order() {
        Installed();

        LocalInstallEvent result;
        using (Record()) result = await Upgrade();

        await Assert.That(_log).IsEquivalentTo([
            "journal:staging",
            "journal:stopping",
            "stop",
            "journal:switching",
            $"registry:{New}",
            "journal:starting",
            "install",
            "GET /healthz/ready",
            "GET /auth/config",
            "journal:cleared",
        ], CollectionOrdering.Matching);

        await Assert.That(result).IsEqualTo(new LocalInstallEvent.Upgraded($"http://localhost:{Port}", Old, New));
        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(New);
        await Assert.That(Staged(Old)).IsFalse();
        await Assert.That(Directory.EnumerateDirectories(Bin, ".staging-*")).IsEmpty();
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    [Arguments(Old, "current")]
    [Arguments("1.2.0", "stack_newer")]
    public async Task A_caller_at_or_below_the_stack_changes_nothing(string caller, string reason) {
        Installed();

        var result = await Upgrade(caller);

        await Assert.That(result is LocalInstallEvent.NothingToDo n && n.Reason.Wire() == reason && n.InstalledVersion == Old).IsTrue();
        await Assert.That(_log).IsEmpty();
        await Assert.That(Bundle(caller).Requests).IsEmpty();
        await Assert.That(RegistryVersion()).IsEqualTo(Old);
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task Without_an_installation_there_is_nothing_to_do_and_no_root_is_created() {
        var result = await Upgrade();

        await Assert.That(result).IsEqualTo(new LocalInstallEvent.NothingToDo(UpgradeSkip.NotInstalled, New, null));
        await Assert.That(Bundle(New).Requests).IsEmpty();
        await Assert.That(Directory.Exists(Root.Directory)).IsFalse();
    }

    [Test]
    public async Task A_stopped_stack_is_migrated_and_stopped_again() {
        Installed();
        await Service.StopAsync(LocalServerUnit.ForInstallation(Root, Port), CancellationToken.None);
        _log.Clear();

        LocalInstallEvent result;
        using (Record()) result = await Upgrade();

        await Assert.That(result).IsTypeOf<LocalInstallEvent.Upgraded>();
        var stopAfterReady = _log.IndexOf("stop", _log.IndexOf("GET /auth/config"));
        await Assert.That(stopAfterReady).IsGreaterThanOrEqualTo(0);
        await Assert.That(stopAfterReady).IsLessThan(_log.IndexOf("journal:cleared"));
        await Assert.That(Service.Running).IsFalse();
        await Assert.That(RegistryVersion()).IsEqualTo(New);
    }

    [Test]
    public async Task A_unit_whose_state_cannot_be_read_is_not_upgraded() {
        Installed();
        Service.ActivityOverride = new UnitActivity.Unknown("launchctl did not answer");

        var failure = await Failure();

        await Assert.That(failure.Switched).IsFalse();
        await Assert.That(_log).IsEmpty();
        await Assert.That(Bundle(New).Requests).IsEmpty();
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_new_version_that_neither_starts_nor_stops_keeps_the_journal() {
        Installed();

        LocalUpgradeFailure failure;
        using (Record(point => {
                   if (point != "upgrade:starting") return;
                   Server.Ready             = HttpStatusCode.ServiceUnavailable;
                   Service.ActivityOverride = new UnitActivity.Exited(1);
                   Service.StopFailure      = new LocalLifecycleException("launchctl bootout failed");
               }))
            failure = await Failure();

        await Assert.That(failure.Switched).IsTrue();
        await Assert.That(failure.Message).Contains("Run kcap local upgrade to finish");
        await Assert.That(JournalPhase()).IsEqualTo(LocalUpgradeJournal.Starting);
    }

    [Test]
    public async Task A_download_failure_leaves_the_old_version_running_untouched() {
        Installed();
        Bundle(New).ManifestMissing = true;

        var failure = await Failure();

        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.BundleNotPublished);
        await Assert.That(failure.Switched).IsFalse();
        await Assert.That(_log).DoesNotContain("stop");
        await Assert.That(Service.Running).IsTrue();
        await Assert.That(RegistryVersion()).IsEqualTo(Old);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(Old);
        await Assert.That(Staged(New)).IsFalse();
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_failed_stop_starts_the_old_version_again() {
        Installed();
        Service.StopFailure = new LocalLifecycleException("the stores did not go down");

        var failure = await Failure();

        await Assert.That(failure.Switched).IsFalse();
        await Assert.That(failure.Message).Contains($"kcap local {Old} was started again");
        await Assert.That(_log.IndexOf("start")).IsGreaterThan(_log.IndexOf("stop"));
        await Assert.That(RegistryVersion()).IsEqualTo(Old);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(Old);
        await Assert.That(Staged(New)).IsFalse();
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_stopped_stack_is_not_started_by_a_failure_before_the_switch() {
        Installed();
        await Service.StopAsync(LocalServerUnit.ForInstallation(Root, Port), CancellationToken.None);
        _log.Clear();
        Service.StopFailure = new LocalLifecycleException("the stores did not go down");

        var failure = await Failure();

        await Assert.That(failure.Message).Contains("stays selected and stopped");
        await Assert.That(_log).DoesNotContain("start");
    }

    [Test]
    public async Task A_new_version_that_does_not_start_stays_selected_and_stopped() {
        Installed();

        LocalUpgradeFailure failure;
        using (Record(point => {
                   if (point != "upgrade:starting") return;
                   Server.Ready             = HttpStatusCode.ServiceUnavailable;
                   Service.ActivityOverride = new UnitActivity.Exited(1);
               }))
            failure = await Failure();

        await Assert.That(failure.Switched).IsTrue();
        await Assert.That(failure.Code).IsEqualTo(LocalInstallFailureCode.StartFailed);
        await Assert.That(failure.Message).Contains("stays selected and stopped");
        await Assert.That(_log[^2..]).IsEquivalentTo(["stop", "journal:cleared"], CollectionOrdering.Matching);
        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(New);
        await Assert.That(Staged(Old)).IsTrue().Because("the old bundle is kept for diagnosis");
    }

    [Test]
    public async Task A_crash_before_the_stop_is_undone_by_the_next_command() {
        Installed();

        using (CrashAt("upgrade:stopping")) await Assert.ThrowsAsync<SimulatedCrash>(() => Upgrade());

        await Assert.That(JournalPhase()).IsEqualTo(LocalUpgradeJournal.Stopping);
        _log.Clear();

        await new LocalStackControl(Stack(), _ => null).StopAsync(_events, _ => { }, CancellationToken.None);

        await Assert.That(_log[0]).IsEqualTo("start");
        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(RegistryVersion()).IsEqualTo(Old);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(Old);
        await Assert.That(Staged(New)).IsFalse();
        await Assert.That(_events.Reports).Contains(new LocalInstallEvent.Recovered(LocalUpgradeJournal.Operation, LocalUpgradeJournal.Stopping));
    }

    [Test]
    public async Task A_switch_that_cannot_be_confirmed_is_finished_forward_by_the_next_upgrade() {
        Installed();

        LocalUpgradeFailure failure;
        using (CrashAt("upgrade:published")) failure = await Failure();

        await Assert.That(failure.Switched).IsTrue();
        await Assert.That(JournalPhase()).IsEqualTo(LocalUpgradeJournal.Switching);
        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(Old);

        // The older CLI's run recovers forward first, then finds the stack newer than itself.
        var result = await Upgrade(Old);

        await Assert.That(result is LocalInstallEvent.NothingToDo { Reason: UpgradeSkip.StackNewer }).IsTrue();
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(New);
        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(Service.Running).IsTrue();
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_crash_while_starting_the_new_version_is_rolled_forward() {
        Installed();

        using (CrashAt("upgrade:starting")) await Assert.ThrowsAsync<SimulatedCrash>(() => Upgrade());

        await Assert.That(JournalPhase()).IsEqualTo(LocalUpgradeJournal.Starting);

        await new LocalStackControl(Stack(), _ => null).StartAsync(_events, _ => { }, CancellationToken.None);

        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(Staged(Old)).IsFalse();
    }

    [Test]
    public async Task A_journal_this_build_did_not_write_changes_nothing() {
        Installed();
        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            lease.Begin(LocalUpgradeJournal.Operation, "migrating", ServerStart.Deny, Id, new Dictionary<string, string>());

        await Assert.ThrowsAsync<LocalLifecycleException>(() => Upgrade());

        await Assert.That(JournalPhase()).IsEqualTo("migrating");
        await Assert.That(_log).IsEmpty();
    }

    [Test]
    [Arguments("1.2.5", New)]
    [Arguments(New, "1.2.5")]
    public async Task Two_callers_in_either_order_leave_the_newer_version(string first, string second) {
        Installed();

        await Upgrade(first);
        var later = await Upgrade(second);

        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(New);
        if (second != New) await Assert.That(later is LocalInstallEvent.NothingToDo { Reason: UpgradeSkip.StackNewer }).IsTrue();
    }

    [Test]
    public async Task Two_concurrent_callers_never_move_the_stack_backwards() {
        Installed();

        var runs = await Task.WhenAll(Upgrade("1.2.5"), Upgrade(New));

        await Assert.That(runs.Count(r => r is LocalInstallEvent.Upgraded)).IsGreaterThanOrEqualTo(1);
        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(New);
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task A_recorded_builtin_setting_downloads_the_new_model_set_before_the_stop() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        var bundle = PublishModelSet();

        var result = await Upgrade();

        await Assert.That(result).IsTypeOf<LocalInstallEvent.Upgraded>();
        await Assert.That(HasModelSet(bundle.ModelSet)).IsTrue();

        var downloading = _events.Reports.FindIndex(e => e is LocalInstallEvent.EmbeddingBuiltinDownloading);
        await Assert.That(downloading).IsGreaterThanOrEqualTo(0);
        await Assert.That(downloading).IsLessThan(_events.Reports.FindIndex(e => e is LocalInstallEvent.Stopping));
    }

    /// <summary>A manifest whose only file the publication does not serve.</summary>
    FakeBundle PublishUnservedModelSet() =>
        PublishModelSet(files: [], entry: new JsonObject {
            ["model_id"]  = "builtin-test",
            ["contract"]  = 1,
            ["model_set"] = new string('e', 64),
            ["files"]     = new JsonArray(new JsonObject { ["name"] = "model.onnx", ["sha256"] = new string('a', 64), ["size"] = 12 }),
        });

    [Test]
    public async Task Model_download_failure_does_not_fail_the_upgrade() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        var bundle = PublishUnservedModelSet();

        var result = await Upgrade();

        await Assert.That(result).IsTypeOf<LocalInstallEvent.Upgraded>();
        await Assert.That(RegistryVersion()).IsEqualTo(New);
        await Assert.That(_events.Reports.Any(e => e is LocalInstallEvent.EmbeddingsSkipped { Code: LocalEmbeddingSkipCode.ModelDownloadFailed })).IsTrue();
        await Assert.That(HasModelSet(bundle.ModelSet)).IsFalse();
    }

    [Test]
    public async Task Cancellation_during_model_download_cancels_the_upgrade_before_switching() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        PublishModelSet();
        using var cts = new CancellationTokenSource();

        var progress = new SyncProgress<LocalInstallEvent>(e => {
            _events.Report(e);
            if (e is LocalInstallEvent.EmbeddingBuiltinProgress) cts.Cancel();
        });

        LocalUpgradeFailure? failure = null;

        try {
            await Upgrade(ct: cts.Token, progress: progress);
        } catch (LocalUpgradeFailure f) {
            failure = f;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Code).IsEqualTo(LocalInstallFailureCode.Cancelled);
        await Assert.That(failure.Switched).IsFalse();
        await Assert.That(_log).DoesNotContain("stop");
        await Assert.That(RegistryVersion()).IsEqualTo(Old);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(Old);
        await Assert.That(Staged(New)).IsFalse();
        await Assert.That(Directory.EnumerateFileSystemEntries(Models)).IsEmpty();
        await Assert.That(JournalAbsent()).IsTrue();
    }

    [Test]
    public async Task Cancellation_while_a_failed_download_is_reported_cancels_the_upgrade() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        PublishUnservedModelSet();
        using var cts = new CancellationTokenSource();

        var progress = new SyncProgress<LocalInstallEvent>(e => {
            _events.Report(e);
            if (e is LocalInstallEvent.EmbeddingsSkipped) cts.Cancel();
        });

        LocalUpgradeFailure? failure = null;

        try {
            await Upgrade(ct: cts.Token, progress: progress);
        } catch (LocalUpgradeFailure f) {
            failure = f;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Code).IsEqualTo(LocalInstallFailureCode.Cancelled);
        await Assert.That(failure.Switched).IsFalse();
        await Assert.That(_log).DoesNotContain("stop");
        await Assert.That(RegistryVersion()).IsEqualTo(Old);
        await Assert.That(LocalBin.Current(Root)).IsEqualTo(Old);
    }

    [Test]
    public async Task Old_model_sets_are_removed_after_commit() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        var bundle = PublishModelSet();
        Tmp.CreateFile(["r", "models", OldSet, "model.onnx"]);
        Tmp.CreateFile(["r", "models", "not-a-model-set", "keep"]);
        Tmp.CreateFile(["r", "models", ".partial-0123456789abcdef", "model.onnx"]);
        Tmp.CreateFile(["r", "models", ".stale-0123456789abcdef", "model.onnx"]);

        await Upgrade();

        await Assert.That(HasModelSet(OldSet)).IsFalse();
        await Assert.That(HasModelSet(bundle.ModelSet)).IsTrue();
        await Assert.That(HasModelSet(".partial-0123456789abcdef")).IsFalse();
        await Assert.That(HasModelSet(".stale-0123456789abcdef")).IsFalse();
        await Assert.That(HasModelSet("not-a-model-set")).IsTrue().Because("only model sets and download leftovers are removed");
    }

    /// <summary>The record names the set the new server serves, so the next setup finds nothing to change and leaves
    /// the unit alone; it stays in the form an older kcap reads as a settled decline.</summary>
    [Test]
    public async Task An_upgrade_records_the_new_model_set_for_a_recorded_builtin_choice() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        var bundle = PublishModelSet();

        await Upgrade();

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(bundle.ModelSet));
        await Assert.That(LocalServerUnit.ForInstallation(Root, Port).Serves(LocalEmbeddingSetting.Builtin(bundle.ModelSet))).IsTrue();

        var wire = JsonNode.Parse(File.ReadAllText(LocalEmbeddingFile.PathFor(Root)))!.AsObject();
        await Assert.That(wire["provider"]!.GetValue<string>()).IsEqualTo("none");
        await Assert.That(wire.ContainsKey("pending")).IsFalse();
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(true, false)]
    public async Task A_pending_builtin_repair_settles_only_after_starting_with_its_model(bool resume, bool served) {
        Installed();
        var bundle = PublishModelSetInArchive(served);
        Recorded(LocalEmbeddingSetting.Builtin(bundle.ModelSet) with { Pending = true });

        if (resume) {
            using (CrashAt("upgrade:starting")) await Assert.ThrowsAsync<SimulatedCrash>(() => Upgrade());
            await Assert.That(LocalEmbeddingFile.Read(Root)!.Pending).IsTrue();
            await new LocalStackControl(Stack(), _ => null).StartAsync(_events, _ => { }, CancellationToken.None);
        } else {
            await Upgrade();
        }

        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(bundle.ModelSet) with { Pending = !served });
    }

    /// <summary>The record still names the old set, so its files stay: the next setup can serve it again.</summary>
    [Test]
    public async Task A_failed_model_download_leaves_the_recorded_model_set() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        PublishUnservedModelSet();
        Tmp.CreateFile(["r", "models", OldSet, "model.onnx"]);

        await Upgrade();

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(OldSet));
        await Assert.That(HasModelSet(OldSet)).IsTrue();
    }

    [Test]
    public async Task A_record_that_cannot_be_rewritten_keeps_the_model_set_it_names() {
        Skip.Unless(!Environment.IsPrivilegedProcess, "needs an unprivileged user: root ignores the read-only data directory");
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        var bundle = PublishModelSet();
        Tmp.CreateFile(["r", "models", OldSet, "model.onnx"]);
        var data = Path.GetDirectoryName(LocalEmbeddingFile.PathFor(Root))!;
        var progress = new SyncProgress<LocalInstallEvent>(e => {
            _events.Report(e);
            if (e is LocalInstallEvent.Stopped) File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        });

        try {
            await Assert.That(await Upgrade(progress: progress)).IsTypeOf<LocalInstallEvent.Upgraded>();
        } finally {
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(OldSet));
        await Assert.That(HasModelSet(OldSet)).IsTrue();
        await Assert.That(HasModelSet(bundle.ModelSet)).IsTrue();
    }

    /// <summary>An upgrade rolled forward by the next lifecycle command settles model sets as a finished one does, from
    /// the model set the new version's bundle carries.</summary>
    [Test]
    public async Task A_rolled_forward_upgrade_records_the_new_model_set_and_removes_the_old_ones() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        var bundle = PublishModelSetInArchive();
        Tmp.CreateFile(["r", "models", OldSet, "model.onnx"]);
        Tmp.CreateFile(["r", "models", ".partial-0123456789abcdef", "model.onnx"]);

        using (CrashAt("upgrade:starting")) await Assert.ThrowsAsync<SimulatedCrash>(() => Upgrade());

        await Assert.That(HasModelSet(OldSet)).IsTrue();

        await new LocalStackControl(Stack(), _ => null).StartAsync(_events, _ => { }, CancellationToken.None);

        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(bundle.ModelSet));
        await Assert.That(HasModelSet(OldSet)).IsFalse();
        await Assert.That(HasModelSet(".partial-0123456789abcdef")).IsFalse();
        await Assert.That(HasModelSet(bundle.ModelSet)).IsTrue();
    }

    [Test]
    public async Task A_rolled_forward_upgrade_whose_model_download_failed_keeps_the_recorded_model_set() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        PublishModelSetInArchive(served: false);
        Tmp.CreateFile(["r", "models", OldSet, "model.onnx"]);

        using (CrashAt("upgrade:starting")) await Assert.ThrowsAsync<SimulatedCrash>(() => Upgrade());
        await new LocalStackControl(Stack(), _ => null).StartAsync(_events, _ => { }, CancellationToken.None);

        await Assert.That(JournalAbsent()).IsTrue();
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(OldSet));
        await Assert.That(HasModelSet(OldSet)).IsTrue();
    }

    [Test]
    public async Task A_rolled_forward_upgrade_leaves_another_recorded_choice_alone() {
        Installed();
        Recorded(LocalEmbeddingSetting.Ollama("nomic-embed-text"));
        PublishModelSetInArchive();

        using (CrashAt("upgrade:starting")) await Assert.ThrowsAsync<SimulatedCrash>(() => Upgrade());
        await new LocalStackControl(Stack(), _ => null).StartAsync(_events, _ => { }, CancellationToken.None);

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Ollama("nomic-embed-text"));
    }

    /// <summary>As <see cref="PublishModelSet"/>, with the entry also in the archive as the server's <c>embedding-model.json</c>;
    /// unless <paramref name="served"/>, no file of the set is served.</summary>
    FakeBundle PublishModelSetInArchive(bool served = true) {
        var entry = new JsonObject {
            ["model_id"]  = "builtin-test",
            ["contract"]  = 1,
            ["model_set"] = new string('e', 64),
            ["files"]     = new JsonArray([.. ModelFiles.Select(f => (JsonNode)new JsonObject {
                ["name"] = f.Key, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(f.Value)), ["size"] = f.Value.Length,
            })]),
        };

        return _bundles[New] = new(Tmp.PathTo($"bundle-{New}.tar.gz"), New, "osx-arm64",
            TestBundles.RegularFile("./kcap-server", "#!/bin/sh\n"), TestBundles.RegularFile("./embedding-model.json", entry.ToJsonString())) {
            ModelFiles = served ? ModelFiles : [], EmbeddingModel = entry,
        };
    }

    [Test]
    public async Task A_bundle_without_a_model_set_reports_a_skip_and_keeps_the_old_one() {
        Installed();
        Recorded(LocalEmbeddingSetting.Builtin(OldSet));
        Tmp.CreateFile(["r", "models", OldSet, "model.onnx"]);

        var result = await Upgrade();

        await Assert.That(result).IsTypeOf<LocalInstallEvent.Upgraded>();
        await Assert.That(_events.Reports.Any(e => e is LocalInstallEvent.EmbeddingsSkipped { Code: LocalEmbeddingSkipCode.BuiltinUnavailable })).IsTrue();
        await Assert.That(HasModelSet(OldSet)).IsTrue();
    }

    [Test]
    public async Task An_upgrade_never_turns_another_recorded_choice_into_builtin() {
        Installed();
        Recorded(LocalEmbeddingSetting.Ollama("nomic-embed-text"));
        var bundle = PublishModelSet();

        await Upgrade();

        await Assert.That(RequestedModels(bundle)).IsFalse();
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Ollama("nomic-embed-text"));
        await Assert.That(_events.Reports.Any(e => e is LocalInstallEvent.EmbeddingsSkipped)).IsFalse();
    }
}
