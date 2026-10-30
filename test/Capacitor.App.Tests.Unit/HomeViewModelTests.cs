using TUnit.Assertions.Enums;
using System.Collections.Frozen;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI.Reactive;

namespace Capacitor.App.Tests.Unit;

/// HomeViewModel's Harnesses/Sessions projections ObserveOn(RxSchedulers.MainThreadScheduler)
/// before touching bound state (same reason as MainWindowViewModel/TrayViewModel), so every test
/// here runs inside AvaloniaSession.WithImmediateRxScheduler and carries
/// [NotInParallel("AvaloniaSession")] — see MainWindowViewModelTests' identical header comment.
public class HomeViewModelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// The daemon mints agent ids as Guid("N") — 32 hex digits — and a Started outcome carrying
    /// anything else is the "launched but unopenable" case, so every launch fixture here
    /// uses real-shaped ids.
    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    const string SecondLaunchedId = "fedcba9876543210fedcba9876543210";

    sealed class RecordingLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public LaunchRequest? Last;
        public LaunchOutcome Next = new(true, LaunchedId, null);
        /// Runs while the call is in flight, before its outcome returns.
        public Action? During;

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Last = request;
            During?.Invoke();
            return Task.FromResult(Next);
        }
    }

    /// Scripted stand-in for RepoPathStore.GetSortedPathsAsync — the real store reads the
    /// developer's own ~/.config/kcap/repos.json, which no test may touch.
    static Func<Task<string[]>> Known(params string[] paths) => () => Task.FromResult(paths);

    /// Primed connected: StartCommand's canExecute gates on daemon + server both being up, so a
    /// fixture that launches must model the connected steady state.
    static HomeViewModel Build(out RecordingLaunchClient launch, out AppStateStore store, string statePath) {
        launch = new RecordingLaunchClient();
        store = new AppStateStore(statePath);
        var daemon = new FakeDaemonClientService();
        Connect(daemon);
        return new HomeViewModel(daemon, store, launch, Known(), TimeProvider.System);
    }

    /// The launcher's model list prefers the server catalog per vendor and falls back to the
    /// curated list where the server offers none — so an empty or unreachable catalog never blocks
    /// a launch.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Model_choices_prefer_the_server_catalog_and_fall_back_to_the_curated_list() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var catalog = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) {
                ["gemini"] = [new("gemini-3-pro", "Gemini 3 Pro")],
            };
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                modelCatalog: Observable.Return<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>(catalog));

            await Assert.That(vm.ModelChoicesFor("gemini").Select(m => m.Slug)).Contains("gemini-3-pro");
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).Contains("claude-opus-5");
            await Assert.That(vm.ModelChoicesFor("cursor")).IsEmpty();
        });
    }

    static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> Server(params (string V, string[] Slugs)[] e) =>
        e.ToDictionary(x => x.V, x => (IReadOnlyList<ModelChoice>) [.. x.Slugs.Select(s => new ModelChoice(s, s))], StringComparer.OrdinalIgnoreCase);

    static IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>> ServerFeed(params (string V, string[] Slugs)[] e) =>
        Observable.Return(Server(e));

    static DaemonInfo Box(string owner, params string[] piModels) => new() {
        Name = "box", OwnerUserId = owner, Connected = true, SupportedVendors = ["pi", "claude"],
        VendorModels = new() { ["pi"] = [.. piModels.Select(m => new VendorModelOptionDto { Value = m, Label = m + " label" })] },
    };

    [Test]
    public async Task MergeCatalogs_machine_key_wins_even_when_empty_else_non_empty_server_else_absent() {
        var machine = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) {
            ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")], ["kiro"] = [] };
        var server  = Server(("pi", ["server-pi"]), ("kiro", ["server-kiro"]), ("gemini", ["gemini-3-pro"]), ("cursor", []));

        var merged = HomeViewModel.MergeCatalogs(machine, server);

        await Assert.That(merged["pi"].Single().Slug).IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(merged["kiro"]).IsEmpty();
        await Assert.That(merged["gemini"].Single().Slug).IsEqualTo("gemini-3-pro");
        await Assert.That(merged.ContainsKey("cursor")).IsFalse();
        await Assert.That(HomeViewModel.MergeCatalogs(null, server).ContainsKey("pi")).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_snapshot_catalog_takes_precedence_and_empty_key_beats_curated() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, modelCatalog: ServerFeed(("claude", ["server-claude"]), ("pi", ["server-pi"])));

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")], ["claude"] = [] }));

            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("anthropic/claude-opus-5");
            await Assert.That(vm.ModelChoicesFor("claude")).IsEmpty();
            await Assert.That(vm.ModelChoicesFor("codex")).IsNotEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Before_any_local_snapshot_the_server_catalog_alone_populates_the_view() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, modelCatalog: ServerFeed(("gemini", ["gemini-3-pro"])));

            await Assert.That(vm.EffectiveModelCatalog["gemini"].Single().Slug).IsEqualTo("gemini-3-pro");
            await Assert.That(vm.ModelChoicesFor("pi")).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_older_daemon_without_a_catalog_leaves_the_server_and_curated_lists() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, modelCatalog: ServerFeed(("gemini", ["gemini-3-pro"]), ("claude", [])));

            await Assert.That(vm.ModelChoicesFor("gemini").Single().Slug).IsEqualTo("gemini-3-pro");
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).Contains("claude-opus-5");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selected_remote_machine_supplies_its_own_catalog_and_follows_registry_updates() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("local/model", "Local")] }));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1", "a/x")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                modelCatalog: ServerFeed(("pi", ["server-pi"])));

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("a/x");

            remote.DaemonsSubject.OnNext([Box("u1", "a/y")]);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("a/y");

            remote.DaemonsSubject.OnNext([]);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("server-pi");

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("local/model");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_restored_repository_never_keeps_a_harness_the_daemon_lacks() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex"]));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_direct_start_with_no_harness_sends_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), launch, Known(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");
            Connect(daemon);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: []));
            vm.Goal = "go";

            await vm.StartCommand.Execute();

            await Assert.That(launch.Last).IsNull();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selected_remote_follows_its_live_vendor_list() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            var box = Box("u1") with { RepoPaths = ["/w/repo"] };
            remote.DaemonsSubject.OnNext([box]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)));

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            remote.DaemonsSubject.OnNext([box with { SupportedVendors = [] }]);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.NoHarnessesNotice("box"));

            remote.DaemonsSubject.OnNext([box with { SupportedVendors = ["codex"] }]);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_same_named_daemon_owned_by_someone_else_never_supplies_the_catalog() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u2", "a/z"), Box("u1", "a/x")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                modelCatalog: ServerFeed(("pi", ["server-pi"])));

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("a/x");

            remote.DaemonsSubject.OnNext([Box("u2", "a/z")]);
            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("server-pi");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_agent_chip_labels_a_model_only_the_machine_lists() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")] }));
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System);

            var text = Capacitor.App.Views.AgentChipTextConverter.Instance.Convert(
                [vm.Harnesses, "pi", "anthropic/claude-opus-5", vm.EffectiveModelCatalog], typeof(string), null,
                System.Globalization.CultureInfo.InvariantCulture);

            await Assert.That(text as string).EndsWith("· Claude Opus 5 · anthropic");
        });
    }

    [Test]
    public async Task ShouldClearSelection_only_when_previously_listed_and_now_absent_under_a_present_key() {
        var listedA  = Server(("pi", ["a", "b"]));
        var withoutA = Server(("pi", ["b"]));
        var noKey    = Server();

        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, withoutA)).IsTrue();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "A", listedA, withoutA)).IsTrue();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "custom", noKey, withoutA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "custom", withoutA, withoutA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", noKey, listedA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, noKey)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, listedA)).IsFalse();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "", listedA, withoutA)).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_to_a_machine_whose_pi_list_lacks_the_pick_resets_the_model_and_a_custom_id_survives() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("a", "A")] }));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1", "b")]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.ChooseHarnessAsync("pi");
            vm.SelectedModel = "a";

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.SelectedVendor).IsEqualTo("pi");
            await Assert.That(vm.SelectedModel).IsEqualTo("");

            vm.SelectedModel = "typed/custom";
            remote.DaemonsSubject.OnNext([Box("u1", "c")]);
            await Assert.That(vm.SelectedModel).IsEqualTo("typed/custom");

            remote.DaemonsSubject.OnNext([Box("u1", "c", "typed/custom")]);
            await Assert.That(vm.SelectedModel).IsEqualTo("typed/custom");
            remote.DaemonsSubject.OnNext([Box("u1", "c")]);
            await Assert.That(vm.SelectedModel).IsEqualTo("");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_refresh_that_drops_the_pick_resets_it_and_a_pick_made_before_any_catalog_survives() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System);
            await vm.ChooseHarnessAsync("pi");
            vm.SelectedModel = "a";

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("b", "B")] }));
            await Assert.That(vm.SelectedModel).IsEqualTo("a");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("a", "A"), new("b", "B")] }));
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("b", "B")] }));
            await Assert.That(vm.SelectedModel).IsEqualTo("");
        });
    }

    /// Repo keys compare the way the filesystem does — so the SAME repository reached under
    /// different casing restores its harness on Windows/macOS, and stays distinct on Linux where
    /// two such paths really are two repositories. Asserting the platform's own answer rather than
    /// one hardcoded expectation is what lets this run on every CI leg.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repo_keys_compare_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/Alpha");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync("/repo/alpha");

            var expected = OperatingSystem.IsLinux() ? HomeViewModel.DefaultVendor : "codex";
            await Assert.That(vm.SelectedVendor).IsEqualTo(expected);

            // And re-choosing under the other casing must overwrite, not accumulate a second
            // shadowing entry, wherever the two paths are the same repository.
            await vm.ChooseHarnessAsync("pi");
            var saved = await store.LoadAsync();
            var expectedKeys = OperatingSystem.IsLinux() ? 2 : 1;
            await Assert.That(saved.HarnessByRepo!.Count).IsEqualTo(expectedKeys);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Choosing_a_harness_remembers_it_for_that_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_repository_restores_that_repositorys_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync("/repo/b");
            await vm.ChooseHarnessAsync("kiro");
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_repository_with_no_choice_falls_back_to_the_default_not_the_previous_repo() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("kiro");
            await vm.SelectRepositoryAsync("/repo/never-seen");

            await Assert.That(vm.SelectedVendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_sends_the_selected_repository_and_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("gemini");
            vm.Goal = "Fix the flaky test";
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.RepoPath).IsEqualTo("/repo/a");
            await Assert.That(launch.Last!.Vendor).IsEqualTo("gemini");
            await Assert.That(launch.Last!.Prompt).IsEqualTo("Fix the flaky test");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_carries_the_chosen_model_and_effort_and_a_vendor_change_resets_the_model() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.SelectedModel = "claude-fable-5";
            vm.SelectedEffort = "high";
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.Model).IsEqualTo("claude-fable-5");
            await Assert.That(launch.Last!.Effort).IsEqualTo("high");

            // Model ids are vendor-specific; effort's ladder is shared and survives.
            await vm.ChooseHarnessAsync("codex");
            await Assert.That(vm.SelectedModel).IsEqualTo("");
            await Assert.That(vm.SelectedEffort).IsEqualTo("high");

            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.Model).IsEqualTo("");
        });
    }

    /// Manual must ride the launch: without the flag Claude falls back to the user's own
    /// <c>permissions.defaultMode</c>, which can be <c>auto</c> while the chip still reads Manual.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Permission_mode_defaults_to_manual_which_is_sent_explicitly() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(HomeViewModel.DefaultPermissionMode);

            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo(ClaudePermissionModes.Manual);
        });
    }

    /// The mode vocabulary is Claude's, so a choice made under Claude never rides a launch of
    /// another vendor — but it is kept, not reset, so switching back restores it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_chosen_permission_mode_is_sent_for_claude_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.SelectedPermissionMode = "bypassPermissions";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo("bypassPermissions");

            await vm.ChooseHarnessAsync("codex");
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsNull();

            await vm.ChooseHarnessAsync("claude");
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo("bypassPermissions");
        });
    }

    /// An untouched launcher sends no posture, so a daemon without posture support still accepts it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Codex_posture_defaults_to_the_daemons_own_pair_which_sends_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsNull();
        });
    }

    /// Changing either half sends both — the daemon rejects a partial posture — and only for codex.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_chosen_codex_posture_is_sent_whole_and_for_codex_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            vm.SelectedCodexApproval = "never";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsEqualTo(new CodexLaunchPosture("workspace-write", "never"));

            vm.SelectedCodexSandbox = "read-only";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsEqualTo(new CodexLaunchPosture("read-only", "never"));

            await vm.ChooseHarnessAsync("claude");
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_start_surfaces_the_servers_reason() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
        });
    }

    /// Storage isolation only: ScratchRepoPath ("") is a reserved HarnessByRepo key that must not
    /// share or clobber a real repository's remembered vendor. It says nothing about launching it
    /// — the daemon does not accept a repo-less launch (HomeViewModel.ScratchRepoPath).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_scratch_target_keeps_its_own_remembered_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync(HomeViewModel.ScratchRepoPath);
            await vm.ChooseHarnessAsync("claude");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo![HomeViewModel.ScratchRepoPath]).IsEqualTo("claude");
            await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    static AgentStatusDto Agent(string id, string? repoPath) => new(
        id, "agent", "claude", repoPath, "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_merges_remembered_repos_and_agent_repos() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            daemon.Agents.AddOrUpdate(Agent("x", "/repo/b"));

            var repos = await vm.ListRepositoriesAsync();

            var a = repos.Single(r => r.RepoPath == "/repo/a");
            var b = repos.Single(r => r.RepoPath == "/repo/b");
            await Assert.That(a.Vendor).IsEqualTo("codex");
            await Assert.That(b.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
            await Assert.That(a.Selected).IsTrue();
            await Assert.That(b.Selected).IsFalse();
        });
    }

    /// Same platform-follows-filesystem rule as the harness-memory test above: a daemon-supplied
    /// path and a remembered key differing only in case are one repository on Windows/macOS and
    /// two on Linux. This merge is exactly the case that motivated PathComparer.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_dedups_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            await vm.SelectRepositoryAsync("/repo/Alpha");
            await vm.ChooseHarnessAsync("codex");
            daemon.Agents.AddOrUpdate(Agent("x", "/repo/alpha"));

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            var expected = OperatingSystem.IsLinux() ? 2 : 1;
            await Assert.That(repos.Count).IsEqualTo(expected);
            // The remembered key is the one the user picked, so its casing is the one displayed.
            await Assert.That(repos.Any(r => r.RepoPath == "/repo/Alpha")).IsTrue();
        });
    }

    /// Trailing separators are spelling, not identity — a remembered path and a known/agent path
    /// that differ only by `/` must stay one menu row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_dedups_trailing_separators() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                () => Task.FromResult(new[] { "/repo/kcap-cli/" }), TimeProvider.System);

            await vm.SelectRepositoryAsync("/repo/kcap-cli");
            daemon.Agents.AddOrUpdate(Agent("x", "/repo/kcap-cli/"));

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            await Assert.That(repos.Count).IsEqualTo(1);
            await Assert.That(repos[0].RepoPath).IsEqualTo("/repo/kcap-cli");
            await Assert.That(repos[0].Selected).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Scratch_appears_only_when_no_real_repositories() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            var empty = await vm.ListRepositoriesAsync();
            await Assert.That(empty.Count).IsEqualTo(1);
            await Assert.That(empty[0].RepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);

            daemon.Agents.AddOrUpdate(Agent("x", "/repo/b"));
            var withRepo = await vm.ListRepositoriesAsync();

            await Assert.That(withRepo.Any(r => r.RepoPath.Length == 0)).IsFalse();
            await Assert.That(withRepo.Single(r => r.RepoPath == "/repo/b").Selected).IsTrue();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/b");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_selection_adopts_the_most_recent_known_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            // GetSortedPathsAsync is last-used first — index 0 is the most recent.
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                Known("/repo/newer", "/repo/older"), TimeProvider.System);

            await vm.EnsureDefaultRepositoryAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/newer");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Any(r => r.RepoPath.Length == 0)).IsFalse();
            await Assert.That(repos.Single(r => r.RepoPath == "/repo/newer").Selected).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task EnsureDefault_does_not_overwrite_a_pick_made_during_discovery() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var release = new TaskCompletionSource();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                async () => {
                    await release.Task;
                    return ["/repo/default"];
                }, TimeProvider.System);

            var ensure = vm.EnsureDefaultRepositoryAsync();
            await vm.SelectRepositoryAsync("/repo/user-picked");
            release.SetResult();
            await ensure;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/user-picked");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agent_without_a_repository_contributes_no_entry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.Agents.AddOrUpdate(Agent("x", null));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos.Count).IsEqualTo(1);
            await Assert.That(repos[0].RepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
        });
    }

    /// A repository picked through the folder dialog but not yet remembered (and with no agent in
    /// it) must still appear — otherwise closing and reopening the menu would lose it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_current_selection_appears_even_when_unsaved_and_agentless() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            await vm.SelectRepositoryAsync("/repo/fresh");

            var repos = await vm.ListRepositoriesAsync();

            var fresh = repos.Single(r => r.RepoPath == "/repo/fresh");
            await Assert.That(fresh.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repositories_are_ordered_by_leaf_name() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.Agents.AddOrUpdate(Agent("x", "/x/bravo"));
            daemon.Agents.AddOrUpdate(Agent("y", "/y/alpha"));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos[0].RepoPath).IsEqualTo("/y/alpha");
            await Assert.That(repos[1].RepoPath).IsEqualTo("/x/bravo");
        });
    }

    /// A reviewer launched into a requester's worktree reports that worktree as its RepoPath —
    /// the menu must offer the repository, never the agent's checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agents_worktree_path_is_listed_as_its_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.Agents.AddOrUpdate(Agent("x", "/repo/a/.claude/worktrees/leafy"));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos.Any(r => r.RepoPath == "/repo/a")).IsTrue();
            await Assert.That(repos.Any(r => r.RepoPath.Contains("worktrees"))).IsFalse();
        });
    }

    /// The daemon's persisted known-repos store (repos.json — the same list DaemonConnect.RepoPaths
    /// feeds the server's launch dialog) is a source of its own: a repo you once ran an agent in
    /// must appear with no live agent and no locally remembered harness.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_includes_the_daemons_persisted_known_repos() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(),
                Known("/repo/recorded"), TimeProvider.System);

            var repos = await vm.ListRepositoriesAsync();

            var known = repos.Single(r => r.RepoPath == "/repo/recorded");
            await Assert.That(known.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_known_repo_dedups_against_a_remembered_key_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                Known("/repo/alpha", "/repo/beta"), TimeProvider.System);

            await vm.SelectRepositoryAsync("/repo/Alpha");
            await vm.ChooseHarnessAsync("codex");

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            // beta only exists in the store, so it proves the source is wired; alpha collides
            // with the remembered key wherever the filesystem says they are one repository.
            await Assert.That(repos.Any(r => r.RepoPath == "/repo/beta")).IsTrue();
            var expected = OperatingSystem.IsLinux() ? 3 : 2;
            await Assert.That(repos.Count).IsEqualTo(expected);
            await Assert.That(repos.Any(r => r.RepoPath == "/repo/Alpha")).IsTrue();
        });
    }

    /// The daemon's advertised vendor set is what narrows the picker, end to end: a snapshot
    /// arriving on the Snapshots stream must reach the bound Harnesses list.
    /// HostedHarnessCatalogTests covers Build in isolation; this covers the wire into it, which
    /// nothing else did.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_snapshot_narrows_the_harness_picker() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            // Before any snapshot: capability unknown, so everything is offered.
            var piBefore = vm.Harnesses.Single(h => h.Vendor == "pi").Available;

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));

            await Assert.That(piBefore).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "pi").Available).IsFalse();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsTrue();
        });
    }

    static void Connect(FakeDaemonClientService daemon, string connection = "connected") {
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: connection));
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_is_disabled_until_daemon_and_server_are_both_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            Connect(daemon);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    /// An update the user went on from early holds local launches until its steps settle, then hands Start back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_waits_for_an_update_that_is_still_finishing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");
            Connect(daemon);
            var progress = new BehaviorSubject<(bool, string?)>((true, "Reconnecting the daemon to the server…"));
            vm.AttachUpdateProgress(progress);

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.FinishingUpdateNotice);
            await Assert.That(vm.Notices.ShowUpdate).IsTrue();
            await Assert.That(vm.UpdateDetail).IsEqualTo("Reconnecting the daemon to the server");

            progress.OnNext((false, null));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");
            await Assert.That(vm.Notices.ShowUpdate).IsFalse();
        });
    }

    /// The update changes only this machine, so a remote selection keeps its own notices and launches.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_selection_is_not_held_by_a_local_update() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([Box("u1") with { RepoPaths = ["/w/repo"] }]);
            using var vm = new HomeViewModel(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                Known(), TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)));
            vm.AttachUpdateProgress(new BehaviorSubject<(bool, string?)>((true, "Starting the local server…")));

            await vm.SelectMachineAsync("box", isLocal: false);

            await Assert.That(vm.Notices.ShowUpdate).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_is_disabled_with_a_notice_when_the_daemon_hosts_no_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);
            await vm.SelectRepositoryAsync("/repo/a");
            Connect(daemon);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: []));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));

            using var lifecycle = new BehaviorSubject<string?>(null);
            vm.AttachDaemonRecovery(ReactiveCommand.Create(() => { }), ReactiveCommand.Create(() => { }),
                Observable.Return(false), Observable.Return(false), lifecycle);
            const string serviceNotice = "Daemon service replacement was skipped. Restart kcap to review it again.";
            lifecycle.OnNext(serviceNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(serviceNotice);
            await Assert.That(vm.BannerDetail).IsNull();
            lifecycle.OnNext(null);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex"]));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_lost_server_connection_tells_the_user_to_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            Connect(daemon);
            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.ServerLostNotice);

            // Transient by definition — the retry resolves it or lands on "disconnected".
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "reconnecting"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    /// The lane can go SignedOut (a remote HTTP fetch's own 401, ParkSignedOut) independently of
    /// the local daemon's attach state — the sign-in affordance must surface either way.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaneSignedOutSurfacesSignInEvenWithTheLocalDaemonDown() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System, laneStatus: lane.Status);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    /// The app lane can report Connected while the daemon's connection word is still
    /// "disconnected" — tokens landed, the daemon has not caught up. That is still sign-in
    /// catch-up: Sign in would restart a flow that already succeeded. Only a SignedOut park
    /// (or the daemon becoming Ready / down) settles the finishing notice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaneConnectedDoesNotAskToSignInWhileDaemonIsStillCatchingUp() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected"));
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaneSignedOutAfterSignInShowsSignInAgain() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            vm.NotifySignInCompleted();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    /// The same signed-out lane that offers Sign in above offers nothing once the server is the
    /// local installation: there is no credential to repair, and the Start banner owns recovery.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_server_never_shows_the_launcher_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.LocalServer).IsSameReferenceAs(local);
        });
    }

    /// The Start banner is the whole story for a stopped local server: the connection banner under
    /// it must neither spin "Connecting", nor tell the user to install an agent, nor, after the
    /// catch-up bound, tell the user to sign in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stopped_local_server_shows_no_connecting_or_sign_in_notice() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected", supportedVendors: []));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(local.Visible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.ConnectionBannerVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));

            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.BannerMessage ?? "").DoesNotContain("Sign in");
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    /// The local server can report down while the daemon still says connected, leaving no raw
    /// notice for the stopped-server rule to hide.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stopped_local_server_shows_no_agents_notice_before_the_daemon_notices() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected", supportedVendors: []));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(local.Visible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    (HomeViewModel Vm, LocalServerRecoveryViewModel Local, FakeServerLane Lane, FakeDaemonClientService Daemon) LocalServerWithRemote(
            bool remoteConnected, List<IDisposable> owned) {
        var daemon = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        remote.DaemonsSubject.OnNext([
            new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = remoteConnected, RepoPaths = ["/w/repo"] },
        ]);
        var lane = new FakeServerLane();
        var vm = new HomeViewModel(
            daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
            daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status,
            appServerUrl: "http://localhost:9999");
        var local = new LocalServerRecoveryViewModel(
            lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
            () => Task.CompletedTask, CancellationToken.None);
        vm.AttachLocalServerRecovery(local);
        owned.Add(vm);
        owned.Add(local);
        Connect(daemon, "connected");

        return (vm, local, lane, daemon);
    }

    /// The Start banner speaks for this Mac only: under a remote pick it must not hide that machine's notice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_pick_keeps_its_notice_while_the_local_server_is_down() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var owned = new List<IDisposable>();
            try {
                var (vm, local, lane, _) = LocalServerWithRemote(remoteConnected: true, owned);
                await vm.SelectMachineAsync("home-pc", isLocal: false);
                lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

                await Assert.That(local.Visible).IsTrue();
                await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            } finally {
                owned.ForEach(d => d.Dispose());
            }
        });
    }

    /// A remote machine that dropped off is that machine's story, not the local server's, and never a sign-in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_offline_remote_pick_under_a_running_local_server_names_the_machine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var owned = new List<IDisposable>();
            try {
                var (vm, local, lane, _) = LocalServerWithRemote(remoteConnected: false, owned);
                lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
                await vm.SelectMachineAsync("home-pc", isLocal: false);

                await Assert.That(local.Visible).IsFalse();
                await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.RemoteOfflineNotice("home-pc"));
                await Assert.That(vm.ConnectionNotice!).DoesNotContain("Sign in");
            } finally {
                owned.ForEach(d => d.Dispose());
            }
        });
    }

    /// Selecting this Mac again restores the local server's own handling.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task This_mac_selected_under_a_down_local_server_leaves_the_start_banner_alone() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var owned = new List<IDisposable>();
            try {
                var (vm, local, lane, daemon) = LocalServerWithRemote(remoteConnected: true, owned);
                await vm.SelectMachineAsync("home-pc", isLocal: false);
                await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
                Connect(daemon, "disconnected");
                lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

                await Assert.That(local.Visible).IsTrue();
                await Assert.That(vm.ConnectionNotice).IsNull();
            } finally {
                owned.ForEach(d => d.Dispose());
            }
        });
    }

    /// A local server takes no sign-in: its refusal must not ask for one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_signed_out_local_server_names_a_restart_not_a_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            Connect(daemon, "connected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.ConnectionNotice ?? "").DoesNotContain("Sign in");
            await Assert.That(vm.BannerMessage ?? "").DoesNotContain("Sign in");
            await Assert.That(vm.ConnectionNotice).IsNotNull();
        });
    }

    /// A local server takes no sign-in, so no state of its connection may ask for one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_local_server_notice_asks_for_a_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);
            var shown = new List<string>();
            void Record() => shown.AddRange(new[] { vm.ConnectionNotice, vm.BannerMessage }.OfType<string>());

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));
            Record();
            time.Advance(HomeViewModel.CatchUpLimit);
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Dormant));
            Record();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            Record();

            await Assert.That(shown).IsNotEmpty();
            await Assert.That(shown.Where(t => t.Contains("sign in", StringComparison.OrdinalIgnoreCase))).IsEmpty();
        });
    }

    /// Suppressing the server notices must not hide the daemon's own: a local server with the
    /// daemon down still says so.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_server_keeps_the_daemon_down_notice() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
        });
    }

    /// First paint after wizard auth never calls NotifySignInCompleted — Home is constructed
    /// afterwards. A live app lane plus a daemon still saying "disconnected" is catch-up, not
    /// "sign in again".
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALiveAppLaneTreatsDaemonDisconnectAsConnecting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "HTTP://LOCALHOST:9999/");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(
                serverUrl: "https://other.example", connection: "disconnected"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonOnOtherServerNotice("https://other.example", "HTTP://LOCALHOST:9999/"));
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsFalse();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALiveAppLaneStopsTreatingDaemonDisconnectAsConnectingAfterTheCatchUpBound() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task AStuckConnectingDaemonOffersSignInAfterTheCatchUpBound() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "connecting");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CatchUpBoundDoesNotFireIfTheDaemonConnectsInTime() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);

            time.Advance(HomeViewModel.CatchUpLimit - TimeSpan.FromSeconds(1));
            Connect(daemon, "connected");

            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(TimeSpan.FromSeconds(2));
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task FinishingSignInAlsoExpiresToSignInAfterTheCatchUpBound() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999", time: time);

            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            vm.NotifySignInCompleted();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    /// A daemon attached by name that serves another server says so, and offers no sign-in, which could
    /// not move it, whatever the app's own lane is doing.
    [Test]
    [Arguments(ServerLaneState.Connecting, "reconnecting")]
    [Arguments(ServerLaneState.Connected, "reconnecting")]
    [Arguments(ServerLaneState.Connected, "connected")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_serving_another_server_says_so_instead_of_asking_for_sign_in(ServerLaneState laneState, string connection) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: "https://kurrent.kcap.ai");
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(serverUrl: "https://norton.kcap.ai:443", connection: connection));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(laneState));

            await Assert.That(vm.ConnectionNotice)
                .IsEqualTo("This machine's daemon uses another server. It's connected to norton.kcap.ai, so it can't run agents for kurrent.kcap.ai.");
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    /// Without both servers known there is no mismatch to report, so a lost connection still asks for sign-in.
    [Test]
    [Arguments(ServerLaneState.Connected, null, "https://other.example")]
    [Arguments(ServerLaneState.Connected, "https://app.example", "")]
    [NotInParallel("AvaloniaSession")]
    public async Task AnUnrelatedAppLaneDoesNotMaskTheLocalServerDisconnection(
            ServerLaneState laneState, string? appServerUrl, string daemonServerUrl) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                laneStatus: lane.Status, appServerUrl: appServerUrl);
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(
                serverUrl: daemonServerUrl, connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(laneState));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();

            vm.NotifySignInCompleted();

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task BannerBusyFollowsTheVisibleMessageAndSelectedMachine(bool afterSignIn) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: lane.Status, appServerUrl: "http://localhost:9999");
            Connect(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));
            if (afterSignIn) vm.NotifySignInCompleted();

            using var startMessage = new BehaviorSubject<string?>(null);
            using var start = ReactiveCommand.Create(() => { });
            using var retry = ReactiveCommand.Create(() => { });
            vm.AttachDaemonRecovery(start, retry, Observable.Return(false), Observable.Return(true), startMessage);
            await Assert.That(vm.BannerBusy).IsTrue();

            const string failure = "Daemon start did not finish. Press Retry.";
            startMessage.OnNext(failure);
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(failure);
            await Assert.That(vm.BannerBusy).IsFalse();

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.BannerMessage).IsNull();
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.BannerBusy).IsFalse();

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(failure);
            await Assert.That(vm.BannerBusy).IsFalse();

            startMessage.OnNext(null);
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.BannerMessage).IsEqualTo(
                afterSignIn ? HomeViewModel.FinishingSignInNotice : HomeViewModel.ConnectingNotice);
            await Assert.That(vm.BannerBusy).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreachable_daemon_is_not_a_sign_in_problem() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.ConnectionBannerVisible).IsTrue();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    /// An update's daemon restart is explained and shown as in progress, and the catch-up timer that turns a long
    /// connecting line into "sign in again" does not apply to it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_restarting_after_an_update_is_explained_not_reported_as_down() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, ExpectedDaemonRestart.Reason, null));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonRestartingNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonRestartingNotice);
            await Assert.That(vm.BannerBusy).IsTrue();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(HomeViewModel.BusyNotice(HomeViewModel.DaemonRestartingNotice)).IsFalse();
        });
    }

    /// A remote selection's notices/tooltip/Start-enabled must follow the SELECTED machine's own
    /// availability (RemoteAvailabilityFor), never the local daemon's — a local daemon left down
    /// or disconnected must never leak its own notice into a remote pick, and a lane loss under a
    /// remote pick must still surface something rather than nothing. Switching back to local
    /// restores the legacy local-only notices.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task NoticesFollowTheSelectedMachineNotTheLocalDaemon() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            // Local daemon Unreachable, lane Connected, remote machine Connected: no banner, the
            // local daemon's own down-notice never surfaces, Start is enabled.
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.ConnectionBannerVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connecting));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);

            // Back to local: the local daemon's own Unreachable notice applies again.
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
        });
    }

    [Test]
    [Arguments(null, null, null, null)]
    [Arguments("daemon down", null, "daemon down", null)]
    [Arguments("daemon down", "kcap too old", "daemon down", "kcap too old")]
    [Arguments(null, "kcap too old", "kcap too old", null)]
    [Arguments("daemon down", "Wrong server. Run this: `kcap fix`", "Wrong server. Run this: `kcap fix`", null)]
    [Arguments("daemon down", "", "daemon down", null)]
    [Arguments("daemon down", "daemon down", "daemon down", null)]
    [Arguments(HomeViewModel.SignInExpiredNotice, "Reconnecting…", HomeViewModel.SignInExpiredNotice, null)]
    [Arguments(HomeViewModel.SignInExpiredNotice, "kcap too old", HomeViewModel.SignInExpiredNotice, null)]
    public async Task BannerContent_keeps_daemon_output_below_the_explanation(
            string? notice, string? startMessage, string? expectedMessage, string? expectedDetail) {
        var content = HomeViewModel.BannerContentFor(notice, startMessage);
        await Assert.That(content.Message).IsEqualTo(expectedMessage);
        await Assert.That(content.Detail).IsEqualTo(expectedDetail);
    }

    /// Red is for something that broke. A daemon that is merely down, or a start still in flight,
    /// needs the user without anything having failed.
    [Test]
    [Arguments(HomeViewModel.DaemonDownNotice, null, false)]
    [Arguments(HomeViewModel.DaemonDownNotice, "Starting the daemon…", false)]
    [Arguments(HomeViewModel.DaemonDownNotice, "The daemon didn't start. Press Start daemon to try again.", true)]
    [Arguments(HomeViewModel.DaemonIncompatibleNotice, null, true)]
    [Arguments(HomeViewModel.LocalServerRefusedNotice, null, true)]
    [Arguments(HomeViewModel.ServerLostNotice, null, false)]
    [Arguments(HomeViewModel.SignInExpiredNotice, null, false)]
    public async Task Only_a_failure_turns_the_banner_red(string message, string? detail, bool failed) =>
        await Assert.That(HomeViewModel.BannerFailedFor((message, detail))).IsEqualTo(failed);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unauthorized_start_asks_for_sign_in_instead_of_the_raw_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var signInRequests = 0;
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, requestSignIn: () => signInRequests++);
            Connect(daemon);
            await vm.SelectRepositoryAsync("/repo/a");
            launch.Next = new LaunchOutcome(
                false, null, "Response status code does not indicate success: 401 (Unauthorized).",
                Unauthorized: true);

            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);

            await vm.SignInCommand.Execute();
            await Assert.That(signInRequests).IsEqualTo(1);

            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task After_sign_in_a_disconnected_server_shows_finishing_not_sign_in_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                appServerUrl: "http://localhost:9999");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);

            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected"));
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_successful_start_clears_the_goal_and_any_previous_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            launch.Next = new LaunchOutcome(false, null, "boom");
            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            launch.Next = new LaunchOutcome(true, SecondLaunchedId, null);
            vm.Goal = "next thing";
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.Goal).IsEqualTo("");
        });
    }

    // The launcher's machine picker: name-based launch routing is only defined within one owner.
    // ListMachinesAsync applies that filter at listing time; every test below reads its output
    // rather than trusting a wired-through daemon list. StartAsync separately re-verifies
    // ownership at invocation time — see the launch-path tests further down for that layer.

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PickerOffersLocalPlusOwnConnectedDaemons() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", MachineId = "m2", Connected = true },
                new DaemonInfo { Name = "work-mac", OwnerUserId = "u2", MachineId = "m3", Connected = true },
                // Same machine id + name as the local daemon: the local daemon's own registry
                // twin, never a distinct remote target.
                new DaemonInfo { Name = daemon.DaemonName, OwnerUserId = "u1", MachineId = "m1", Connected = true },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), localMachineId: "m1");

            var options = await vm.ListMachinesAsync();

            await Assert.That(options.Count).IsEqualTo(2);
            await Assert.That(options[0].DaemonName).IsEqualTo(daemon.DaemonName);
            await Assert.That(options[0].IsLocal).IsTrue();
            await Assert.That(options[0].Selected).IsTrue();
            await Assert.That(options[1].DaemonName).IsEqualTo("home-pc");
            await Assert.That(options.Any(o => o.DaemonName == "work-mac")).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task NullViewerIdOffersOnlyLocal() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true }]);
            // No viewerId supplied — the ctor default never guesses ownership.
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System, daemons: remote.Daemons);

            var options = await vm.ListMachinesAsync();

            await Assert.That(options.Count).IsEqualTo(1);
            await Assert.That(options[0].DaemonName).IsEqualTo(daemon.DaemonName);
        });
    }

    /// The local daemon (server B) and an owned remote daemon (server A) can share a name — name-
    /// based routing alone can never tell them apart, so the caller's own isLocal (from the
    /// clicked MachineOption) is what SelectMachineAsync must trust.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingARemoteEntryThatSharesTheLocalDaemonsNameSelectsTheRemoteOne() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService { DaemonName = "work" };
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "work", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/a/repo"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("work", isLocal: false);

            await Assert.That(vm.RemoteMachineSelected).IsTrue();
            await Assert.That(vm.SelectedMachine).IsEqualTo("work");
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/a/repo");
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "codex").Available).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingRemoteMachineSwitchesRepoAndVendorSources() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var store = new AppStateStore(path);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo", "/w/repo2"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, store, new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Count).IsEqualTo(2);
            await Assert.That(repos.Any(r => r.RepoPath == "/w/repo")).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "codex").Available).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsFalse();
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            // Picking another of the MACHINE's own repo paths must revalidate against that
            // machine's supported vendors — never fall through to the local HarnessByRepo/
            // DefaultVendor rule, which would reset onto "claude" (unsupported here) since
            // "/w/repo2" has no local entry.
            await vm.SelectRepositoryAsync("/w/repo2");
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo2");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo is null || saved.HarnessByRepo.Count == 0).IsTrue();
        });
    }

    /// <summary>A machine advertises its repositories most recently used first. The picker lists them by
    /// name like the local list, and the most recently used one stays the selection. A Windows machine's
    /// path is named by its last segment on any desktop.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteMachineRepositoriesAreListedByName() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/widgets", @"Z:\x\Engine", "/w/api"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(), Known(),
                TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath).ToArray())
                .IsEquivalentTo(["/w/api", @"Z:\x\Engine", "/w/widgets"], CollectionOrdering.Matching);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/widgets");
        });
    }

    /// Pins the remote repository menu to the registry as it stands at menu open: a path the machine
    /// advertises after it was selected (its first launch there) is offered without re-selecting it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRepositoryMenuReadsTheMachinesCurrentAdvertisement() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo", "/w/new"] },
            ]);

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath)).IsEquivalentTo(["/w/repo", "/w/new"]);
        });
    }

    /// Pins the typed remote path: a full path on any platform is selected, offered in the menu
    /// before the machine advertises it, and never written to the local store; anything else is
    /// refused with the selection left alone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task EnteringARemoteRepositorySelectsAFullPathAndRefusesARelativeOne() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var store = new AppStateStore(path);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), store, new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(await vm.EnterRemoteRepositoryAsync("git/other")).IsNotNull();
            await Assert.That(await vm.EnterRemoteRepositoryAsync("  ")).IsNotNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");

            await Assert.That(await vm.EnterRemoteRepositoryAsync("  /srv/other  ")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/srv/other");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Single(r => r.Selected).RepoPath).IsEqualTo("/srv/other");
            await Assert.That(repos.Count).IsEqualTo(2);

            await Assert.That(await vm.EnterRemoteRepositoryAsync(@"C:\src\repo")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(@"C:\src\repo");
            await Assert.That(await vm.EnterRemoteRepositoryAsync(@"\\host\share\repo")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(@"\\host\share\repo");
            await Assert.That(await vm.EnterRemoteRepositoryAsync("C:/")).IsNull();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("C:/");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo is null || saved.HarnessByRepo.Count == 0).IsTrue();
        });
    }

    /// Pins that a local repository discovered after a switch to a remote machine is not selected on
    /// that machine, where the path means nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LocalDefaultDiscoveryFinishingAfterARemotePickLeavesItEmpty() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var known = new TaskCompletionSource<string[]>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = [] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(Tmp.PathTo("app-state.json")), new RecordingLaunchClient(),
                () => known.Task, TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            var discovery = vm.EnsureDefaultRepositoryAsync();
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            known.SetResult(["/local/repo"]);
            await discovery;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
        });
    }

    /// Pins remote path identity to the remote's rules, not this host's: on a case-insensitive host a
    /// typed /w/repo must not fold into an advertised /w/Repo and mark that row selected.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ATypedRemotePathDifferingOnlyInCaseIsItsOwnRow() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/Repo"] },
            ]);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(await vm.EnterRemoteRepositoryAsync("/w/repo")).IsNull();

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath)).IsEquivalentTo(["/w/Repo", "/w/repo"]);
            await Assert.That(repos.Single(r => r.Selected).RepoPath).IsEqualTo("/w/repo");
        });
    }

    /// Pins the menu to the selected remote as the registry lists it now: once that remote is gone,
    /// neither its selection-time paths nor a same-named local twin's paths are offered.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRepositoryMenuNeverOffersAVanishedRemoteOrALocalTwinsPaths() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = daemon.DaemonName, OwnerUserId = "u1", MachineId = "m2", Connected = true,
                    RepoPaths = ["/remote/a", "/remote/b"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), localMachineId: "m1");
            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: false);

            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = daemon.DaemonName, OwnerUserId = "u1", MachineId = "m1", Connected = true,
                    RepoPaths = ["/local/repo"],
                },
            ]);

            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Select(r => r.RepoPath)).IsEquivalentTo(["/remote/a"]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaunchCarriesTheSelectedMachine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.DaemonName).IsEqualTo("home-pc");
        });
    }

    /// Pins remote failure matching to the remote's case rules: a refusal for /w/Repo does not land
    /// on /w/repo, which a Linux remote holds as a different repository, while one for the path
    /// still selected does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchFailureDoesNotLandOnAPathDifferingOnlyInCase() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/Repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(Tmp.PathTo("app-state.json")), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await vm.StartCommand.Execute();
            await vm.EnterRemoteRepositoryAsync("/w/repo");
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));
            await Assert.That(vm.StartError).IsNull();

            launch.Next = new LaunchOutcome(true, "agent-10", null);
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-10", "launch_denied_by_owner: default"));
            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    static AgentRow RemoteRow(string agentId) => AgentRow.FromRemote(new AgentInstanceDto {
        AgentId = agentId, Status = "Running", DaemonName = "home-pc", OwnerUserId = "u1",
        Vendor = "claude", RepoOwner = "o", RepoName = "r",
    });

    /// Like the window's own, an open under a stale generation is refused and one that lands bumps it.
    static HomeViewModel RemoteLauncher(
            string path, FakeAgentDirectory directory, List<(string AgentId, AgentOrigin Origin, int Generation)> opened,
            RecordingLaunchClient? launch = null, StrongBox<int>? navigationBox = null, Action<string>? afterOpen = null) {
        var navigation = navigationBox ?? new StrongBox<int>(7);
        var remote = new FakeRemoteAgents();
        remote.DaemonsSubject.OnNext([
            new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
        ]);
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
        return new HomeViewModel(
            new FakeDaemonClientService(), new AppStateStore(path), launch ?? new RecordingLaunchClient(), Known(), TimeProvider.System,
            navigationGeneration: () => navigation.Value,
            openSessionIfCurrent: (id, origin, generation) => {
                if (generation != navigation.Value) return;
                opened.Add((id, origin, generation));
                navigation.Value++;
                afterOpen?.Invoke(id);
            },
            daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status,
            directory: directory);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchOpensOnItsPlaceholderAtOnce() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();

            await Assert.That(directory.RemotePlaceholders).IsEquivalentTo([(LaunchedId, "home-pc", (string?)"u1", vm.SelectedVendor, "/w/repo")]);
            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7)]);
        });
    }

    /// The placeholder row in the directory does not settle the launch; the registry's row does,
    /// and it reaches the open placeholder workspace under the generation that open left behind.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchSettlesOnTheRegistryRowNotItsPlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(AgentRow.RemotePlaceholder(
                LaunchedId, "home-pc", "claude", "/w/repo", null, null, DateTime.UtcNow, new RepoIdentity("repo:o/r", "o/r"), "u1/home-pc"));
            await Assert.That(opened).Count().IsEqualTo(1);

            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    /// A navigation while the launch was in flight refuses the placeholder's open, and the registry
    /// row arriving later must not open over what the user moved to either.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchTheUserNavigatedAwayFromNeverOpens() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            var navigation = new StrongBox<int>(7);
            var launch = new RecordingLaunchClient { During = () => navigation.Value++ };
            using var vm = RemoteLauncher(path, directory, opened, launch, navigation);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEmpty();
        });
    }

    /// Holds what is scheduled on it until drained, as the UI thread holds a posted open until the
    /// work it is doing returns.
    sealed class QueuedScheduler : IScheduler {
        readonly Queue<Action> _queue = new();
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) {
            _queue.Enqueue(() => action(this, state));
            return System.Reactive.Disposables.Disposable.Empty;
        }
        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);
        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);
        public void Drain() { while (_queue.TryDequeue(out var next)) next(); }
    }

    /// The registry's differently spelled row can be confirmed off the UI thread while the placeholder
    /// is still opening; its open runs after that, and must carry the generation the open left.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARegistryRowConfirmedWhileThePlaceholderOpensStillTakesOver() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            var dashed = Guid.ParseExact(LaunchedId, "N").ToString("D");
            var ui = new QueuedScheduler();
            using var vm = RemoteLauncher(path, directory, opened, new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) },
                afterOpen: id => {
                    if (id != dashed) return;
                    var prior = RxSchedulers.MainThreadScheduler;
                    RxSchedulers.MainThreadScheduler = ui;
                    try { directory.Rows.AddOrUpdate(RemoteRow(LaunchedId)); } finally { RxSchedulers.MainThreadScheduler = prior; }
                });

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            ui.Drain();

            await Assert.That(opened).IsEquivalentTo([(dashed, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    /// The registry may spell the id another way than the launch's accept did; its row still takes
    /// over from the placeholder workspace.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARegistryRowSpelledDifferentlyStillOpensFromThePlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            var dashed = Guid.ParseExact(LaunchedId, "N").ToString("D");
            using var vm = RemoteLauncher(path, directory, opened, new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) });

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEquivalentTo([(dashed, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteRowThatBeatTheLaunchReturnOpensAtOnce() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();

            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7)]);
        });
    }

    /// A same-id local row is a different agent: only the remote lane's row settles a remote launch.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalRowWithTheSameIdDoesNotSettleARemoteLaunch() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var directory = new FakeAgentDirectory();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = RemoteLauncher(path, directory, opened);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent(LaunchedId, "claude", false, "/w/repo"), new RepoIdentity("path:/w/repo", "repo")));
            directory.Rows.AddOrUpdate(RemoteRow(LaunchedId));

            await Assert.That(opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Remote, 7), (LaunchedId, AgentOrigin.Remote, 8)]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalLaunchStillAutoOpensTheWorkspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            var opened = new List<(string AgentId, AgentOrigin Origin, int Generation)>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                openSessionIfCurrent: (id, origin, generation) => opened.Add((id, origin, generation)));

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(opened.Count).IsEqualTo(1);
            await Assert.That(opened[0].AgentId).IsEqualTo(LaunchedId);
            await Assert.That(opened[0].Origin).IsEqualTo(AgentOrigin.Local);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalLaunchAddsAPlaceholderRowForTheAcceptedId() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.Goal = "Fix the flaky test\nand more";
            await vm.StartCommand.Execute();

            await Assert.That(directory.Placeholders).Count().IsEqualTo(1);
            var placeholder = directory.Placeholders[0];
            await Assert.That(placeholder.Id).IsEqualTo(LaunchedId);
            await Assert.That(placeholder.Vendor).IsEqualTo(vm.SelectedVendor);
            await Assert.That(placeholder.RepoPath).IsEqualTo("/repo/a");
            await Assert.That(placeholder.Title).IsEqualTo("Fix the flaky test");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchAddsNoLocalPlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status, directory: directory);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await vm.StartCommand.Execute();

            await Assert.That(directory.Placeholders).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALaunchFailureRemovesThePlaceholder() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "boom"));

            await Assert.That(directory.RemovedPlaceholders).Contains("agent-9");
        });
    }

    /// Same-id rows on different lanes are different agents, so a remote row can neither stand in
    /// for a local launch's placeholder nor settle its failure tracking.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteRowWithTheSameIdDoesNotSettleALocalLaunch() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "agent-9", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RepoOwner = "o", RepoName = "r",
            }));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            await Assert.That(directory.Placeholders.Select(p => p.Id)).Contains("agent-9");

            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));
            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    /// The pending row is the launch's own stand-in, so its arrival proves nothing: a failure
    /// after it must still render, and only a real agent row settles the launch.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task APendingRowDoesNotConfirmTheLaunch() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new FakeAgentDirectory();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            directory.Rows.AddOrUpdate(AgentRow.FromPending(
                new PendingLaunchDto("agent-9", "claude", "/repo/a", null, DateTime.UtcNow, "spawned"), new RepoIdentity("path:/repo/a", "a")));
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    /// The owned-remote list can legitimately empty out from under an already-selected remote
    /// machine (a registry blip) — the picker must stay reachable so the user can switch back to
    /// local, rather than hiding itself with no way to change the selection.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PickerStaysVisibleForAnActiveRemoteSelectionEvenWhenTheOwnedListEmpties() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await WaitUntilAsync(() => vm.MachinePickerVisible, "picker visible after selecting home-pc");

            remote.DaemonsSubject.OnNext([]);
            await Task.Delay(20); // let the CombineLatest re-run over the now-empty owned list
            await Assert.That(vm.MachinePickerVisible).IsTrue();

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.RemoteMachineSelected).IsFalse();
        });
    }

    static async Task WaitUntilAsync(Func<bool> condition, string what, int ms = 2000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteSelectionRequiresTheLane() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true }]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.BannerBusy).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        });
    }

    // Display-list filtering alone doesn't protect the launch path — a name can reach
    // SelectMachineAsync/StartCommand without ever having passed through ListMachinesAsync.

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingAnUnownedNameIsRefused() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "work-mac", OwnerUserId = "u2", Connected = true }]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectRepositoryAsync("/repo/a");

            // "work-mac" never passed ListMachinesAsync's own-daemons filter (it's u2's) — calling
            // SelectMachineAsync with it directly (a stale menu row, a forged binding) must be a
            // no-op, not a silent remote selection.
            await vm.SelectMachineAsync("work-mac", isLocal: false);

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            await Assert.That(vm.SelectedMachine).IsEqualTo(daemon.DaemonName);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ReassignedDaemonOwnerRevokesAnAlreadySelectedMachine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            // Same name, now a different owner — a registry refresh racing the earlier selection.
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u2", Connected = true, RepoPaths = ["/w/repo"] },
            ]);

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            // ReactiveCommand.Execute() does not itself gate on CanExecute (verified directly: an
            // unguarded call here reaches StartAsync regardless) — the actual boundary is
            // StartAsync's own fresh ownership re-check, so calling it straight through must still
            // refuse rather than reach the wire.
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.MachineUnavailableMessage);
            await Assert.That(launch.Last).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SwitchingBackToLocalRestoresTheLocalRepoAndRevalidatesTheVendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var store = new HeldAppStateStore(new AppState());
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["kiro"]));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, store, new RecordingLaunchClient(), Known("/repo/local"), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            store.Release();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            // The existing local flow (EnsureDefaultRepositoryAsync) adopts the known repo —
            // never the remote machine's path.
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/local");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Any(r => r.RepoPath == "/w/repo")).IsFalse();
            // codex isn't in the local daemon's advertised set ("kiro") — revalidated, not left at
            // whatever the remote machine last set it to.
            await Assert.That(vm.SelectedVendor).IsEqualTo("kiro");
        });
    }

    /// The remote machine hosts DefaultVendor as well as the vendor the pick kept, so revalidation
    /// alone would accept the late local adoption's reset to DefaultVendor.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ADefaultRepositoryLoadThatLandsAfterARemotePickLeavesItsVendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var store = new HeldAppStateStore(new AppState());
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["codex"]));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo"], SupportedVendors = ["claude", "codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, store, new RecordingLaunchClient(), Known("/repo/local"), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc", isLocal: false);
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            store.Release();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    /// Holds the first LoadAsync, the constructor's default-repository adoption, until Release. The
    /// source completes without RunContinuationsAsynchronously, so the adoption finishes inside
    /// Release on the test's own thread rather than racing it from the pool.
    sealed class HeldAppStateStore(AppState state) : IAppStateStore {
        readonly TaskCompletionSource<AppState> _first = new();
        int _loads;

        public Task<AppState> LoadAsync() => Interlocked.Increment(ref _loads) == 1 ? _first.Task : Task.FromResult(state);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) => Task.FromResult(true);

        public void Release() => _first.SetResult(state);
    }

    // Launch-outcome correlation: the accepted id is request-accepted, not success.

    /// Pushes the failure before returning the launch Task, to exercise the race where a
    /// LaunchFailed arrives while StartAsync's invoke is still in flight.
    sealed class FailureBeforeReturnLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string AgentId { get; init; }
        public required string Reason { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Failures.OnNext(new LaunchFailure(AgentId, Reason));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    public async Task DenialReasonRendersFriendly() =>
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("launch_denied_by_owner: prompt_no_ui"))
            .Contains("consent policy denied");

    [Test]
    public async Task A_cancelled_launch_reads_as_cancelled() =>
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(WireTokens.LaunchCancelled)).IsEqualTo("The launch was cancelled.");

    [Test]
    public async Task An_empty_snapshot_failure_is_a_sentence() {
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("standalone_snapshot_empty"))
            .IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("git commit -m Initial snapshot failed:"))
            .IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(
                "git commit -m Initial snapshot failed: On branch main\nnothing to commit"))
            .IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
        await Assert.That(HomeViewModel.FriendlyLaunchFailure(
                "git commit -m Initial snapshot failed: fatal: unable to write new index file"))
            .IsEqualTo(HomeViewModel.WorkspacePrepFailed + " Unable to write new index file");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_repository_clears_the_start_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            launch.Next = new LaunchOutcome(false, null, "git commit -m Initial snapshot failed:");

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.EmptyFolderStartMessage);

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.EmptyFolderStartMessage);

            // Same folder under a different casing is still that folder on macOS/Windows.
            if (!OperatingSystem.IsLinux()) {
                await vm.SelectRepositoryAsync("/repo/A");
                await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.EmptyFolderStartMessage);
            }

            await vm.SelectRepositoryAsync("/repo/b");
            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_for_the_previous_repository_does_not_stick() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            await vm.SelectRepositoryAsync("/repo/b");
            failures.OnNext(new LaunchFailure("agent-9", "git commit -m Initial snapshot failed:"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_for_the_previous_machine_does_not_stick() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/repo/a"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            await vm.SelectMachineAsync("home-pc", isLocal: false);
            failures.OnNext(new LaunchFailure("agent-9", "git commit -m Initial snapshot failed:"));

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_for_local_does_not_stick_on_a_same_named_remote() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService { DaemonName = "home-pc" };
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/repo/a"],
                    MachineId = "remote-machine",
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"),
                localMachineId: "local-machine");

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            await vm.SelectMachineAsync("home-pc", isLocal: false);
            failures.OnNext(new LaunchFailure("agent-9", "git commit -m Initial snapshot failed:"));

            await Assert.That(vm.SelectedMachine).IsEqualTo("home-pc");
            await Assert.That(vm.RemoteMachineSelected).IsTrue();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaunchFailureAfterAcceptSetsStartError() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task FailureBeforeInvokeReturnsIsStillApplied() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var failures = new Subject<LaunchFailure>();
            var launch = new FailureBeforeReturnLaunchClient {
                Failures = failures, AgentId = "agent-9", Reason = "launch_denied_by_owner: default",
            };
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ForeignFailuresAreIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("other-id", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RowAppearanceClearsPendingSoLateFailureIsIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            daemon.Agents.AddOrUpdate(Agent("agent-9", "/repo/a"));
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// The hub can return either id shape (NormalizeAgentId's own comment) — a failure carrying
    /// the DASHED form must still correlate against the "N"-normalized key StartAsync recorded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task DashedGuidFailureCorrelatesWithTheNormalizedPendingKey() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System, launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    /// Same id-shape mismatch as above, on the row-confirmation path: a directory row carrying
    /// the dashed form must still clear the "N"-normalized pending entry.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task DashedGuidRowClearsThePendingEntrySoALateDashedFailureIsIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            daemon.Agents.AddOrUpdate(Agent(dashed, "/repo/a"));
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Adds the directory row before the launch call returns, so it exists before RecordPendingLaunch
    /// ever runs — the race a directory-row Add event can never observe (nothing was pending yet).
    sealed class RowBeforeReturnLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required FakeDaemonClientService Daemon { get; init; }
        public required string AgentId { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Daemon.Agents.AddOrUpdate(Agent(AgentId, request.RepoPath));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARowThatPredatesRecordingIsConfirmedImmediately() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RowBeforeReturnLaunchClient { Daemon = daemon, AgentId = "agent-9" };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Same predates-recording race as above, but with a DASHED-Guid row and a dashed accepted
    /// id — RowExists must compare under NormalizeAgentId, not a raw "local:{id}" key lookup,
    /// since the directory key preserves the row's incoming id spelling verbatim.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ADashedGuidRowThatPredatesRecordingIsConfirmedImmediately() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RowBeforeReturnLaunchClient { Daemon = daemon, AgentId = dashed };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Adds the directory row AND pushes a LaunchFailure for the same id before StartAsync's own
    /// invoke returns — RecordPendingLaunch must confirm the row and discard the buffered failure
    /// without ever rendering it, whichever order its own two checks run in.
    sealed class RowAndFailureBeforeReturnLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required FakeDaemonClientService Daemon { get; init; }
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string AgentId { get; init; }
        public required string Reason { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Daemon.Agents.AddOrUpdate(Agent(AgentId, request.RepoPath));
            Failures.OnNext(new LaunchFailure(AgentId, Reason));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARowPresentAndABufferedFailureBeforeReturnNeverRendersTheFailure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var failures = new Subject<LaunchFailure>();
            var launch = new RowAndFailureBeforeReturnLaunchClient {
                Daemon = daemon, Failures = failures, AgentId = "agent-9",
                Reason = "launch_denied_by_owner: default",
            };
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null, TimeProvider.System);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// A directory that reports every read of its Rows, so a test can land an Add at a chosen point
    /// of the caller's own read sequence — the only way to reach the window between HomeViewModel's
    /// two row checks, which the Add event itself cannot close (nothing is pending when it fires).
    sealed class ReadHookedDirectory : IAgentDirectory, IDisposable {
        readonly SourceCache<AgentRow, string> _source = new(r => r.Key);
        readonly IObservableCache<AgentRow, string> _rows;
        public Action? OnRead;

        public ReadHookedDirectory() => _rows = _source.AsObservableCache();

        public IObservableCache<AgentRow, string> Rows {
            get { OnRead?.Invoke(); return _rows; }
        }

        public IObservable<bool> RemoteStale => Observable.Return(false);
        public IObservable<bool> LocalDaemonOnAppServer => Observable.Return(true);

        public IObservable<IReadOnlyDictionary<string, string>> SessionAgents =>
            Observable.Return((IReadOnlyDictionary<string, string>)FrozenDictionary<string, string>.Empty);
        public string? VendorOfSession(string sessionId) => null;
        public (string Id, AgentOrigin Origin)? RowForSession(string sessionId) => null;
        public bool IsProvenLocalTwin(string agentId) => false;
        public bool IsHostedNow(AgentRow row) => false;
        public void AddPlaceholder(string agentId, string vendor, string repoPath, string? title, string? model) { }
        public void AddRemotePlaceholder(
            string agentId, string machine, string? ownerUserId, string vendor, string repoPath, string? title, string? model) { }
        public void RemovePlaceholder(string agentId) { }

        public void Add(string agentId) => _source.AddOrUpdate(
            AgentRow.FromLocal(Agent(agentId, "/repo/a"), new RepoIdentity("path:/repo/a", "repo")));

        public void Dispose() {
            _rows.Dispose();
            _source.Dispose();
        }
    }

    /// Buffers a failure for the launched id, then arms the directory to add that id's row on the
    /// SECOND row check of the launch that follows — the first is RecordPendingLaunch's leading
    /// check, the second its re-check after registering the pending entry.
    sealed class RowBetweenTheRowChecksLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public required ReadHookedDirectory Directory { get; init; }
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string AgentId { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Failures.OnNext(new LaunchFailure(AgentId, "launch_denied_by_owner: default"));
            var reads = 0;
            Directory.OnRead = () => { if (++reads == 2) Directory.Add(AgentId); };
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARowAppearingAfterTheLeadingCheckStillDiscardsTheBufferedFailure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var failures = new Subject<LaunchFailure>();
            using var directory = new ReadHookedDirectory();
            var launch = new RowBetweenTheRowChecksLaunchClient {
                Directory = directory, Failures = failures, AgentId = "agent-9",
            };
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), TimeProvider.System,
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// The daemon lifecycle detail and its Start daemon action speak for the local machine: under a
    /// healthy remote pick both yield, and both come back the moment the selection does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalStartMessageBannerYieldsToARemoteSelection() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            const string startMessageText = "App and daemon are incompatible.";
            var daemon = new FakeDaemonClientService();
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            using var startMessage = new BehaviorSubject<string?>(startMessageText);
            vm.AttachDaemonRecovery(
                ReactiveCommand.Create(() => { }), ReactiveCommand.Create(() => { }),
                Observable.Return(true), Observable.Return(false), startMessage);

            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(startMessageText);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await vm.SelectMachineAsync("home-pc", isLocal: false);

            await Assert.That(vm.BannerMessage).IsNull();
            await Assert.That(vm.BannerDetail).IsNull();
            await Assert.That(vm.ConnectionBannerVisible).IsFalse();
            await Assert.That(vm.DaemonStartVisible).IsFalse();
            await Assert.That(vm.BannerActionsVisible).IsFalse();

            await vm.SelectMachineAsync(daemon.DaemonName, isLocal: true);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.BannerDetail).IsEqualTo(startMessageText);
            await Assert.That(vm.DaemonStartVisible).IsTrue();
            await Assert.That(vm.BannerActionsVisible).IsTrue();
        });
    }
}
