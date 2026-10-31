using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.PullRequests;
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
    // Real AppNotifier: these tests do not exercise toasts, but AgentActionService needs one.
    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service) {
        var notifier = new AppNotifier();
        var actions = new AgentActionService(new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        return (actions, notifier);
    }

    sealed class UnusedLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, "unexpected launch"));
    }

    /// The view-state/rail-wiring tests' standard construction: a VM over the fake service, with
    /// an optional workspace factory and rail.
    static MainWindowViewModel NewVm(
            FakeDaemonClientService service, Func<string, WorkspaceViewModel>? workspaceFactory = null,
            SessionRailViewModel? rail = null, Func<string, AgentOrigin?>? originOf = null,
            Func<string, RemoteSessionViewModel?>? remoteWorkspaceFactory = null,
            Action<Func<Task>>? trackWorkspaceTeardown = null, IAgentDirectory? directory = null,
            Action<FeedbackCategory>? openFeedback = null, IUrlOpener? opener = null,
            Func<string, RemoteSessionViewModel?>? serverWorkspaceFactory = null, TimeProvider? time = null,
            IServerSessionsService? sessions = null) {
        return new MainWindowViewModel(
            service, CancellationToken.None, TestActivity.New(), time ?? TimeProvider.System,
            trackWorkspaceTeardown: trackWorkspaceTeardown,
            workspaceFactory: workspaceFactory, rail: rail,
            originOf: originOf, remoteWorkspaceFactory: remoteWorkspaceFactory, directory: directory,
            openFeedback: openFeedback, opener: opener, serverWorkspaceFactory: serverWorkspaceFactory, sessions: sessions);
    }

    /// A real WorkspaceViewModel over the fake service and scripted attach/surface fakes — same
    /// pieces WorkspaceNavigationTests.NewNav wires, just without its Nav bookkeeping.
    /// A window wired to a launcher pane: the Start/Reconnect buttons live on the pane, so their
    /// visibility is read where LauncherPaneView binds it.
    static (MainWindowViewModel Vm, HomeViewModel Home, TempDir Tmp) WithLauncher(
            FakeDaemonClientService service, IObservable<bool>? movingToWindowsService = null) {
        var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var home = new HomeViewModel(
            service, new AppStateStore(path), new UnusedLaunchClient(),
            () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, home: home,
            movingToWindowsService: movingToWindowsService);
        return (vm, home, tmp);
    }

    static WorkspaceViewModel NewWorkspace(FakeDaemonClientService service, string agentId, IPullRequestSource? pullRequests = null, ComposerDrafts? drafts = null) {
        var (actions, _) = NewActions(service);
        var attach = new FakeTerminalAttachClientFactory();
        return new WorkspaceViewModel(
            agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(),
            new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: pullRequests, drafts: drafts);
    }

    static HistoryImportRun ImportRun(int expected, int passes) =>
        new(expected, 2, "from the last 90 days", passes, action => action());

    static ImportRequest Pass() => new(ImportScopeChoice.Repo, null, [], ["a/b"]);

    /// After setup closes, the main window keeps counting against the total setup showed, and its bar moves as sessions settle.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_running_import_keeps_its_total_and_progress_after_setup() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var gate = new TaskCompletionSource<StreamingResult>();
            Action<StreamedLine>? emit = null;
            var run = ImportRun(94, 1);
            var vm = new MainWindowViewModel(
                new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System, historyImport: run);
            using var activation = vm.Activator.Activate();
            var shown = ProgressShown(vm);

            run.Start(new FakeKcapCli { ImportBehavior = (_, onLine, _) => { emit = onLine; return gate.Task; } }, [Pass()]);
            for (var i = 0; i < 46; i++) emit!(new StreamedLine(ProcessStreamKind.Stdout, $"  Loading s{i} (claude)"));

            await Assert.That(vm.HistoryImportStatus).IsEqualTo("Importing history · 46 of 94 sessions processed");
            await Assert.That(shown.LastOrDefault()).IsEqualTo(100.0 * 46 / 94);

            gate.SetResult(new StreamingResult(0, false, []));
            await run.Completion;
        });
    }

    /// With no total to count against, the bar moves when a pass completes, even if no session settled in it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_total_the_bar_follows_completed_passes() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var gate = new TaskCompletionSource<StreamingResult>();
            var run = ImportRun(0, 2);
            var vm = new MainWindowViewModel(
                new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System, historyImport: run);
            using var activation = vm.Activator.Activate();
            var shown = ProgressShown(vm);

            var calls = 0;
            run.Start(new FakeKcapCli {
                ImportBehavior = (_, _, _) => ++calls < 2 ? Task.FromResult(new StreamingResult(0, false, [])) : gate.Task,
            }, [Pass(), Pass()]);

            await Assert.That(shown.LastOrDefault()).IsEqualTo(50.0);

            gate.SetResult(new StreamingResult(0, false, []));
            await run.Completion;
        });
    }

    /// Every value the window announced for its import bar, in order.
    static List<double> ProgressShown(MainWindowViewModel vm) {
        var shown = new List<double>();
        vm.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(MainWindowViewModel.HistoryImportProgress)) shown.Add(vm.HistoryImportProgress);
        };
        return shown;
    }

    /// A remote launch opens on its placeholder, which the launcher's failure wording would sit
    /// behind; once the registry has listed the agent, a trailing failure report leaves it open.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_remote_launch_closes_only_a_workspace_still_on_its_placeholder(bool placeholder) {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var vm = NewVm(new FakeDaemonClientService(),
                remoteWorkspaceFactory: id => placeholder ? host.NewPlaceholder(id) : host.New(id),
                trackWorkspaceTeardown: teardown => _ = teardown(), directory: host.Directory);
            vm.OpenSession("a1", AgentOrigin.Remote);
            await Assert.That(vm.CurrentWorkspace).IsNotNull();

            vm.CloseFailedLaunch("a1");

            await Assert.That(vm.CurrentWorkspace is null).IsEqualTo(placeholder);
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task An_origin_handoff_keeps_the_explicit_pr_choice(bool startRemote) {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var source = new FakePullRequestSource(new FakeTimeProvider());
            var service = new FakeDaemonClientService();
            var dto = WorkspaceFixtures.Agent("a1", "claude", hasTerminal: false, "/repos/kcap-cli", sessionId: "s1");
            service.Agents.AddOrUpdate(dto);
            var localRow = AgentRow.FromLocal(dto, new RepoIdentity("path:/repos/kcap-cli", "kcap-cli"));
            if (!startRemote) host.Directory.Rows.AddOrUpdate(localRow);
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id, source),
                originOf: _ => startRemote ? AgentOrigin.Remote : AgentOrigin.Local,
                remoteWorkspaceFactory: id => host.New(id, "s1", source),
                trackWorkspaceTeardown: teardown => _ = teardown(), directory: host.Directory);
            try {
                vm.OpenSessionPullRequest("a1", startRemote ? AgentOrigin.Remote : AgentOrigin.Local, PullRequestWire.Subject(source.Links[0]));
                var before = vm.CurrentWorkspace switch {
                    WorkspaceViewModel local => local.PullRequests!,
                    RemoteSessionViewModel remote => remote.PullRequests!,
                    _ => throw new InvalidOperationException(),
                };
                before.SetForeground(true);
                await WorkspaceFixtures.WaitUntilAsync(() => before.CanReveal && !before.IsReading, what: "chosen PR");
                await Assert.That(before.Selected!.Subject.Number).IsEqualTo(1);
                if (startRemote) {
                    host.Directory.ProvenTwins.Add("a1");
                    host.Directory.Rows.AddOrUpdate(localRow);
                    host.Directory.Rows.Remove("remote:a1");
                } else {
                    host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                        AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "claude",
                    }));
                    host.Directory.Rows.Remove("local:a1");
                }
                var after = vm.CurrentWorkspace switch {
                    WorkspaceViewModel local when startRemote && local.IsPullRequestActive => local.PullRequests!,
                    RemoteSessionViewModel remote when !startRemote && remote.IsPullRequestActive => remote.PullRequests!,
                    _ => throw new InvalidOperationException("Handoff did not preserve the PR tab"),
                };
                after.SetForeground(true);
                await WorkspaceFixtures.WaitUntilAsync(() => after.CanReveal && !after.IsReading, what: "PR after handoff");
                await Assert.That(after.Selected!.Subject.Number).IsEqualTo(1);
            } finally {
                var current = vm.CurrentWorkspace;
                vm.CloseWorkspace();
                if (current is not null) await current.TeardownAsync();
            }
        });
    }

    /// Pins that an unsent draft follows its session across lanes, and that the lane it left keeps
    /// no copy to resurrect on a later move back.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task An_origin_handoff_carries_the_unsent_draft(bool startRemote) {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var drafts = new ComposerDrafts();
            var service = new FakeDaemonClientService();
            var dto = WorkspaceFixtures.Agent("a1", "claude", hasTerminal: false, "/repos/kcap-cli", sessionId: "s1");
            service.Agents.AddOrUpdate(dto);
            var localRow = AgentRow.FromLocal(dto, new RepoIdentity("path:/repos/kcap-cli", "kcap-cli"));
            if (!startRemote) host.Directory.Rows.AddOrUpdate(localRow);
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id, drafts: drafts),
                originOf: _ => startRemote ? AgentOrigin.Remote : AgentOrigin.Local,
                remoteWorkspaceFactory: id => host.New(id, "s1", drafts: drafts),
                trackWorkspaceTeardown: teardown => _ = teardown(), directory: host.Directory);
            try {
                vm.OpenSession("a1", startRemote ? AgentOrigin.Remote : AgentOrigin.Local);
                var before = vm.CurrentWorkspace!;
                static ChatTabViewModel? ChatOf(ISessionWorkspace? workspace) => workspace switch {
                    WorkspaceViewModel local => local.Chat,
                    RemoteSessionViewModel remote => remote.Chat,
                    _ => null,
                };
                await WorkspaceFixtures.WaitUntilAsync(() => ChatOf(before) is not null, what: "chat tab");
                ChatOf(before)!.ComposerText = "half a thought";

                if (startRemote) {
                    host.Directory.ProvenTwins.Add("a1");
                    host.Directory.Rows.AddOrUpdate(localRow);
                    host.Directory.Rows.Remove("remote:a1");
                } else {
                    host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                        AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "claude",
                    }));
                    host.Directory.Rows.Remove("local:a1");
                }

                await WorkspaceFixtures.WaitUntilAsync(() => !ReferenceEquals(vm.CurrentWorkspace, before) && ChatOf(vm.CurrentWorkspace) is not null,
                    what: "rebound chat tab");
                await Assert.That(ChatOf(vm.CurrentWorkspace)!.ComposerText).IsEqualTo("half a thought");
                var left = startRemote ? AgentOrigin.Remote : AgentOrigin.Local;
                await Assert.That(drafts.Get(left, "a1")).IsEqualTo("");
            } finally {
                var current = vm.CurrentWorkspace;
                vm.CloseWorkspace();
                if (current is not null) await current.TeardownAsync();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Projections_follow_the_snapshot() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999", connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await Assert.That(vm.DaemonName).IsEqualTo("daemon-a");
            await Assert.That(vm.ServerUrl).IsEqualTo("http://localhost:9999");
            await Assert.That(vm.Alert).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Restart_pending_raises_an_alert_while_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var restartPending = new BehaviorSubject<bool>(false);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, restartPending: restartPending);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap());
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.Alert).IsNull();

            restartPending.OnNext(true);

            await Assert.That(vm.Alert!.Text).IsEqualTo(MainWindowViewModel.RestartPendingText);
            await Assert.That(vm.Alert.Detail).IsEqualTo(MainWindowViewModel.RestartPendingMessage);

            restartPending.OnNext(false);

            await Assert.That(vm.Alert).IsNull();
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

            await Assert.That(vm.Alert!.Text).IsEqualTo(MainWindowViewModel.DaemonStoppedText);
        });
    }

    // ---- StripBuildMetadata (SEMVER only, everything from the first '+' is build metadata) ----

    [Test]
    [Arguments("1.2.3+abc", "1.2.3")]
    [Arguments("1.2.3", "1.2.3")]
    [Arguments("1.2.3+a.b+c", "1.2.3")] // only the FIRST '+' matters
    [Arguments("", "")]
    [Arguments(null, "")]
    public async Task StripBuildMetadata_keeps_the_semver(string? raw, string expected) {
        await Assert.That(MainWindowViewModel.StripBuildMetadata(raw)).IsEqualTo(expected);
    }

    /// A healthy stack shows nothing; every other state names what is wrong, and offers the one
    /// action that repairs it where there is one.
    [Test]
    [Arguments(AttachState.Connected, null, "connected", null, RailRecovery.None)]
    [Arguments(AttachState.Connected, null, "", null, RailRecovery.None)]
    [Arguments(AttachState.Connecting, null, "connected", MainWindowViewModel.DaemonConnectingText, RailRecovery.None)]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", MainWindowViewModel.DaemonStoppedText, RailRecovery.StartDaemon)]
    [Arguments(AttachState.Unreachable, "daemon_incompatible", "connected", MainWindowViewModel.DaemonMismatchText, RailRecovery.Reconnect)]
    [Arguments(AttachState.Unreachable, ExpectedDaemonRestart.Reason, "connected", MainWindowViewModel.DaemonRestartingText, RailRecovery.None)]
    [Arguments(AttachState.Unreachable, "socket_refused", "connected", MainWindowViewModel.DaemonUnreachableText, RailRecovery.Reconnect)]
    [Arguments(AttachState.Connected, null, "connecting", MainWindowViewModel.ServerConnectingText, RailRecovery.None)]
    [Arguments(AttachState.Connected, null, "reconnecting", MainWindowViewModel.ReconnectingText, RailRecovery.None)]
    [Arguments(AttachState.Connected, null, "disconnected", MainWindowViewModel.ServerLostText, RailRecovery.None)]
    public async Task AlertFor_is_silent_when_healthy_and_specific_otherwise(
            AttachState state, string? reason, string daemonConnection, string? text, RailRecovery recovery) {
        var alert = MainWindowViewModel.AlertFor(new AttachStatus(state, reason, null), daemonConnection);
        await Assert.That(alert?.Text).IsEqualTo(text);
        await Assert.That(alert?.Recovery ?? RailRecovery.None).IsEqualTo(recovery);
    }

    /// Waiting on the daemon hub will not restore an expired sign-in, so it wins whatever the attach
    /// says, and offers Sign in only where the app's sign-in repairs it.
    [Test]
    [Arguments(AttachState.Connected, null, "reconnecting", true)]
    [Arguments(AttachState.Connected, null, "connected", false)]
    [Arguments(AttachState.Connecting, null, "connected", true)]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", false)]
    public async Task AlertFor_sign_in_required_wins_over_attach_and_daemon_connection(
            AttachState state, string? reason, string daemonConnection, bool canSignIn) {
        var alert = MainWindowViewModel.AlertFor(new AttachStatus(state, reason, null), daemonConnection,
            signedOut: true, canSignIn: canSignIn);
        await Assert.That(alert!.Text).IsEqualTo(MainWindowViewModel.SignInRequiredText);
        await Assert.That(alert.Recovery).IsEqualTo(canSignIn ? RailRecovery.SignIn : RailRecovery.None);
        await Assert.That(alert.ActionLabel).IsEqualTo(canSignIn ? "Sign in" : null);
    }

    /// The move to the Windows service brings the daemon back itself, so Start is not offered.
    [Test]
    public async Task AlertFor_offers_no_start_while_the_daemon_moves_to_the_windows_service() {
        var alert = MainWindowViewModel.AlertFor(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null), "",
            moving: true);
        await Assert.That(alert!.Text).IsEqualTo(MainWindowViewModel.DaemonRestartingText);
        await Assert.That(alert.Recovery).IsEqualTo(RailRecovery.None);
    }

    /// A stopped local server explains a daemon that cannot reach it, so it outranks the daemon's
    /// reconnecting word; a daemon that is itself down outranks both.
    [Test]
    public async Task AlertFor_puts_the_local_server_between_the_attach_and_the_daemon_connection() {
        var local = MainWindowViewModel.LocalServerAlertFor(updateFailed: false, starting: false, stopped: true, canStart: true);
        var connected = new AttachStatus(AttachState.Connected, null, null);

        await Assert.That(MainWindowViewModel.AlertFor(connected, "reconnecting", local: local)).IsEqualTo(local);
        await Assert.That(MainWindowViewModel.AlertFor(
                new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null), "reconnecting", local: local)!.Text)
            .IsEqualTo(MainWindowViewModel.DaemonStoppedText);
    }

    [Test]
    [Arguments(false, false, false, true, null, RailRecovery.None)]
    [Arguments(false, false, true, true, MainWindowViewModel.LocalStoppedText, RailRecovery.StartLocalServer)]
    [Arguments(false, false, true, false, MainWindowViewModel.LocalStoppedText, RailRecovery.None)]
    [Arguments(false, true, true, false, MainWindowViewModel.LocalStartingText, RailRecovery.None)]
    [Arguments(true, false, true, true, MainWindowViewModel.LocalUpdateFailedText, RailRecovery.None)]
    public async Task LocalServerAlertFor_names_a_stopped_server_and_offers_start_when_the_launcher_does(
            bool updateFailed, bool starting, bool stopped, bool canStart, string? text, RailRecovery recovery) {
        var alert = MainWindowViewModel.LocalServerAlertFor(updateFailed, starting, stopped, canStart);
        await Assert.That(alert?.Text).IsEqualTo(text);
        await Assert.That(alert?.Recovery ?? RailRecovery.None).IsEqualTo(recovery);
    }

    /// A stopped server whose installation is gone, is being changed by another kcap, or whose port another program
    /// holds never carries Start, and its hover says why.
    [Test]
    [Arguments(LocalServerCondition.Changing, MainWindowViewModel.LocalChangingText)]
    [Arguments(LocalServerCondition.Uninstalled, MainWindowViewModel.LocalRemovedText)]
    [Arguments(LocalServerCondition.Purged, MainWindowViewModel.LocalRemovedText)]
    [Arguments(LocalServerCondition.NotInstalled, MainWindowViewModel.LocalRemovedText)]
    [Arguments(LocalServerCondition.FilesMissing, MainWindowViewModel.LocalNeedsAttentionText)]
    [Arguments(LocalServerCondition.Interrupted, MainWindowViewModel.LocalNeedsAttentionText)]
    [Arguments(LocalServerCondition.PortTaken, MainWindowViewModel.LocalPortTakenText)]
    public async Task LocalServerAlertFor_names_a_gone_or_changing_installation_without_start(LocalServerCondition condition, string text) {
        var alert = MainWindowViewModel.LocalServerAlertFor(updateFailed: false, starting: false, stopped: true, canStart: true, condition);

        await Assert.That(alert!.Text).IsEqualTo(text);
        await Assert.That(alert.Recovery).IsEqualTo(RailRecovery.None);
        await Assert.That(alert.Detail).IsEqualTo(condition.Explanation);
    }

    /// The lowest rungs: a queued daemon restart, then the lane's diagnostic, each with its long copy on hover.
    [Test]
    public async Task AlertFor_restart_pending_and_lane_diagnostic_only_when_nothing_else_is_wrong() {
        var connected = new AttachStatus(AttachState.Connected, null, null);

        var pending = MainWindowViewModel.AlertFor(connected, "connected", restartPending: true, laneDiagnostic: "marker");
        await Assert.That(pending!.Text).IsEqualTo(MainWindowViewModel.RestartPendingText);
        await Assert.That(pending.Detail).IsEqualTo(MainWindowViewModel.RestartPendingMessage);

        var diagnostic = MainWindowViewModel.AlertFor(connected, "connected", laneDiagnostic: "marker");
        await Assert.That(diagnostic!.Text).IsEqualTo(MainWindowViewModel.LiveUpdatesText);
        await Assert.That(diagnostic.Detail).IsEqualTo("marker");

        await Assert.That(MainWindowViewModel.AlertFor(connected, "reconnecting", restartPending: true)!.Text)
            .IsEqualTo(MainWindowViewModel.ReconnectingText);
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

    /// The alert takes the launcher card's tone: grey while in progress, amber when it needs the user, red once broken.
    [Test]
    [Arguments(AttachState.Connecting, null, "connected", false, StatusColors.Pending)]
    [Arguments(AttachState.Unreachable, "daemon_restarting", "connected", false, StatusColors.Pending)]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", false, StatusColors.InProgress)]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", true, StatusColors.Disrupted)]
    [Arguments(AttachState.Unreachable, "daemon_incompatible", "connected", false, StatusColors.Disrupted)]
    [Arguments(AttachState.Connected, null, "connecting", false, StatusColors.Pending)]
    [Arguments(AttachState.Connected, null, "reconnecting", false, StatusColors.Pending)]
    [Arguments(AttachState.Connected, null, "disconnected", false, StatusColors.InProgress)]
    public async Task AlertFor_matches_the_launcher_card_tone(
            AttachState state, string? reason, string daemonConnection, bool failed, string expectedHex) {
        var alert = MainWindowViewModel.AlertFor(new AttachStatus(state, reason, null), daemonConnection, failed: failed);
        await Assert.That(alert!.Tone).IsEqualTo(expectedHex);
    }

    /// An expired sign-in needs the user; only a local server that refused the app, which the card reports as a failure, is red.
    [Test]
    [Arguments(false, StatusColors.InProgress)]
    [Arguments(true, StatusColors.Disrupted)]
    public async Task AlertFor_signed_out_follows_the_card(bool failed, string expectedHex) {
        var status = new AttachStatus(AttachState.Connected, null, null);
        var alert = MainWindowViewModel.AlertFor(status, "reconnecting", signedOut: true, failed: failed);
        await Assert.That(alert!.Tone).IsEqualTo(expectedHex);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_daemon_failure_stays_red_when_a_healthy_remote_machine_is_selected(bool remoteFirst) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var service = new FakeDaemonClientService();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var home = new HomeViewModel(service, new AppStateStore(path), new UnusedLaunchClient(),
                () => Task.FromResult(Array.Empty<string>()), TimeProvider.System, daemons: remote.Daemons,
                viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);
            using var report = new BehaviorSubject<string?>(null);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                home: home, laneStatus: lane.Status, lifecycleAttention: report);
            using var activation = vm.Activator.Activate();
            const string failed = "The daemon didn't start. Press Start daemon to try again.";
            if (!remoteFirst) {
                report.OnNext(failed);
                await Assert.That(((ISolidColorBrush)vm.StatusBrush).Color).IsEqualTo(Color.Parse("#FF7272"));
            }
            await home.SelectMachineAsync("home-pc", isLocal: false);
            if (remoteFirst) report.OnNext(failed);

            await Assert.That(home.BannerFailed).IsFalse();
            await Assert.That(vm.Alert!.Text).IsEqualTo(MainWindowViewModel.DaemonStoppedText);
            await Assert.That(((ISolidColorBrush)vm.StatusBrush).Color).IsEqualTo(Color.Parse("#FF7272"));

            report.OnNext(null);
            await Assert.That(((ISolidColorBrush)vm.StatusBrush).Color).IsEqualTo(Color.Parse("#F4B860"));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sign_in_required_wins_over_daemon_reconnecting() {
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
            await Assert.That(vm.Alert!.Text).IsEqualTo(MainWindowViewModel.ReconnectingText);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            await Assert.That(home.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);
            await Assert.That(vm.Alert!.Text).IsEqualTo(MainWindowViewModel.SignInRequiredText);
            // An expired sign-in needs the user, which is how the launcher's card shows it too.
            var brush = (ISolidColorBrush)vm.StatusBrush;
            await Assert.That(brush.Color).IsEqualTo(Color.Parse(StatusColors.InProgress));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SignInVisible_tracks_signed_out_only_when_a_sign_in_action_exists() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var without = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System, laneStatus: lane.Status);
            var with = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                laneStatus: lane.Status, requestSignIn: () => { });
            using var a = without.Activator.Activate();
            using var b = with.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            await Assert.That(without.SignInVisible).IsFalse();
            await Assert.That(with.SignInVisible).IsTrue();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(with.SignInVisible).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_lane_diagnostic_is_an_alert_with_its_text_on_hover() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System, laneStatus: lane.Status);
            using var activation = vm.Activator.Activate();
            service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await Assert.That(vm.Alert).IsNull();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Diagnostic: "diagnostic-marker"));
            await Assert.That(vm.Alert!.Text).IsEqualTo(MainWindowViewModel.LiveUpdatesText);
            await Assert.That(vm.Alert.Detail).IsEqualTo("diagnostic-marker");

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.Alert).IsNull();
        });
    }

    /// The alert's action runs the same command the launcher banner offers for that state.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_alert_action_is_the_command_that_repairs_the_state() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                laneStatus: lane.Status, requestSignIn: () => { });
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.AlertActionCommand).IsSameReferenceAs(vm.StartDaemonCommand);
            await Assert.That(vm.Alert!.ActionLabel).IsEqualTo("Start daemon");

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));
            await Assert.That(vm.AlertActionCommand).IsSameReferenceAs(vm.RetryCommand);

            service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.AlertActionCommand).IsNull();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            await Assert.That(vm.AlertActionCommand).IsSameReferenceAs(vm.SignInCommand);
        });
    }

    /// The local server's state reaches the rail only through the alert, and only while it needs the user.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stopped_local_server_is_an_alert_with_its_start() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var home = new HomeViewModel(service, new AppStateStore(path), new UnusedLaunchClient(),
                () => Task.FromResult(Array.Empty<string>()), TimeProvider.System, laneStatus: lane.Status);
            using var recovery = new LocalServerRecoveryViewModel(lane.Status, isLocalServer: true,
                _ => Task.FromResult(new ProcessResult(0, "", "", false)), () => Task.CompletedTask, CancellationToken.None);
            home.AttachLocalServerRecovery(recovery);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                home: home, laneStatus: lane.Status);
            using var activation = vm.Activator.Activate();
            service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.Alert).IsNull();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await Assert.That(vm.Alert!.Text).IsEqualTo(MainWindowViewModel.LocalStoppedText);
            await Assert.That(vm.AlertActionCommand).IsSameReferenceAs(recovery.StartCommand);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(vm.Alert).IsNull();
        });
    }

    // ---- Start/Reconnect visibility (one primary action: Start when unreachable-down;
    // Reconnect when connecting or skew — never both) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_and_reconnect_visibility_are_mutually_exclusive() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (vm, home, tmp) = WithLauncher(service);
            using var _tmp = tmp;
            using var _home = home;

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
            await Assert.That(home.DaemonStartVisible).IsFalse();
            await Assert.That(home.DaemonRetryVisible).IsTrue();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(home.DaemonStartVisible).IsFalse();
            await Assert.That(home.DaemonRetryVisible).IsFalse();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(home.DaemonStartVisible).IsTrue();
            await Assert.That(home.DaemonRetryVisible).IsFalse();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));
            await Assert.That(home.DaemonStartVisible).IsFalse();
            await Assert.That(home.DaemonRetryVisible).IsTrue();
        });
    }

    /// The move to the Windows service takes the daemon down and brings it back itself, so Start is not offered
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_is_not_offered_while_the_daemon_moves_to_the_windows_service() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var moving = new BehaviorSubject<bool>(true);
            var (vm, home, tmp) = WithLauncher(service, moving);
            using var _tmp = tmp;
            using var _home = home;

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(home.DaemonStartVisible).IsFalse();

            moving.OnNext(false);
            await Assert.That(home.DaemonStartVisible).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_start_button_stays_visible_while_a_start_is_in_flight_unlike_CanExecute() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (vm, home, tmp) = WithLauncher(service);
            using var _tmp = tmp;
            using var _home = home;

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
            await Assert.That(home.DaemonStartVisible).IsTrue();  // ...but the button itself stays visible

            gate.SetResult();
            await execute;
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Command_enablement_matrix() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
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
            var (vm, home, tmp) = WithLauncher(service);
            using var _tmp = tmp;
            using var _home = home;
            using var activation = vm.Activator.Activate();

            var startCanExecute = false;
            var retryCanExecute = false;
            using var subStart = vm.StartDaemonCommand.CanExecute.Subscribe(v => startCanExecute = v);
            using var subRetry = vm.RetryCommand.CanExecute.Subscribe(v => retryCanExecute = v);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));

            await Assert.That(vm.Reason).IsNotNull();
            await Assert.That(vm.Reason).IsEqualTo(HomeViewModel.DaemonIncompatibleNotice);
            await Assert.That(vm.Reason!).DoesNotContain("daemon_incompatible");
            await Assert.That(home.DaemonStartVisible).IsFalse();
            await Assert.That(home.DaemonRetryVisible).IsTrue();
            await Assert.That(startCanExecute).IsFalse();
            await Assert.That(retryCanExecute).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_message_lifecycle() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.Reason).IsEqualTo(HomeViewModel.DaemonDownNotice);
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
            await Assert.That(vm.StartMessage).IsEqualTo("Starting the daemon. Waiting for it to connect…");

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
            var (vm, home, tmp) = WithLauncher(service);
            using var _tmp = tmp;
            using var _home = home;
            using var activation = vm.Activator.Activate();

            // Reconnect is offered while connecting or skewed — not while Start owns the down case.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
            await Assert.That(vm.StartMessage).IsNull();
            await Assert.That(home.DaemonRetryVisible).IsTrue();

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

    /// A connected daemon never clears the banner, so a withdrawn line has to; a line another
    /// sender set since stays.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_withdrawn_lifecycle_line_clears_only_while_it_is_still_shown() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lifecycleStatus = new Subject<string?>();
            var lifecycleAttention = new Subject<string?>();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                lifecycleStatus: lifecycleStatus, lifecycleAttention: lifecycleAttention);
            using var activation = vm.Activator.Activate();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            lifecycleStatus.OnNext("Installing the command-line tool was canceled.");
            lifecycleStatus.OnNext(null);
            var withdrawn = vm.StartMessage;

            lifecycleStatus.OnNext("Installing the command-line tool was canceled.");
            lifecycleAttention.OnNext("The daemon didn't come up cleanly.");
            lifecycleStatus.OnNext(null);
            var replaced = vm.StartMessage;

            lifecycleStatus.OnNext("Installing the command-line tool failed.");
            lifecycleAttention.OnNext(null);
            var restored = vm.StartMessage;

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            lifecycleAttention.OnNext("The daemon didn't come up cleanly.");
            lifecycleAttention.OnNext(null);

            await Assert.That(withdrawn).IsNull();
            await Assert.That(replaced).IsEqualTo("The daemon didn't come up cleanly.");
            await Assert.That(restored).IsEqualTo("Installing the command-line tool failed.");
            await Assert.That(vm.StartMessage).IsNull(); // a connect cleared the status line, so nothing comes back
        });
    }

    /// A withdrawal falls back to the newest standing line: an identity report outranks a lane line posted before it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_withdrawal_after_an_identity_report_restores_the_report() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var lifecycleStatus = new Subject<string?>();
            var lifecycleAttention = new Subject<string?>();
            var identity = new Subject<string?>();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                lifecycleStatus: lifecycleStatus, lifecycleAttention: lifecycleAttention, identityReport: identity);
            using var activation = vm.Activator.Activate();
            const string report = "Daemon daemon-a is running for another server.";

            lifecycleStatus.OnNext("Installing the command-line tool failed.");
            identity.OnNext(report);
            lifecycleAttention.OnNext("The daemon didn't come up cleanly.");
            lifecycleAttention.OnNext(null);

            await Assert.That(vm.StartMessage).IsEqualTo(report);
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

    /// A connect clears progress lines back to the standing identity report, in either order of arrival, and to
    /// nothing once the report is withdrawn.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_connect_resets_the_banner_to_the_standing_identity_report(bool reportFirst) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var identity = new System.Reactive.Subjects.BehaviorSubject<string?>(null);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                identityReport: identity);
            using var activation = vm.Activator.Activate();
            const string report = "Daemon daemon-a is running for another server.";

            if (reportFirst) identity.OnNext(report);
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            if (!reportFirst) identity.OnNext(report);
            await Assert.That(vm.StartMessage).IsEqualTo(report);

            // Withdrawn after the connect that restored it, as a replace's own reconnect does: the banner clears.
            identity.OnNext(null);
            await Assert.That(vm.StartMessage).IsNull();
        });
    }

    // Supplied startAction runs instead of StartDaemonAsync.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartDaemonCommand_invokes_the_supplied_startAction_instead_of_StartDaemonAsync() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
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
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);

            vm.OpenSession("0123456789abcdef0123456789abcdef");
            vm.OpenSessionIfCurrent("0123456789abcdef0123456789abcdef", AgentOrigin.Local, vm.NavigationGeneration);

            await Assert.That(vm.CurrentWorkspace).IsNull();
        });
    }

    /// Shutdown releases the live workspace before rail disposal; later latch and close callbacks
    /// must remain safe after that disposal.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Shutdown_latch_can_repeat_after_the_session_rail_is_disposed() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            var service = new FakeDaemonClientService();
            var tracked = 0;
            Task? teardown = null;
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), rail: rail,
                trackWorkspaceTeardown: start => { tracked++; teardown = start(); });
            vm.OpenSession("session");
            var workspace = vm.CurrentWorkspace;
            await Assert.That(workspace).IsNotNull();

            // First shutdown request, UI teardown, then the confirmed shutdown request.
            vm.LatchShutdown();
            await Assert.That(vm.CurrentWorkspace).IsNull();
            await Assert.That(rail.SelectedSessionKey).IsNull();
            await Assert.That(tracked).IsEqualTo(1);
            await teardown!;
            rail.Dispose();
            vm.LatchShutdown();
            // MainWindowCoordinator also releases the workspace from the final Closed event.
            vm.CloseWorkspace();

            await Assert.That(vm.CurrentWorkspace).IsNull();
            await Assert.That(tracked).IsEqualTo(1);
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
            var gate = new NavigationGate();
            using var directory = new FakeAgentDirectory();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            MainWindowViewModel Build() => new(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System, navigation: gate, rail: rail);

            var first = Build();
            var second = Build();
            var captured = first.NavigationGeneration;

            first.CloseWorkspace(); // close-to-hide in the first window

            await Assert.That(second.NavigationGeneration).IsEqualTo(first.NavigationGeneration);
            await Assert.That(second.NavigationGeneration).IsNotEqualTo(captured);

            first.LatchShutdown();
            await Assert.That(gate.ShutdownLatched).IsTrue();
            var shutdownGeneration = gate.Generation;
            rail.Dispose();
            second.CloseWorkspace(); // Late close without this window's local shutdown latch.
            await Assert.That(gate.Generation).IsEqualTo(shutdownGeneration);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Deactivation_disposes_subscriptions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);

            var activation = vm.Activator.Activate();
            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", active: 1, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await Assert.That(vm.DaemonName).IsEqualTo("daemon-a");
            await Assert.That(vm.Alert).IsNull();

            activation.Dispose(); // window close

            var daemonNameAtDeactivation = vm.DaemonName;
            var stateAtDeactivation      = vm.State;

            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-b", active: 3, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.DaemonName).IsEqualTo(daemonNameAtDeactivation);
            await Assert.That(vm.Alert).IsNull();
            await Assert.That(vm.State).IsEqualTo(stateAtDeactivation);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenSession_builds_the_workspace_and_reopening_the_same_id_is_a_noop() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var built = 0;
            var vm = NewVm(service,
                workspaceFactory: id => { built++; return NewWorkspace(service, id); });

            await Assert.That(vm.OpenSession("a1")).IsTrue();
            await Assert.That(built).IsEqualTo(1);

            await Assert.That(vm.OpenSession("a1")).IsTrue(); // same id: no teardown/rebuild of a live attach
            await Assert.That(built).IsEqualTo(1);

            vm.OpenSession("a2"); // different id still swaps
            await Assert.That(built).IsEqualTo(2);
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_or_reopening_a_session_reveals_only_its_repository(bool remote) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            const string id = "a1";
            var row = AgentRow.FromLocal(WorkspaceFixtures.Agent(id, "claude", false, "/repos/alpha"),
                new RepoIdentity("path:/repos/alpha", "alpha"));
            RemoteSessionViewModel? remoteWorkspace = null;
            if (remote) remoteWorkspace = host.New(id);
            else host.Directory.Rows.AddOrUpdate(row);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(WorkspaceFixtures.Agent("other", "claude", false, "/repos/beta"),
                new RepoIdentity("path:/repos/beta", "beta")));
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            var vm = NewVm(service, id => NewWorkspace(service, id), rail,
                originOf: _ => remote ? AgentOrigin.Remote : AgentOrigin.Local,
                remoteWorkspaceFactory: _ => remoteWorkspace, directory: host.Directory);
            foreach (var repo in rail.Groups) repo.ToggleCommand.Execute().Subscribe();
            var target = rail.Groups.Single(repo => repo.Sessions.Any(session => session.Id == id));
            var other = rail.Groups.Single(repo => repo != target);
            vm.OpenSessionIfCurrent(id, remote ? AgentOrigin.Remote : AgentOrigin.Local, vm.NavigationGeneration);
            await Assert.That(target.IsExpanded).IsTrue();
            await Assert.That(other.IsExpanded).IsFalse();
            await Assert.That(rail.SelectedSessionKey).IsEqualTo($"{(remote ? "remote" : "local")}:{id}");
            var workspace = vm.CurrentWorkspace;
            target.ToggleCommand.Execute().Subscribe();
            vm.OpenSession(id);
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(workspace);
            await Assert.That(target.IsExpanded).IsTrue();
            await Assert.That(other.IsExpanded).IsFalse();
            // Remember the navigation expansion if a refresh recreates the group.
            var current = host.Directory.Rows.Items.Single(r => r.Id == id);
            host.Directory.Rows.Remove(current.Key);
            host.Directory.Rows.AddOrUpdate(current);
            await Assert.That(rail.Groups.Single(repo => repo.Sessions.Any(session => session.Id == id)).IsExpanded).IsTrue();
            vm.CloseWorkspace();
            await workspace!.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenSession_reports_an_id_neither_lane_holds_as_not_opened() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), originOf: _ => null);

            await Assert.That(vm.OpenSession("a1")).IsFalse();
            await Assert.That(vm.CurrentWorkspace).IsNull();
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
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), rail: rail);

            vm.OpenSession("a1");
            await Assert.That(rail.SelectedSessionKey).IsEqualTo("local:a1");
            await Assert.That(rail.Groups[0].Sessions[0].IsSelected).IsTrue();

            vm.CloseWorkspace();
            await Assert.That(rail.SelectedSessionKey).IsNull();
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Refresh_work_command_refreshes_the_remote_workspace() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service, originOf: _ => AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "s1"));
            vm.OpenSession("r1");
            var work = vm.CurrentWorkspace!.WorkContext;
            await WorkspaceFixtures.WaitUntilAsync(() => host.WorkSource.Requested.Count == 1 && !work.IsReading,
                what: "remote work context loaded");
            await WorkspaceFixtures.WaitUntilAsync(() => ((System.Windows.Input.ICommand)vm.RefreshWorkCommand).CanExecute(null), what: "refresh enabled");
            await vm.RefreshWorkCommand.Execute().ToTask();
            await WorkspaceFixtures.WaitUntilAsync(() => host.WorkSource.Requested.Count == 2 && !work.IsReading,
                what: "remote work context refreshed");
            var workspace = vm.CurrentWorkspace;
            vm.CloseWorkspace();
            await workspace!.TeardownAsync();
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
            var remoteWorkspace = host.New("a1");
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", false, "/repos/kcap-cli"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            var sessions = rail.Groups.SelectMany(repo => repo.Sessions).ToList();
            var local = sessions.Single(row => !row.IsRemote);
            var remote = sessions.Single(row => row.IsRemote);
            var vm = NewVm(service,
                rail: rail,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local,
                remoteWorkspaceFactory: _ => remoteWorkspace,
                trackWorkspaceTeardown: teardown => _ = teardown());

            vm.OpenSession("a1", AgentOrigin.Remote);
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<RemoteSessionViewModel>();
            await Assert.That(remote.IsSelected).IsTrue();
            await Assert.That(local.IsSelected).IsFalse();

            vm.OpenSession("a1", AgentOrigin.Local);
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<WorkspaceViewModel>();
            await Assert.That(local.IsSelected).IsTrue();
            await Assert.That(remote.IsSelected).IsFalse();

            vm.CloseWorkspace();
            await Assert.That(sessions.Any(row => row.IsSelected)).IsFalse();
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
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("other", "claude", false, "/repos/kcap-cli"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            var vm = NewVm(service, rail: rail,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);

            vm.OpenSession("r1");
            foreach (var repo in rail.Groups) repo.ToggleCommand.Execute().Subscribe();
            var remote = (RemoteSessionViewModel)vm.CurrentWorkspace!;
            await remote.ShowTerminalCommand.Execute().ToTask();
            await Assert.That(remote.IsTerminalActive).IsTrue();

            host.Directory.ProvenTwins.Add("r1");
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("r1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            host.Directory.Rows.Remove("remote:r1");

            await Assert.That(rail.Groups.Any(repo => repo.IsExpanded)).IsFalse();
            await Assert.That(remote.OriginChangedToLocal).IsTrue();
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<WorkspaceViewModel>();
            await Assert.That(((WorkspaceViewModel)vm.CurrentWorkspace!).AgentId).IsEqualTo("r1");
            await Assert.That(((WorkspaceViewModel)vm.CurrentWorkspace!).IsTerminalActive).IsTrue();
            await Assert.That(rail.SelectedSessionKey).IsEqualTo("local:r1");
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("local:r1");
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
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            var vm = NewVm(service,
                rail: rail,
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
            await Assert.That(rail.SelectedSessionKey).IsEqualTo("remote:a1");
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("remote:a1");
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changelog_opens_through_the_link_policy() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var opener = new RecordingOpener();
            var vm = NewVm(new FakeDaemonClientService(), opener: opener);

            await vm.OpenChangelogCommand.Execute().ToTask();

            await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.ChangelogUrl]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Background_priority_shows_only_while_connected_and_the_failure_text_always() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var background = new BehaviorSubject<bool>(true);
            var reload = new BehaviorSubject<ReloadState?>(new ReloadState(ReloadOutcomeKind.Failed, "unit_missing", 1, "daemon-a", 1));
            var reloading = new BehaviorSubject<bool>(false);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                backgroundPriority: background, reloadState: reload, isReloading: reloading, reloadDaemon: _ => Task.CompletedTask);
            using var activation = vm.Activator.Activate();
            var canReload = false;
            using var canExecuteSub = vm.ReloadDaemonCommand.CanExecute.Subscribe(x => canReload = x);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.BackgroundPriority).IsFalse();
            await Assert.That(vm.BackgroundPriorityText).IsNull();
            await Assert.That(canReload).IsFalse();
            await Assert.That(vm.ReloadFailureText).IsEqualTo(ReloadCopy.For(reload.Value!));
            await Assert.That(vm.ShowsReloadBlock).IsTrue();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.BackgroundPriority).IsTrue();
            await Assert.That(vm.BackgroundPriorityText).IsEqualTo(MainWindowViewModel.BackgroundPriorityMessage);
            await Assert.That(canReload).IsTrue();

            reloading.OnNext(true);
            await Assert.That(vm.IsReloading).IsTrue();
            await Assert.That(canReload).IsFalse();

            reloading.OnNext(false);
            reload.OnNext(null);
            background.OnNext(false);
            await Assert.That(vm.ReloadFailureText).IsNull();
            await Assert.That(vm.ShowsReloadBlock).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_view_model_built_while_unreachable_shows_the_replayed_failure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            var reload = new BehaviorSubject<ReloadState?>(new ReloadState(ReloadOutcomeKind.Unconfirmed, "unconfirmed", null, "daemon-a", 1));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, reloadState: reload);
            using var activation = vm.Activator.Activate();
            await Assert.That(vm.ReloadFailureText).IsEqualTo(ReloadCopy.For(reload.Value!));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reload_command_can_execute_when_activated_while_already_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                backgroundPriority: new BehaviorSubject<bool>(true), isReloading: new BehaviorSubject<bool>(false),
                reloadDaemon: _ => Task.CompletedTask);
            using var activation = vm.Activator.Activate();

            var canExecute = false;
            using (vm.ReloadDaemonCommand.CanExecute.Subscribe(x => canExecute = x)) { }
            await Assert.That(canExecute).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reload_command_runs_the_controller_action_only_when_it_can() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var calls = 0;
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                backgroundPriority: new BehaviorSubject<bool>(true), isReloading: new BehaviorSubject<bool>(false),
                reloadDaemon: _ => { calls++; return Task.CompletedTask; });
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await vm.ReloadDaemonCommand.Execute().ToTask();
            await Assert.That(calls).IsEqualTo(1);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            var canExecute = false;
            using (vm.ReloadDaemonCommand.CanExecute.Subscribe(x => canExecute = x)) { }
            await Assert.That(canExecute).IsFalse();
        });
    }

    // ── server rows and the ended hand-off ─────────────────────────────────────────────────────

    static AgentStatusDto LocalWithSession(string id, string sessionId, string status = "Running") =>
        WorkspaceFixtures.Agent(id, "claude", false, "/repos/alpha", sessionId: sessionId) with { Status = status };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_a_server_row_selects_its_server_key() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, rail: rail, originOf: _ => AgentOrigin.Server,
                serverWorkspaceFactory: id => built = host.NewServer(id), directory: host.Directory);

            await Assert.That(vm.OpenSession("S", AgentOrigin.Server)).IsTrue();
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built);
            await Assert.That(built!.IsSessionOnly).IsTrue();
            await Assert.That(rail.SelectedSessionKey).IsEqualTo("server:S");

            vm.OpenSession("S", AgentOrigin.Server);
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built);
            vm.CloseWorkspace();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_completed_local_workspace_hands_off_to_the_server_row_within_the_grace() {
        // A real WorkspaceViewModel's status pushes reach the UI dispatcher, so this runs on a pumped one.
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), new RepoIdentity("path:/repos/alpha", "alpha")));
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, id => NewWorkspace(service, id), rail,
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => built = host.NewServer(id), directory: host.Directory, time: time);
            vm.OpenSession("a1", AgentOrigin.Local);
            var local = vm.CurrentWorkspace;

            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Completed"));
            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("local:a1");
                rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            });

            await WorkspaceFixtures.WaitUntilAsync(() => built is not null && ReferenceEquals(vm.CurrentWorkspace, built), what: "hand-off to the server row");
            await Assert.That(built!.IsSessionOnly).IsTrue();
            await Assert.That(rail.SelectedSessionKey).IsEqualTo("server:S");
            vm.CloseWorkspace();
        });
    }

    /// A scope switch or a daemon row taking the session removes the server row too; only the
    /// server's own word that the session is gone closes the workspace.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_only_workspace_survives_a_transient_row_removal() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            var vm = NewVm(service, originOf: _ => AgentOrigin.Server,
                serverWorkspaceFactory: id => host.NewServer(id), directory: host.Directory, sessions: host.Sessions);
            vm.OpenSession("S", AgentOrigin.Server);
            var open = vm.CurrentWorkspace;

            host.Directory.Rows.Remove("server:S");
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));

            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(open);
            vm.CloseWorkspace();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_only_workspace_closes_when_the_server_drops_the_session() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            var vm = NewVm(service, originOf: _ => AgentOrigin.Server,
                serverWorkspaceFactory: id => host.NewServer(id), directory: host.Directory, sessions: host.Sessions);
            vm.OpenSession("S", AgentOrigin.Server);

            host.Sessions.DroppedSubject.OnNext("S");

            await Assert.That(vm.CurrentWorkspace).IsNull();
        });
    }

    /// A daemon has taken the session up: the live workspace is the one to show.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_only_workspace_follows_the_session_onto_a_daemon_row() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S", "active"));
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => host.NewServer(id, "active"), directory: host.Directory, sessions: host.Sessions);
            vm.OpenSession("S", AgentOrigin.Server);

            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("server:S");
                rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), new RepoIdentity("path:/repos/alpha", "alpha")));
            });

            await WorkspaceFixtures.WaitUntilAsync(() => vm.CurrentWorkspace is WorkspaceViewModel { AgentId: "a1" }, what: "the local workspace");
            vm.CloseWorkspace();
        });
    }

    /// Dropping the server row lets a completed daemon row for the same session show again; that row
    /// is no takeover, and the server's word that the session is gone closes the workspace.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dropped_session_closes_even_when_a_completed_daemon_row_resurfaces() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Completed"));
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => host.NewServer(id), directory: host.Directory, sessions: host.Sessions);
            vm.OpenSession("S", AgentOrigin.Server);

            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("server:S");
                rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S", "Completed"), new RepoIdentity("path:/repos/alpha", "alpha")));
            });
            host.Sessions.DroppedSubject.OnNext("S");

            await WorkspaceFixtures.WaitUntilAsync(() => vm.CurrentWorkspace is null, what: "closed on the drop");
        });
    }

    /// While the local daemon reports another server, a local row sharing the id is another session.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_only_workspace_ignores_a_local_row_from_another_server() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            host.Directory.LocalOnAppServer.OnNext(false);
            var service = new FakeDaemonClientService();
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S", "active"));
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => host.NewServer(id, "active"), directory: host.Directory, sessions: host.Sessions);
            vm.OpenSession("S", AgentOrigin.Server);
            var open = vm.CurrentWorkspace;

            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("server:S");
                rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), new RepoIdentity("path:/repos/alpha", "alpha")));
            });

            await Task.Delay(100);
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(open);
            vm.CloseWorkspace();
        });
    }

    /// A row the daemon lane no longer vouches for is history on display, not a takeover.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_cached_local_row_without_its_socket_is_no_takeover() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            host.Directory.LocalConnected = false;
            var service = new FakeDaemonClientService();
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => host.NewServer(id), directory: host.Directory, sessions: host.Sessions);
            vm.OpenSession("S", AgentOrigin.Server);
            var open = vm.CurrentWorkspace;

            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("server:S");
                rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), new RepoIdentity("path:/repos/alpha", "alpha")));
            });

            await Task.Delay(100);
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(open);
            vm.CloseWorkspace();
        });
    }

    /// One changeset can carry a live revision and the end together; the directory's state when the
    /// change is read is what counts.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_revision_in_the_same_changeset_as_the_end_does_not_cancel_the_hand_off() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            var repo = new RepoIdentity("path:/repos/alpha", "alpha");
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), repo));
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => built = host.NewServer(id), directory: host.Directory, time: time, sessions: host.Sessions);
            vm.OpenSession("a1", AgentOrigin.Local);
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Completed"));

            host.Directory.Rows.Edit(rows => {
                rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S") with { Title = "still running" }, repo));
                rows.RemoveKey("local:a1");
                rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            });

            await WorkspaceFixtures.WaitUntilAsync(() => built is not null && ReferenceEquals(vm.CurrentWorkspace, built), what: "hand-off despite the live revision");
            vm.CloseWorkspace();
        });
    }

    /// A session id names a session only within one server: while the local daemon reports another
    /// one, a listed session sharing the id is somebody else's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_completed_local_workspace_closes_when_the_daemon_is_on_another_server() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            host.Directory.LocalOnAppServer.OnNext(false);
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), new RepoIdentity("path:/repos/alpha", "alpha")));
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local, serverWorkspaceFactory: id => host.NewServer(id), directory: host.Directory, time: time);
            vm.OpenSession("a1", AgentOrigin.Local);

            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Completed"));

            await WorkspaceFixtures.WaitUntilAsync(() => vm.CurrentWorkspace is null, what: "closed without a hand-off");
        });
    }

    /// While the directory still carries the row as running, only a change to that row can mean it
    /// came back; another session's revision proves nothing about it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unrelated_directory_change_does_not_cancel_the_hand_off() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), new RepoIdentity("path:/repos/alpha", "alpha")));
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => built = host.NewServer(id), directory: host.Directory, time: time);
            vm.OpenSession("a1", AgentOrigin.Local);

            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Completed"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("other", "T"), new RepoIdentity("path:/repos/beta", "beta")));
            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("local:a1");
                rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            });

            await WorkspaceFixtures.WaitUntilAsync(() => built is not null && ReferenceEquals(vm.CurrentWorkspace, built), what: "hand-off despite an unrelated change");
            vm.CloseWorkspace();
        });
    }

    /// The daemon reports the hook's GUID as spelled; the server lists it as 32 hex digits.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_completed_local_workspace_hands_off_when_the_server_spells_the_session_without_dashes() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            const string hook = "0D1E2F30-4455-6677-8899-AABBCCDDEEFF";
            const string filed = "0d1e2f30445566778899aabbccddeeff";
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            service.Agents.AddOrUpdate(LocalWithSession("a1", hook));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", hook), new RepoIdentity("path:/repos/alpha", "alpha")));
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, id => NewWorkspace(service, id), rail,
                originOf: id => id == filed ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => built = host.NewServer(id), directory: host.Directory, time: time);
            vm.OpenSession("a1", AgentOrigin.Local);

            service.Agents.AddOrUpdate(LocalWithSession("a1", hook, "Completed"));
            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("local:a1");
                rows.AddOrUpdate(RemoteHost.ServerRow(filed));
            });

            await WorkspaceFixtures.WaitUntilAsync(() => built is not null && ReferenceEquals(vm.CurrentWorkspace, built), what: "hand-off to the server row");
            await Assert.That(rail.SelectedSessionKey).IsEqualTo($"server:{filed}");
            vm.CloseWorkspace();
        });
    }

    /// The server can take longer than the grace to list a stopped session as ended. While the rail
    /// still shows the finished row the workspace stays selected; it closes once the row is gone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_completed_local_workspace_closes_only_once_its_row_leaves_the_rail() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            var repo = new RepoIdentity("path:/repos/alpha", "alpha");
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), repo));
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local, serverWorkspaceFactory: id => host.NewServer(id), directory: host.Directory, time: time);
            vm.OpenSession("a1", AgentOrigin.Local);
            var local = vm.CurrentWorkspace;

            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Completed"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S", "Completed"), repo));
            await WorkspaceFixtures.WaitUntilAsync(() => ((WorkspaceViewModel)local!).SessionEnded, what: "the agent's end reached the workspace");

            time.Advance(AgentDirectory.HandoverGrace + TimeSpan.FromSeconds(1));
            await Task.Delay(100);
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(local);

            host.Directory.Rows.RemoveKey("local:a1");
            await WorkspaceFixtures.WaitUntilAsync(() => vm.CurrentWorkspace is null, what: "closed once the rail dropped the row");
        });
    }

    /// Past the grace, a server row that lists the session ended still takes the workspace over.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_completed_local_workspace_hands_off_to_a_server_row_that_arrives_after_the_grace() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            var repo = new RepoIdentity("path:/repos/alpha", "alpha");
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S"), repo));
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, id => NewWorkspace(service, id), rail,
                originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Local,
                serverWorkspaceFactory: id => built = host.NewServer(id), directory: host.Directory, time: time);
            vm.OpenSession("a1", AgentOrigin.Local);
            var local = vm.CurrentWorkspace;

            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Completed"));
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(LocalWithSession("a1", "S", "Completed"), repo));
            await WorkspaceFixtures.WaitUntilAsync(() => ((WorkspaceViewModel)local!).SessionEnded, what: "the agent's end reached the workspace");
            time.Advance(AgentDirectory.HandoverGrace + TimeSpan.FromSeconds(1));
            await Task.Delay(100);
            await Assert.That(rail.SelectedSessionKey).IsEqualTo("local:a1");

            host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("local:a1");
                rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            });

            await WorkspaceFixtures.WaitUntilAsync(() => built is not null && ReferenceEquals(vm.CurrentWorkspace, built), what: "hand-off after the grace");
            await Assert.That(rail.SelectedSessionKey).IsEqualTo("server:S");
            vm.CloseWorkspace();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_local_workspace_keeps_todays_behaviour() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            service.Agents.AddOrUpdate(LocalWithSession("a1", "S"));
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            var vm = NewVm(service, id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local, serverWorkspaceFactory: id => host.NewServer(id), directory: host.Directory);
            vm.OpenSession("a1", AgentOrigin.Local);

            service.Agents.AddOrUpdate(LocalWithSession("a1", "S", "Failed"));

            await WorkspaceFixtures.WaitUntilAsync(() => vm.CurrentWorkspace is null, what: "closed on failure");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_removed_remote_row_hands_off_once_the_server_lists_the_session_ended() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "S"), serverWorkspaceFactory: id => built = host.NewServer(id),
                directory: host.Directory, time: time);
            vm.OpenSession("r1", AgentOrigin.Remote);
            var remote = vm.CurrentWorkspace;

            host.Directory.Rows.RemoveKey("remote:r1");
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(remote);

            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built);
            await Assert.That(built!.IsSessionOnly).IsTrue();
            vm.CloseWorkspace();
        });
    }

    /// The server drops a stopped session from its live listing before its ended listing carries it:
    /// the listing keeps the session meanwhile, and the hand-off still lands however long that takes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_removed_remote_row_keeps_its_session_listed_and_hands_off_after_the_grace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            RemoteSessionViewModel? built = null;
            var vm = NewVm(service, originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "S"), serverWorkspaceFactory: id => built = host.NewServer(id),
                directory: host.Directory, time: time, sessions: host.Sessions);
            vm.OpenSession("r1", AgentOrigin.Remote);
            var remote = vm.CurrentWorkspace;

            host.Directory.Rows.RemoveKey("remote:r1");
            await Assert.That(host.Sessions.Kept).IsEqualTo("S");
            time.Advance(AgentDirectory.HandoverGrace + TimeSpan.FromSeconds(1));
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(remote);

            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built);
            vm.CloseWorkspace();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_that_returns_within_the_grace_cancels_the_hand_off() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var time = new FakeTimeProvider();
            var vm = NewVm(service, originOf: id => id == "S" ? AgentOrigin.Server : AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "S"), serverWorkspaceFactory: id => host.NewServer(id),
                directory: host.Directory, time: time);
            vm.OpenSession("r1", AgentOrigin.Remote);
            var remote = vm.CurrentWorkspace;
            var row = host.Directory.Rows.Lookup("remote:r1").Value;

            host.Directory.Rows.RemoveKey("remote:r1");
            host.Directory.Rows.AddOrUpdate(row);
            host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));

            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(remote);
            time.Advance(AgentDirectory.HandoverGrace + TimeSpan.FromSeconds(1));
            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(remote);
            vm.CloseWorkspace();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task New_session_points_the_launcher_at_the_selected_sessions_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = new TempDir();
            string repo = tmp.CreateDir("alpha");
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var row = AgentRow.FromLocal(WorkspaceFixtures.Agent("a1", "claude", false, repo), new RepoIdentity("path:" + repo, "alpha"));
            host.Directory.Rows.AddOrUpdate(row);
            using var rail = new SessionRailViewModel(host.Directory, _ => { }, _ => { }, TimeProvider.System);
            using var home = new HomeViewModel(service, new AppStateStore(tmp.PathTo("app-state.json")), new UnusedLaunchClient(),
                () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                workspaceFactory: id => NewWorkspace(service, id), rail: rail, directory: host.Directory, home: home);
            vm.OpenSession("a1");
            await Assert.That(rail.SelectedSessionKey).IsEqualTo(row.Key);

            await vm.NewSessionCommand.Execute().ToTask();

            await Assert.That(vm.CurrentWorkspace).IsNull();
            await Assert.That(home.SelectedRepoPath).IsEqualTo(PlatformPaths.Normalize(repo));
        });
    }

    /// Hide-to-tray releases the workspace, and every trip back reopens the same session, selected
    /// in the rail and on the tab it showed, rather than the launcher.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Coming_back_from_the_tray_reopens_the_session_on_its_tab() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            service.Agents.AddOrUpdate(new AgentStatusDto(
                "a1", "agent", "claude", "/dev/alpha", "Running", null, null, null, DateTime.UtcNow, null, null));
            var directory = new AgentDirectory(
                service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null), p => p, null, null,
                TimeProvider.System);
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), rail: rail,
                trackWorkspaceTeardown: teardown => _ = teardown());
            vm.OpenSession("a1");
            await ((WorkspaceViewModel)vm.CurrentWorkspace!).ShowTerminalCommand.Execute().ToTask();

            for (var trip = 0; trip < 3; trip++) {
                vm.ParkWorkspace();
                await Assert.That(vm.CurrentWorkspace).IsNull();

                vm.RestoreParkedWorkspace();
                await Assert.That(vm.CurrentWorkspace?.AgentId).IsEqualTo("a1");
                await Assert.That(((WorkspaceViewModel)vm.CurrentWorkspace!).IsTerminalActive).IsTrue();
                await Assert.That(rail.SelectedSessionKey).IsEqualTo("local:a1");
            }
            vm.CloseWorkspace();
        });
    }

    /// The window's Closed callback can park again after the hide already did: the second park,
    /// with nothing open, must not forget the session.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_repeated_park_keeps_the_session_the_first_one_remembered() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), trackWorkspaceTeardown: teardown => _ = teardown());
            vm.OpenSession("a1");

            vm.ParkWorkspace();
            vm.ParkWorkspace();
            vm.RestoreParkedWorkspace();

            await Assert.That(vm.CurrentWorkspace?.AgentId).IsEqualTo("a1");
            vm.CloseWorkspace();
        });
    }

    /// A notification opened another session while the window was hidden: that is where the user
    /// is going, so the parked session is not brought back over it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_opened_while_hidden_wins_over_the_parked_one() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), trackWorkspaceTeardown: teardown => _ = teardown());
            vm.OpenSession("a1");

            vm.ParkWorkspace();
            vm.OpenSession("a2");
            vm.RestoreParkedWorkspace();

            await Assert.That(vm.CurrentWorkspace?.AgentId).IsEqualTo("a2");
            vm.CloseWorkspace();
        });
    }

    /// A session that ended while the window was hidden cannot be reopened; the window comes back
    /// on the launcher, and a later trip does not try again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_parked_session_that_is_gone_leaves_the_launcher() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var listed = true;
            var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => listed ? AgentOrigin.Local : null, trackWorkspaceTeardown: teardown => _ = teardown());
            vm.OpenSession("a1");

            vm.ParkWorkspace();
            listed = false;
            vm.RestoreParkedWorkspace();
            await Assert.That(vm.CurrentWorkspace).IsNull();

            listed = true;
            vm.RestoreParkedWorkspace();
            await Assert.That(vm.CurrentWorkspace).IsNull();
        });
    }
}
