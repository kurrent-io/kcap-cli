using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// Scripted IDaemonClientService fake (FakeDaemonClientService) — subject-backed
/// Status/Snapshots/Agents — so VM tests drive exact event sequences without a real daemon.
/// All tests here touch RxSchedulers (the VM's WhenActivated projections use
/// RxSchedulers.MainThreadScheduler), so every test runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class MainWindowViewModelTests {
    // Real AppNotifier — these tests do not exercise toasts; AgentActionService still needs one.
    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service) {
        var notifier = new AppNotifier();
        var actions = new AgentActionService(new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        return (actions, notifier);
    }

    /// The view-state/rail-wiring tests' standard construction: a VM over the fake service, with
    /// an optional workspace factory and rail — mirrors NewActions' shape.
    sealed class UnusedLaunchClient : ILaunchClient {
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, "unexpected launch"));
    }

    static MainWindowViewModel NewVm(
            FakeDaemonClientService service, Func<string, WorkspaceViewModel>? workspaceFactory = null,
            SessionRailViewModel? rail = null, Func<string, AgentOrigin?>? originOf = null,
            Func<string, RemoteSessionViewModel?>? remoteWorkspaceFactory = null,
            Action<Func<Task>>? trackWorkspaceTeardown = null, IAgentDirectory? directory = null,
            Action<FeedbackCategory>? openFeedback = null, IUrlOpener? opener = null) {
        var (actions, _) = NewActions(service);
        return new MainWindowViewModel(
            service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            trackWorkspaceTeardown: trackWorkspaceTeardown,
            workspaceFactory: workspaceFactory, rail: rail,
            originOf: originOf, remoteWorkspaceFactory: remoteWorkspaceFactory, directory: directory,
            openFeedback: openFeedback, opener: opener);
    }

    /// The remote host's dependencies, held together so a test disposes them once. The lane is
    /// never connected, so even a row that carries a session id dials nothing and the routing
    /// assertions stay deterministic.
    sealed class RemoteHost : IDisposable {
        readonly FakeServerLane _lane = new();
        readonly SessionAccessService _access;
        readonly FakePermissionService _permissions = new();

        public RemoteHost() => _access = new SessionAccessService(_lane, new FakeTimeProvider());

        public FakeAgentDirectory Directory { get; } = new();

        // A separate overload rather than an optional parameter: the factory parameter takes this
        // as a method group, and that conversion needs an exact one-argument signature.
        public RemoteSessionViewModel New(string agentId) => New(agentId, null);

        /// A session id only where a test needs one: it is what a local row is compared against
        /// before the removal counts as an origin change rather than an ended session.
        public RemoteSessionViewModel New(string agentId, string? sessionId) {
            var row = AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = agentId, SessionId = sessionId, Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            });
            Directory.Rows.AddOrUpdate(row);
            return new RemoteSessionViewModel(row, Directory, _access, _permissions, WorkspaceFixtures.NewActions(), _lane,
                (_, _) => Task.FromResult(new SessionDetailFetch(null)), new RecordingOpener(), new FakeTimeProvider(),
                () => new FakeTerminalSurface());
        }

        public void Dispose() {
            _access.Dispose();
            _permissions.Dispose();
            Directory.Dispose();
        }
    }

    /// A real WorkspaceViewModel over the fake service and scripted attach/surface fakes — same
    /// pieces WorkspaceNavigationTests.NewNav wires, just without its Nav bookkeeping.
    static WorkspaceViewModel NewWorkspace(FakeDaemonClientService service, string agentId) {
        var (actions, _) = NewActions(service);
        var attach = new FakeTerminalAttachClientFactory();
        return new WorkspaceViewModel(
            agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(),
            new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader());
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Projections_follow_the_snapshot() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999", connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await Assert.That(vm.DaemonName).IsEqualTo("daemon-a");
            await Assert.That(vm.DaemonVersion).IsEqualTo("1.2.3");
            await Assert.That(vm.ServerUrl).IsEqualTo("http://localhost:9999");
            await Assert.That(vm.ConnectionText).IsEqualTo("connected"); // raw wire value, unchanged
            await Assert.That(vm.ConnectionDisplay).IsEqualTo("Connected"); // new presentation projection
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Restart_pending_marks_the_indicator_while_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var restartPending = new BehaviorSubject<bool>(false);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, restartPending: restartPending);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap());
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.RestartPending).IsFalse();
            await Assert.That(vm.RestartPendingText).IsNull();

            restartPending.OnNext(true);

            await Assert.That(vm.RestartPending).IsTrue();
            await Assert.That(vm.RestartPendingText).IsEqualTo(MainWindowViewModel.RestartPendingMessage);

            restartPending.OnNext(false);

            await Assert.That(vm.RestartPending).IsFalse();
            await Assert.That(vm.RestartPendingText).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Restart_pending_is_hidden_while_not_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var restartPending = new BehaviorSubject<bool>(true);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, restartPending: restartPending);
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.RestartPending).IsFalse();
            await Assert.That(vm.RestartPendingText).IsNull();
        });
    }

    // ---- VersionDisplay (spec: SEMVER only, everything from the first '+' is build metadata) ----

    [Test]
    [Arguments("1.2.3+abc", "1.2.3")]
    [Arguments("1.2.3", "1.2.3")]
    [Arguments("1.2.3+a.b+c", "1.2.3")] // only the FIRST '+' matters
    [Arguments("", "")]
    [Arguments(null, "")]
    public async Task VersionDisplay_strips_build_metadata(string? raw, string expected) {
        await Assert.That(MainWindowViewModel.StripBuildMetadata(raw)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1.2.3+abc", "daemon 1.2.3")]
    [Arguments("1.2.3", "daemon 1.2.3")]
    [Arguments("", "")]
    [Arguments(null, "")]
    public async Task VersionLabelForRail_prefixes_the_stripped_semver(string? raw, string expected) {
        await Assert.That(MainWindowViewModel.VersionLabelForRail(raw)).IsEqualTo(expected);
    }

    // ---- ConnectionDisplay / StatusDotBrush (local attach State first, daemon Connection only
    // once Connected — see MainWindowViewModel.ConnectionDisplayFor's doc comment) ----

    [Test]
    [Arguments(AttachState.Connecting, null, "connected", "Connecting…")]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", "Unreachable")]
    [Arguments(AttachState.Unreachable, "daemon_incompatible", "connected", "Incompatible")]
    [Arguments(AttachState.Connected, null, "connected", "Connected")]
    [Arguments(AttachState.Connected, null, "connecting", "Connecting…")]
    [Arguments(AttachState.Connected, null, "reconnecting", "Reconnecting…")]
    [Arguments(AttachState.Connected, null, "disconnected", "Disconnected")]
    public async Task ConnectionDisplayFor_maps_state_and_daemon_connection_to_one_capitalized_word(
            AttachState state, string? reason, string daemonConnection, string expected) {
        var status = new AttachStatus(state, reason, null);
        await Assert.That(MainWindowViewModel.ConnectionDisplayFor(status, daemonConnection)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(AttachState.Connected, null, "reconnecting")]
    [Arguments(AttachState.Connected, null, "connected")]
    [Arguments(AttachState.Connecting, null, "connected")]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected")]
    public async Task ConnectionDisplayFor_signed_out_wins_over_attach_and_daemon_connection(
            AttachState state, string? reason, string daemonConnection) {
        var status = new AttachStatus(state, reason, null);
        await Assert.That(MainWindowViewModel.ConnectionDisplayFor(status, daemonConnection, signInExpired: true))
            .IsEqualTo(MainWindowViewModel.SignedOutDisplay);
    }

    [Test]
    [Arguments(null, "")]
    [Arguments("", "")]
    [Arguments("   ", "")]
    [Arguments("default", "")]
    [Arguments("Default", "")]
    [Arguments("kurrent", "kurrent")]
    public async Task ProfileLabelForRail_hides_the_built_in_default(string? profile, string expected) {
        await Assert.That(MainWindowViewModel.ProfileLabelForRail(profile)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(AttachState.Connecting, null, "connected", "#FFB300")]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", "#9E9E9E")]
    [Arguments(AttachState.Unreachable, "daemon_incompatible", "connected", "#E53935")]
    [Arguments(AttachState.Connected, null, "connected", "#4CAF50")]
    [Arguments(AttachState.Connected, null, "connecting", "#FFB300")]
    [Arguments(AttachState.Connected, null, "reconnecting", "#FFB300")]
    [Arguments(AttachState.Connected, null, "disconnected", "#E53935")]
    public async Task StatusDotFor_maps_to_the_matching_bucket_color(
            AttachState state, string? reason, string daemonConnection, string expectedHex) {
        var status = new AttachStatus(state, reason, null);
        var brush = (SolidColorBrush)MainWindowViewModel.StatusDotFor(status, daemonConnection);
        await Assert.That(brush.Color).IsEqualTo(Color.Parse(expectedHex));
    }

    [Test]
    [Arguments(AttachState.Connected, null, "reconnecting")]
    [Arguments(AttachState.Connecting, null, "connected")]
    public async Task StatusDotFor_signed_out_uses_the_disrupted_color(
            AttachState state, string? reason, string daemonConnection) {
        var status = new AttachStatus(state, reason, null);
        var brush = (SolidColorBrush)MainWindowViewModel.StatusDotFor(status, daemonConnection, signInExpired: true);
        await Assert.That(brush.Color).IsEqualTo(Color.Parse("#E53935"));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ConnectionDisplay_signed_out_wins_over_daemon_reconnecting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var home = new HomeViewModel(
                service, new AppStateStore(path), new UnusedLaunchClient(),
                () => Task.FromResult(Array.Empty<string>()), TimeProvider.System, laneStatus: lane.Status);
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                home: home, laneStatus: lane.Status);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(connection: "reconnecting"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.ConnectionDisplay).IsEqualTo("Reconnecting…");

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            await Assert.That(home.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);
            await Assert.That(vm.ConnectionDisplay).IsEqualTo(MainWindowViewModel.SignedOutDisplay);
            var brush = (SolidColorBrush)vm.StatusDotBrush;
            await Assert.That(brush.Color).IsEqualTo(Color.Parse("#E53935"));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ConnectionTip_uses_the_lane_diagnostic_otherwise_names_attach() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System, laneStatus: lane.Status);
            using var activation = vm.Activator.Activate();

            await Assert.That(vm.ConnectionTip).IsEqualTo(MainWindowViewModel.AttachStatusTip);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Diagnostic: "diagnostic-marker"));
            await Assert.That(vm.ConnectionTip).IsEqualTo("diagnostic-marker");
            await Assert.That(vm.ServerLaneTip).IsEqualTo("diagnostic-marker");

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.ConnectionTip).IsEqualTo(MainWindowViewModel.AttachStatusTip);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task VersionDisplay_is_the_prefixed_daemon_semver() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = NewVm(service);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(version: "1.2.3+abc"));
            await Assert.That(vm.VersionDisplay).IsEqualTo("daemon 1.2.3");
            await Assert.That(vm.DaemonVersion).IsEqualTo("1.2.3+abc");
        });
    }

    // ---- Start/Reconnect visibility (one primary action: Start when unreachable-down;
    // Reconnect when connecting or skew — never both) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_and_reconnect_visibility_are_mutually_exclusive() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
            await Assert.That(vm.StartVisible).IsFalse();
            await Assert.That(vm.RetryVisible).IsTrue();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.StartVisible).IsFalse();
            await Assert.That(vm.RetryVisible).IsFalse();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.StartVisible).IsTrue();
            await Assert.That(vm.RetryVisible).IsFalse();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));
            await Assert.That(vm.StartVisible).IsFalse();
            await Assert.That(vm.RetryVisible).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartVisible_stays_true_while_a_start_is_in_flight_unlike_CanExecute() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            var startCanExecute = false;
            using var subStart = vm.StartDaemonCommand.CanExecute.Subscribe(v => startCanExecute = v);

            var gate = new TaskCompletionSource();
            service.StartBehavior = async _ => {
                await gate.Task;
                return new StartDaemonResult(true, null);
            };

            var execute = vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(startCanExecute).IsFalse(); // command disabled while in flight...
            await Assert.That(vm.StartVisible).IsTrue();  // ...but the button itself stays visible

            gate.SetResult();
            await execute;
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Agent_count_renders_only_while_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(active: 2, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.AgentCountText).IsEqualTo("2 of 5 agents");

            // Retention is the SERVICE's concern (spec §5) — the fake never clears its snapshot
            // on disconnect either; the VM merely stops RENDERING the count.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.AgentCountText).IsEqualTo("—");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Command_enablement_matrix() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);

            var startCanExecute = false;
            var retryCanExecute = false;
            using var subStart = vm.StartDaemonCommand.CanExecute.Subscribe(v => startCanExecute = v);
            using var subRetry = vm.RetryCommand.CanExecute.Subscribe(v => retryCanExecute = v);

            foreach (var reason in new[] { "daemon_unreachable", "daemon_incompatible" }) {
                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
                await Assert.That(startCanExecute).IsFalse();
                await Assert.That(retryCanExecute).IsTrue();

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
                await Assert.That(startCanExecute).IsFalse();
                await Assert.That(retryCanExecute).IsFalse();

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, reason, null));
                await Assert.That(startCanExecute).IsEqualTo(reason == "daemon_unreachable");
                await Assert.That(retryCanExecute).IsEqualTo(reason != "daemon_unreachable");
            }

            // In-flight: an outstanding start disables StartDaemonCommand even though the
            // status itself keeps satisfying (Unreachable, daemon_unreachable) throughout —
            // ReactiveCommand's own CanExecute ANDs in "not currently executing".
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(startCanExecute).IsTrue();

            var gate = new TaskCompletionSource();
            service.StartBehavior = async _ => {
                await gate.Task;
                return new StartDaemonResult(true, null);
            };

            var execute = vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(startCanExecute).IsFalse(); // in flight

            gate.SetResult();
            await execute;
            await Assert.That(startCanExecute).IsTrue(); // status unchanged, attempt finished
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Incompatible_renders_neutral_skew_message() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            using var activation = vm.Activator.Activate();

            var startCanExecute = false;
            var retryCanExecute = false;
            using var subStart = vm.StartDaemonCommand.CanExecute.Subscribe(v => startCanExecute = v);
            using var subRetry = vm.RetryCommand.CanExecute.Subscribe(v => retryCanExecute = v);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));

            await Assert.That(vm.Reason).IsNotNull();
            await Assert.That(vm.Reason!).Contains("App and daemon are incompatible");
            await Assert.That(vm.Reason!).Contains("Reconnect");
            await Assert.That(vm.Reason!).DoesNotContain("daemon_incompatible");
            await Assert.That(vm.StartVisible).IsFalse();
            await Assert.That(vm.RetryVisible).IsTrue();
            await Assert.That(startCanExecute).IsFalse();
            await Assert.That(retryCanExecute).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_message_lifecycle() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.Reason).IsEqualTo(MainWindowViewModel.UnreachableMessage);
            await Assert.That(vm.Reason!).DoesNotContain("daemon_unreachable");

            service.StartBehavior = _ => Task.FromResult(new StartDaemonResult(false, "boom: could not bind socket"));
            await vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(vm.StartMessage).IsEqualTo("boom: could not bind socket");

            // StartingMessage is set before the gated startAction returns.
            var gate = new TaskCompletionSource();
            service.StartBehavior = async _ => {
                await gate.Task;
                return new StartDaemonResult(true, null);
            };
            var execute = vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(vm.StartMessage).IsEqualTo(MainWindowViewModel.StartingMessage);
            gate.SetResult();
            await execute;
            await Assert.That(vm.StartMessage).IsEqualTo("Daemon start requested. Waiting to connect…");

            // A transition to Connected clears it too.
            service.StartBehavior = _ => Task.FromResult(new StartDaemonResult(false, "second failure"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(vm.StartMessage).IsEqualTo("second failure");

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.StartMessage).IsNull();
        });
    }

    // ILifecycleSurface.Status one-liners ride the same StartMessage lane.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lifecycle_status_sets_and_is_cleared_like_a_start_failure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var lifecycleStatus = new Subject<string?>();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                lifecycleStatus: lifecycleStatus);
            using var activation = vm.Activator.Activate();

            lifecycleStatus.OnNext("daemon started, app not yet attached — retrying");
            await Assert.That(vm.StartMessage).IsEqualTo("daemon started, app not yet attached — retrying");

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.StartMessage).IsNull(); // same Connected-transition clear RunStartAsync's own message gets
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reconnect_sets_reconnecting_then_settles_if_still_unreachable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            using var activation = vm.Activator.Activate();

            // Reconnect is offered while connecting or skewed — not while Start owns the down case.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
            await Assert.That(vm.StartMessage).IsNull();
            await Assert.That(vm.RetryVisible).IsTrue();

            var execute = vm.RetryCommand.Execute().ToTask();
            await Assert.That(vm.StartMessage).IsEqualTo(MainWindowViewModel.ReconnectingMessage);
            await execute;

            // Still unreachable after the reattach kick: replace the in-flight copy.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
            await Assert.That(vm.StartMessage).IsEqualTo(MainWindowViewModel.ReconnectingMessage);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.StartMessage).IsEqualTo(MainWindowViewModel.ReconnectFailedMessage);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.StartMessage).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Already_running_reconnect_status_clears_when_attach_stays_unreachable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lifecycleStatus = new Subject<string?>();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                lifecycleStatus: lifecycleStatus);
            using var activation = vm.Activator.Activate();

            lifecycleStatus.OnNext(DaemonLifecycleController.AlreadyRunningReconnectStatus);
            await Assert.That(vm.StartMessage).IsEqualTo(DaemonLifecycleController.AlreadyRunningReconnectStatus);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.StartMessage).IsEqualTo(MainWindowViewModel.ReconnectFailedMessage);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lifecycle_attention_also_lands_on_the_start_message_lane() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lifecycleAttention = new Subject<string?>();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                lifecycleAttention: lifecycleAttention);
            using var activation = vm.Activator.Activate();

            lifecycleAttention.OnNext("The daemon didn't come up cleanly. Press Start daemon to try again.");
            await Assert.That(vm.StartMessage).IsEqualTo("The daemon didn't come up cleanly. Press Start daemon to try again.");
        });
    }

    // Supplied startAction runs instead of StartDaemonAsync.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartDaemonCommand_invokes_the_supplied_startAction_instead_of_StartDaemonAsync() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var calls = 0;
            CancellationToken? seen = null;
            Task StartAction(CancellationToken ct) {
                calls++;
                seen = ct;
                return Task.CompletedTask;
            }
            using var cts = new CancellationTokenSource();
            var vm = new MainWindowViewModel(service, cts.Token, TestActivity.New(), TimeProvider.System, StartAction);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await vm.StartDaemonCommand.Execute().ToTask();

            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(seen).IsEqualTo(cts.Token);
            await Assert.That(service.StartDaemonCallCount).IsEqualTo(0);
        });
    }

    // Navigation: the full surface-swap/teardown matrix lives in WorkspaceNavigationTests;
    // these two pin what the VM's own nullable-default seams promise.
    /// Every caller that predates workspaces passes no factory — and must keep landing on the
    /// tabbed shell rather than a half-built workspace or a throw.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_workspace_factory_the_window_stays_on_the_tabbed_shell() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);

            vm.OpenSession("0123456789abcdef0123456789abcdef");
            vm.OpenSessionIfCurrent("0123456789abcdef0123456789abcdef", vm.NavigationGeneration);

            await Assert.That(vm.CurrentWorkspace).IsNull();
        });
    }

    /// The gate is app-lifetime, not per-window: MainWindowCoordinator can build a second window
    /// over the same composition, and a launch captured in the first must read as stale in the
    /// second — which only holds while both read ONE generation.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_navigation_gate_is_shared_across_the_windows_built_over_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var gate = new NavigationGate();
            MainWindowViewModel Build() => new(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System, navigation: gate);

            var first = Build();
            var second = Build();
            var captured = first.NavigationGeneration;

            first.CloseWorkspace(); // close-to-hide in the first window

            await Assert.That(second.NavigationGeneration).IsEqualTo(first.NavigationGeneration);
            await Assert.That(second.NavigationGeneration).IsNotEqualTo(captured);

            first.LatchShutdown();
            await Assert.That(gate.ShutdownLatched).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Deactivation_disposes_subscriptions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);

            var activation = vm.Activator.Activate();
            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", active: 1, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await Assert.That(vm.DaemonName).IsEqualTo("daemon-a");
            await Assert.That(vm.AgentCountText).IsEqualTo("1 of 5 agents");

            activation.Dispose(); // window close

            var daemonNameAtDeactivation = vm.DaemonName;
            var agentCountAtDeactivation = vm.AgentCountText;
            var stateAtDeactivation      = vm.State;

            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-b", active: 3, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.DaemonName).IsEqualTo(daemonNameAtDeactivation);
            await Assert.That(vm.AgentCountText).IsEqualTo(agentCountAtDeactivation);
            await Assert.That(vm.State).IsEqualTo(stateAtDeactivation);
        });
    }

    // ---- Shell view state and rail wiring (spec: Home/Sessions surfaces, orthogonal to
    // CurrentWorkspace, and the rail's SelectedAgentId tracking the open workspace) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenSession_switches_to_sessions_view_and_reopening_the_same_id_is_a_noop() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var built = 0;
            var vm = NewVm(service,
                workspaceFactory: id => { built++; return NewWorkspace(service, id); });

            await Assert.That(vm.IsSessionsView).IsTrue(); // Sessions is the boot surface now
            vm.OpenSession("a1");
            await Assert.That(vm.IsSessionsView).IsTrue();
            await Assert.That(built).IsEqualTo(1);

            vm.OpenSession("a1"); // same id: no teardown/rebuild of a live attach
            await Assert.That(built).IsEqualTo(1);

            vm.OpenSession("a2"); // different id still swaps
            await Assert.That(built).IsEqualTo(2);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task View_commands_swap_surfaces_and_close_keeps_sessions_view() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id));
            vm.OpenSession("a1");
            vm.CloseWorkspace();
            await Assert.That(vm.CurrentWorkspace).IsNull();
            await Assert.That(vm.IsSessionsView).IsTrue(); // placeholder pane, not Home

            vm.ShowHomeCommand.Execute().Subscribe();
            await Assert.That(vm.IsHomeView).IsTrue();
            vm.ShowSessionsCommand.Execute().Subscribe();
            await Assert.That(vm.IsSessionsView).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_selection_follows_the_workspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            service.Agents.AddOrUpdate(new AgentStatusDto(
                "a1", "agent", "claude", "/dev/alpha", "Running", null, null, null, DateTime.UtcNow, null, null));
            var directory = new AgentDirectory(
                service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null), p => p, null, null,
                TimeProvider.System);
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, p => p);
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), rail: rail);

            vm.OpenSession("a1");
            await Assert.That(rail.SelectedAgentId).IsEqualTo("a1");
            await Assert.That(rail.Repos[0].Worktrees[0].IsExpanded).IsTrue(); // NotifySessionOpened ran

            vm.CloseWorkspace();
            await Assert.That(rail.SelectedAgentId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_a_remote_row_swaps_in_the_remote_host_and_close_tears_it_down() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var torn = 0;
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: id => id == "r1" ? AgentOrigin.Remote : AgentOrigin.Local,
                remoteWorkspaceFactory: id => built = host.New(id),
                trackWorkspaceTeardown: teardown => { torn++; _ = teardown(); });

            vm.OpenSession("r1");
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built);

            vm.OpenSession("r1"); // same id: no rebuild of the open host
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built);

            vm.CloseWorkspace();
            await Assert.That(vm.CurrentWorkspace).IsNull();
            await Assert.That(torn).IsEqualTo(1);
        });
    }

    /// The origin decides the workspace, not the presence of a remote factory: a local id still
    /// gets the local one even while a remote factory is wired.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_row_still_opens_the_local_workspace_while_a_remote_factory_is_wired() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var remoteBuilt = 0;
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local,
                remoteWorkspaceFactory: id => { remoteBuilt++; return host.New(id); });

            vm.OpenSession("a1");

            await Assert.That(vm.CurrentWorkspace).IsTypeOf<WorkspaceViewModel>();
            await Assert.That(remoteBuilt).IsEqualTo(0);
        });
    }

    /// The origin lookup and the factory are two reads of a cache a background recompute mutates:
    /// a row that vanishes between them yields no host, and the click must open nothing rather
    /// than fall through to the local workspace.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_whose_host_cannot_be_built_opens_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var localBuilt = 0;
            var vm = NewVm(service,
                workspaceFactory: id => { localBuilt++; return NewWorkspace(service, id); },
                originOf: _ => AgentOrigin.Remote,
                remoteWorkspaceFactory: _ => null);

            vm.OpenSession("r1");

            await Assert.That(vm.CurrentWorkspace).IsNull();
            await Assert.That(localBuilt).IsEqualTo(0);
        });
    }

    /// An unproven twin pair keeps a row on each lane under one id, and the id alone resolves
    /// local: the rail's remote row says which lane it is, and the click must honour that — and
    /// then the local row's click must still swap back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_explicit_origin_wins_over_the_id_lookup_for_a_row_on_both_lanes() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local,
                remoteWorkspaceFactory: host.New,
                trackWorkspaceTeardown: teardown => _ = teardown());

            vm.OpenSession("a1", AgentOrigin.Remote);
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<RemoteSessionViewModel>();

            vm.OpenSession("a1", AgentOrigin.Local);
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<WorkspaceViewModel>();
        });
    }

    /// The directory's own twin verdict plus one session id across both rows prove the move;
    /// the window then swaps to the local workspace on its own, keeping the tab in use.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_remote_host_whose_row_moved_to_this_machine_becomes_the_local_workspace_on_its_own() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);

            vm.OpenSession("r1");
            var remote = (RemoteSessionViewModel)vm.CurrentWorkspace!;
            await remote.ShowTerminalCommand.Execute().ToTask();
            await Assert.That(remote.IsTerminalActive).IsTrue();

            host.Directory.ProvenTwins.Add("r1");
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("r1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            host.Directory.Rows.Remove("remote:r1");

            await Assert.That(remote.OriginChangedToLocal).IsTrue();
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<WorkspaceViewModel>();
            await Assert.That(((WorkspaceViewModel)vm.CurrentWorkspace!).AgentId).IsEqualTo("r1");
            await Assert.That(((WorkspaceViewModel)vm.CurrentWorkspace!).IsTerminalActive).IsTrue();
        });
    }

    /// A lane change is background-triggered: only the user's own click navigates. A shell yanked
    /// to Sessions by a row moving machines takes the surface they were reading with it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rebind_swaps_the_workspace_without_moving_the_shell_off_the_view_in_use() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);

            vm.OpenSession("r1");
            await vm.ShowHomeCommand.Execute().ToTask();
            await Assert.That(vm.CurrentView).IsEqualTo(ShellView.Home);

            host.Directory.ProvenTwins.Add("r1");
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("r1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            host.Directory.Rows.Remove("remote:r1");

            await Assert.That(vm.CurrentWorkspace).IsTypeOf<WorkspaceViewModel>();
            await Assert.That(vm.CurrentView).IsEqualTo(ShellView.Home);
        });
    }

    /// The carried-over tab is only ever one this agent has: the remote row guesses the terminal
    /// from the vendor family, while the local dto carries the daemon's own answer — and a blank
    /// pane is what that disagreement would otherwise leave in front.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rebound_workspace_keeps_the_terminal_tab_only_while_its_agent_has_one(bool hasTerminal) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);

            vm.OpenSession("r1");
            await ((RemoteSessionViewModel)vm.CurrentWorkspace!).ShowTerminalCommand.Execute().ToTask();

            host.Directory.ProvenTwins.Add("r1");
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("r1", "claude", hasTerminal, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            host.Directory.Rows.Remove("remote:r1");

            var local = (WorkspaceViewModel)vm.CurrentWorkspace!;
            await Assert.That(local.IsTerminalActive).IsTrue(); // no dto yet: nothing has said otherwise

            service.Agents.AddOrUpdate(WorkspaceFixtures.Agent("r1", "claude", hasTerminal, "/repos/kcap-cli", sessionId: "s1"));

            await Assert.That(local.IsTerminalActive).IsEqualTo(hasTerminal);
            await Assert.That(local.IsChatActive).IsEqualTo(!hasTerminal);
        });
    }

    /// The daemon dropping a row is not the end of the session while the server still shows the
    /// same agent live under the user's other daemon: the workspace follows it there.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_local_workspace_whose_row_the_daemon_dropped_becomes_the_remote_host_while_the_server_shows_it_live() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: id => host.Directory.Rows.Lookup($"local:{id}").HasValue ? AgentOrigin.Local
                    : host.Directory.Rows.Lookup($"remote:{id}").HasValue ? AgentOrigin.Remote : null,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));

            vm.OpenSession("a1");
            var local = (WorkspaceViewModel)vm.CurrentWorkspace!;
            await local.ShowTerminalCommand.Execute().ToTask();

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            }));
            host.Directory.Rows.Remove("local:a1");

            await Assert.That(vm.CurrentWorkspace).IsTypeOf<RemoteSessionViewModel>();
            await Assert.That(((RemoteSessionViewModel)vm.CurrentWorkspace!).IsTerminalActive).IsTrue();
        });
    }

    /// A shared agent id is no evidence on its own — the dedup fails open — so a server row for
    /// another session is another agent, and the dropped row reads as the ended session it is.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dropped_local_row_with_a_same_id_remote_row_for_another_session_keeps_the_local_workspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: id => host.Directory.Rows.Lookup($"local:{id}").HasValue ? AgentOrigin.Local
                    : host.Directory.Rows.Lookup($"remote:{id}").HasValue ? AgentOrigin.Remote : null,
                remoteWorkspaceFactory: id => host.New(id, "a-different-session"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));

            vm.OpenSession("a1");
            var local = vm.CurrentWorkspace;

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "a-different-session", Status = "Running", DaemonName = "work-mac",
                OwnerUserId = "u1", Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            }));
            host.Directory.Rows.Remove("local:a1");

            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(local);
        });
    }

    /// With no live server row, a dropped local row is what it always was: the session ended,
    /// and the workspace stays to say so.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dropped_local_row_with_no_server_row_keeps_the_local_workspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            vm.OpenSession("a1");
            var local = vm.CurrentWorkspace;

            host.Directory.Rows.Remove("local:a1");

            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(local);
        });
    }

    /// An id on neither lane is not a local id: opening the local workspace for it would attach a
    /// terminal to an agent this machine never ran.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_id_with_no_origin_opens_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var localBuilt = 0;
            var remoteBuilt = 0;
            var vm = NewVm(service,
                workspaceFactory: id => { localBuilt++; return NewWorkspace(service, id); },
                originOf: _ => null,
                remoteWorkspaceFactory: id => { remoteBuilt++; return host.New(id); });

            vm.OpenSession("gone");

            await Assert.That(vm.CurrentWorkspace).IsNull();
            await Assert.That(localBuilt).IsEqualTo(0);
            await Assert.That(remoteBuilt).IsEqualTo(0);
        });
    }

    /// The latch means quiesce is already running: a lane change landing after it must open no
    /// workspace at all, the same refusal a click gets.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_row_changing_lanes_after_the_shutdown_latch_opens_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            vm.OpenSession("a1");
            vm.LatchShutdown();

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            }));
            host.Directory.Rows.Remove("local:a1");

            await Assert.That(vm.CurrentWorkspace).IsNull();
        });
    }

    /// The registry can list the twin before it knows its session id: the proof completes when
    /// the id arrives, and the workspace follows then.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dropped_local_row_follows_its_twin_once_the_twins_session_id_arrives() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: id => host.Directory.Rows.Lookup($"local:{id}").HasValue ? AgentOrigin.Local
                    : host.Directory.Rows.Lookup($"remote:{id}").HasValue ? AgentOrigin.Remote : null,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));

            vm.OpenSession("a1");
            var local = vm.CurrentWorkspace;

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = null, Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            }));
            host.Directory.Rows.Remove("local:a1");
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(local);

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            }));
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<RemoteSessionViewModel>();
        });
    }

    /// A proof the host cannot act on — the twin is gone again by the time its revision is
    /// handled — must not spend the watch: the next proof still rebinds.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rebind_the_remote_host_cannot_be_built_for_keeps_watching() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var builds = 0;
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: id => host.Directory.Rows.Lookup($"local:{id}").HasValue ? AgentOrigin.Local
                    : host.Directory.Rows.Lookup($"remote:{id}").HasValue ? AgentOrigin.Remote : null,
                remoteWorkspaceFactory: id => ++builds == 1 ? null : host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));

            vm.OpenSession("a1");
            var local = vm.CurrentWorkspace;

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            }));
            host.Directory.Rows.Remove("local:a1");
            await Assert.That(builds).IsEqualTo(1);
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(local);

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", PrTitle = "renamed", RegisteredAt = DateTime.UtcNow,
            }));
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<RemoteSessionViewModel>();
        });
    }

    /// The rail footer's two halves part company: Documentation opens through the link policy with
    /// no feedback action in sight, while the report items are inert until one exists.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Footer_help_commands_follow_the_feedback_action() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var opened = new List<FeedbackCategory>();
            var opener = new RecordingOpener();
            var without = NewVm(new FakeDaemonClientService());
            var with = NewVm(new FakeDaemonClientService(), openFeedback: opened.Add, opener: opener);

            await Assert.That(without.CanOpenFeedback).IsFalse();
            await Assert.That(with.CanOpenFeedback).IsTrue();
            await with.OpenFeedbackCommand.Execute(FeedbackCategory.Feedback).ToTask();
            await with.OpenDocsCommand.Execute().ToTask();
            await Assert.That(opened).IsEquivalentTo([FeedbackCategory.Feedback]);
            await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.DocsUrl]);
        });
    }
}
