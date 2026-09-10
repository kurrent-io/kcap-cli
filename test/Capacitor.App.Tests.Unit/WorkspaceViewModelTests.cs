using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// WorkspaceViewModel's header projections (Title/RepoLabelText/
/// ShowsTerminalTab), SessionEnded, Chat construction, and Stop routing. WorkspaceViewModel always
/// builds a real TerminalTabViewModel internally, so pushing a matching AgentStatusDto into the
/// shared daemon.Agents cache also drives Terminal's OWN resolve gate -- which reaches
/// Dispatcher.UIThread.InvokeAsync regardless of hasTerminal (both the NoTerminal and the attach
/// branches dispatch). Every test therefore runs through the same RunOnUiAsync nesting
/// TerminalTabViewModelTests uses (DispatchAsync for a live pumped dispatcher, WithImmediateRxScheduler
/// so ObserveOn(RxSchedulers.MainThreadScheduler) applies synchronously) and carries
/// [NotInParallel("AvaloniaSession")] -- see that class's identical header comment.
public class WorkspaceViewModelTests {
    static WorkspaceViewModel Build(
            FakeDaemonClientService daemon, AgentActionService actions, FakeTerminalAttachClientFactory factory,
            FakeTimeProvider time, string agentId = "a1") =>
        new(agentId, daemon, actions, factory.Factory, () => new FakeTerminalSurface(), time, new RecordingOpener(),
            new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps());

    static AgentActionService NewActions(
            ScriptedLocalControlOps ops, RecordingNotifier notifier, RecordingOpener opener,
            Func<string, Task<bool>>? confirmForceStop = null) =>
        new(ops, notifier, opener, new ReplaySubject<DaemonStatusDto>(1), CancellationToken.None,
            confirmForceStop ?? NeverConfirm.Confirm);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Title_and_repo_label_project_from_the_pushed_dto() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var actions = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, actions, factory, new FakeTimeProvider(), agentId: "a1");

            // Before any dto: placeholders, never a crash on the null-dto default path.
            await Assert.That(vm.RepoLabelText).IsEqualTo("—");
            await Assert.That(vm.ShowsTerminalTab).IsFalse();

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", model: "sonnet"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(vm.Title).IsEqualTo("myproj");
            await Assert.That(vm.RepoLabelText).IsEqualTo("myproj");
            await Assert.That(vm.ShowsTerminalTab).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SessionEnded_flips_on_cache_removal_and_the_header_stays_frozen() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var actions = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, actions, factory, new FakeTimeProvider(), agentId: "a1");

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.SessionEnded).IsFalse();

            daemon.Agents.Remove("a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            // The header keeps identifying the session that just ended rather than reverting to
            // a blanked placeholder -- a frozen last-known snapshot.
            await Assert.That(vm.Title).IsEqualTo("myproj");
            await Assert.That(vm.ShowsTerminalTab).IsTrue();
            await Assert.That(await vm.StopCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SessionEnded_and_Stop_disable_when_status_becomes_Completed_or_Failed() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var actions = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, actions, factory, new FakeTimeProvider(), agentId: "a1");

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj") with { Status = "Running" });
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(await vm.StopCommand.CanExecute.FirstAsync()).IsTrue();

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj") with { Status = "Completed" });
            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(await vm.StopCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
            await Assert.That(vm.Terminal.CanAcceptText).IsFalse();
            await Assert.That(vm.Title).IsEqualTo("myproj");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Stop_routes_through_agent_action_service_with_the_dtos_kind() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var ops = new ScriptedLocalControlOps();
            var notifier = new RecordingNotifier();
            var confirmer = new RecordingConfirmer();
            var actions = NewActions(ops, notifier, new RecordingOpener(), confirmForceStop: confirmer.Confirm);
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, actions, factory, new FakeTimeProvider(), agentId: "a1");

            // "review" is a PROTECTED kind (AgentActionService.IsProtectedKind: anything but
            // exactly "agent"). Only reaching the confirm-then-force seam proves StopCommand read
            // the DTO's OWN kind rather than some fixed default -- StopPayloads alone can't show
            // this, since the wire call never carries kind, only the force bool the confirm
            // decision produces.
            daemon.Agents.AddOrUpdate(Agent("a1", "codex", hasTerminal: true, kind: "review"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);

            confirmer.Queue(true);
            ops.QueueStop(new StopAgentResult(true, "stopped", null));
            await vm.StopCommand.Execute();

            await WaitUntilAsync(() => ops.StopCalls >= 1, what: "stop issued after confirm");
            await Assert.That(confirmer.Prompted.Count).IsEqualTo(1);
            await Assert.That(ops.StopPayloads).IsEquivalentTo([("a1", true)], CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_is_the_default_tab_and_the_switch_commands_flip_it() {
        await RunOnUiAsync(async () => {
            var vm = Build(new FakeDaemonClientService(), NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());

            await Assert.That(vm.ActiveTab).IsEqualTo(WorkspaceTab.Chat);
            await Assert.That(vm.IsChatActive).IsTrue();
            await Assert.That(vm.IsTerminalActive).IsFalse();

            await vm.ShowTerminalCommand.Execute();
            await Assert.That(vm.IsTerminalActive).IsTrue();
            await vm.ShowChatCommand.Execute();
            await Assert.That(vm.IsChatActive).IsTrue();
            await vm.TeardownAsync();
        });
    }

    /// The banner layer (session-ended card, detach/reattach controls) is Terminal-tab-only: a
    /// non-PTY session's chat surface owns the pane on every other tab, so it must never render
    /// underneath ChatHost.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ShowsTerminalBanners_is_true_only_on_the_Terminal_tab_pty_or_not() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: false));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.ShowsTerminalBanners).IsFalse();
            await vm.ShowTerminalCommand.Execute();
            await Assert.That(vm.ShowsTerminalBanners).IsTrue();

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.ShowsTerminalBanners).IsTrue();
            await vm.ShowChatCommand.Execute();
            await Assert.That(vm.ShowsTerminalBanners).IsFalse();

            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_is_built_on_the_first_dto_of_any_vendor_with_the_reader_the_format_names() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());
            await Assert.That(vm.Chat).IsNull();

            daemon.Agents.AddOrUpdate(Agent("a1", "pi", hasTerminal: false) with { TranscriptFormat = TranscriptFormats.Envelopes });
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.Chat).IsNotNull();
            await Assert.That(vm.Chat!.Phase).IsEqualTo(ChatTabPhase.Waiting); // journal reader, no path yet
            await Assert.That(vm.ShowsTerminalTab).IsFalse();

            var chat = vm.Chat;
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await Assert.That(vm.Chat).IsSameReferenceAs(chat); // built once

            await vm.TeardownAsync();
            await Assert.That(chat.PendingReadForTesting!).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_format_non_pty_dto_is_the_older_daemon_and_a_vendor_pty_dto_takes_the_vendor_reader() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var older = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());
            daemon.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: false));
            await (older.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(older.Chat!.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(older.Chat.PhaseNote).IsEqualTo("Update the daemon to view this session");
            await older.TeardownAsync();

            var daemon2 = new FakeDaemonClientService();
            var pty = Build(daemon2, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider(), agentId: "a2");
            daemon2.Agents.AddOrUpdate(Agent("a2", "claude", hasTerminal: true) with { TranscriptFormat = TranscriptFormats.Vendor });
            await (pty.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(pty.Chat!.Phase).IsEqualTo(ChatTabPhase.Waiting);
            await Assert.That(pty.ShowsTerminalTab).IsTrue();
            await pty.TeardownAsync();
        });
    }

    /// The subtitle names the checkout under the repository. The main checkout reads as the rail
    /// labels it; an older daemon's null worktree keeps the repository alone; a snapshot reviewer
    /// names the checkout it borrowed, not the copy it runs in.
    [Test]
    public async Task The_checkout_label_names_the_worktree_under_the_repository() {
        var owned = Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj",
            worktreePath: "/repo/myproj/.capacitor/worktrees/agent-6da2", workLocation: "owned");

        await Assert.That(WorkspaceViewModel.CheckoutLabelFor(owned)).IsEqualTo("myproj / agent-6da2");
        await Assert.That(WorkspaceViewModel.CheckoutLabelFor(owned with { WorktreePath = "/repo/myproj" }))
            .IsEqualTo("myproj / main checkout");
        await Assert.That(WorkspaceViewModel.CheckoutLabelFor(owned with { WorktreePath = null, WorkLocation = null }))
            .IsEqualTo("myproj");
        await Assert.That(WorkspaceViewModel.CheckoutLabelFor(owned with {
                WorktreePath = "/snapshots/borrowed-1", WorkLocation = "borrowed",
                BorrowedFrom = "/repo/myproj/.capacitor/worktrees/agent-6da2" }))
            .IsEqualTo("myproj / agent-6da2 · borrowed");
        await Assert.That(WorkspaceViewModel.CheckoutLabelFor(null)).IsEqualTo("—");
    }

    /// Pins the header for a titled session on a borrowed checkout: the title line is the
    /// session's own title, and the subtitle names the repository and the borrowed worktree.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_header_shows_the_session_title_over_the_borrowed_worktree() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var actions = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var vm = Build(daemon, actions, new FakeTerminalAttachClientFactory(), new FakeTimeProvider(), agentId: "r1");

            daemon.Agents.AddOrUpdate(
                Agent("r1", "codex", hasTerminal: true, repoPath: "/repo/myproj", kind: "review-flow",
                        worktreePath: "/repo/myproj/.capacitor/worktrees/agent-6da2", workLocation: "borrowed",
                        borrowedFrom: "/repo/myproj/.capacitor/worktrees/agent-6da2")
                    with { Title = "Review this PR" });
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(vm.Title).IsEqualTo("Review this PR");
            await Assert.That(vm.RepoLabelText).IsEqualTo("myproj / agent-6da2 · borrowed");
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task WorkContext_is_fed_by_the_same_presence_and_torn_down_with_the_workspace() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var source = new FakeWorkContextSource();
            var factory = new FakeTerminalAttachClientFactory();
            var vm = new WorkspaceViewModel("a1", daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()),
                factory.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), source,
                new ScriptedLocalControlOps());
            await Assert.That(vm.WorkContext.Phase).IsEqualTo(WorkContextPhase.WaitingForSession);

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: "0123456789abcdef0123456789abcdef"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.WorkContext.PendingReadForTesting ?? Task.CompletedTask);

            await Assert.That(vm.WorkContext.Repository).IsEqualTo("myproj");
            await Assert.That(source.Requested).IsEquivalentTo(new[] { "0123456789abcdef0123456789abcdef" });

            await vm.TeardownAsync();
            source.Default = WorkContextRead.Of(WorkContextReadKind.Ready);
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: "ffffffffffffffffffffffffffffffff"));
            await Assert.That(source.Requested.Count).IsEqualTo(1);
        });
    }
}
