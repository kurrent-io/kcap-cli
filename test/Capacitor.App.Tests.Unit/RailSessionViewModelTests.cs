using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// RailSessionViewModel's OpenCommand and IsSelected OAPH both run through
/// RxSchedulers.MainThreadScheduler, which is not immediate in a bare test process — see
/// MainWindowViewModelTests' header comment. Every test here runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class RailSessionViewModelTests {
    static readonly IObservable<IReadOnlySet<string>> NoPending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
    static readonly IObservable<bool> NotStale = new BehaviorSubject<bool>(false);
    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    static AgentRow Row(
            string id = "a1", string kind = "agent", string vendor = "claude", string status = "Running",
            string? model = "Opus 5", string? title = "Fix the flaky test", bool? awaitingInput = null, int? liveSubagents = null) =>
        AgentRow.FromLocal(
            new(id, kind, vendor, "/repo", status, null, null, null, DateTime.UtcNow, model, null,
                Title: title, AwaitingInput: awaitingInput, LiveSubagents: liveSubagents),
            Repo);

    static AgentRow LocalRow(string id) => Row(id: id);

    static AgentRow RemoteRow(string id) => AgentRow.FromRemote(new AgentInstanceDto {
        AgentId = id, Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
        Vendor = "claude", RepoOwner = "o", RepoName = "r",
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_row_reads_its_launch_stage_and_opens_locally() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = AgentRow.FromPending(
                new PendingLaunchDto("p1", "claude", "/repo", "Fix the flaky test", DateTime.UtcNow, "session_created"), Repo);
            var opened = new List<string>();
            using var row = new RailSessionViewModel(pending, new BehaviorSubject<string?>(null), NoPending, NotStale, opened.Add, _ => throw new InvalidOperationException("remote"), TimeProvider.System);

            await Assert.That(row.IsStarting).IsTrue();
            await Assert.That(row.Meta).IsEqualTo("Session created");
            await Assert.That(row.Tooltip).Contains("Starting");
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(opened).IsEquivalentTo(new[] { "p1" });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_published_row_is_not_starting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(status: "Starting"), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.IsStarting).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Title_is_primary_with_vendor_and_model_as_chips() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            await Assert.That(row.HasTitle).IsTrue();
            await Assert.That(row.Vendor).IsEqualTo("claude");
            await Assert.That(row.HasVendor).IsTrue();
            await Assert.That(row.Model).IsEqualTo("Opus 5");
            await Assert.That(row.HasModel).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_title_leaves_the_chips_line_to_carry_the_row() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(title: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.HasTitle).IsFalse();
            await Assert.That(row.Vendor).IsEqualTo("claude");
            await Assert.That(row.Model).IsEqualTo("Opus 5");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Non_agent_kind_goes_to_the_meta_line_not_the_vendor_chip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(kind: "review", title: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Vendor).IsEqualTo("claude");
            await Assert.That(row.Meta).StartsWith("review · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Borrowed_work_location_marks_the_meta_line_and_the_tooltip_names_the_checkout() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var dto = new AgentStatusDto("a1", "review-flow", "codex", "/repo", "Running", null, null, null, DateTime.UtcNow, "Opus 5", null, Title: "Fix the flaky test") with {
                WorktreePath = "/repo/.capacitor/worktrees/agent-1", WorkLocation = "borrowed",
                BorrowedFrom = "/repo/.capacitor/worktrees/agent-1" };
            using var row = new RailSessionViewModel(AgentRow.FromLocal(dto, Repo), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Vendor).IsEqualTo("codex");
            await Assert.That(row.Model).IsEqualTo("Opus 5");
            await Assert.That(row.Meta).StartsWith("review-flow · borrowed · ");
            await Assert.That(row.Tooltip).Contains("/repo/.capacitor/worktrees/agent-1");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_model_hides_the_model_chip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(model: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Model).IsNull();
            await Assert.That(row.HasModel).IsFalse();
        });
    }

    /// A finished turn is Idle; a live turn is Working; an unknown turn keeps the daemon's word.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Awaiting_input_is_idle_and_a_live_turn_is_working() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var waiting = new RailSessionViewModel(Row(awaitingInput: true), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var working = new RailSessionViewModel(Row(awaitingInput: false), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var older   = new RailSessionViewModel(Row(awaitingInput: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(waiting.Status.Kind).IsEqualTo(AgentStatusKind.Idle);
            await Assert.That(waiting.Status.Label).IsEqualTo("Idle");
            await Assert.That(waiting.Status.AccessibleName).IsEqualTo("Idle");
            await Assert.That(waiting.Tooltip).DoesNotContain("Waiting for input");
            await Assert.That(working.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(working.Status.Label).IsEqualTo("Working");
            await Assert.That(working.Tooltip).DoesNotContain("Waiting for input");
            await Assert.That(older.Status.Kind).IsEqualTo(AgentStatusKind.Other);
            await Assert.That(older.Status.Label).IsEqualTo("Running");
        });
    }

    /// A flow participant between rounds waits on the flow, not on the user, who cannot message
    /// it anyway.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Awaiting_input_on_a_flow_participant_does_not_set_the_pip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(kind: "review-flow", awaitingInput: true), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Status.Kind).IsNotEqualTo(AgentStatusKind.Idle);
            await Assert.That(row.Tooltip).DoesNotContain("Waiting for input");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task IsSelected_tracks_the_selection_observable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var selected = new BehaviorSubject<string?>(null);
            using var row = new RailSessionViewModel(Row(id: "a1"), selected, NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.IsSelected).IsFalse();
            selected.OnNext("a1");
            await Assert.That(row.IsSelected).IsTrue();
            selected.OnNext("other");
            await Assert.That(row.IsSelected).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenCommand_invokes_the_local_callback_with_the_id() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string? opened = null;
            using var row = new RailSessionViewModel(Row(id: "a9"), new BehaviorSubject<string?>(null), NoPending, NotStale, id => opened = id, _ => { }, TimeProvider.System);
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(opened).IsEqualTo("a9");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenCommand_invokes_the_remote_callback_and_the_row_carries_its_machine_badge() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var dto = new AgentInstanceDto {
                AgentId = "b1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RepoOwner = "o", RepoName = "r",
            };
            string? openedRemote = null;
            string? openedLocal = null;
            using var row = new RailSessionViewModel(
                AgentRow.FromRemote(dto), new BehaviorSubject<string?>(null), NoPending, NotStale, id => openedLocal = id, id => openedRemote = id, TimeProvider.System);
            await Assert.That(row.IsRemote).IsTrue();
            await Assert.That(row.MachineBadge).IsEqualTo("work-mac");
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(openedRemote).IsEqualTo("b1");
            await Assert.That(openedLocal).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Needs_you_follows_the_pending_set_and_the_status() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var row = new RailSessionViewModel(Row(status: "Running"), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Other);
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            await Assert.That(row.Tooltip).Contains("Pending response");
            pending.OnNext(new HashSet<string>());
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Other);

            using var failed = new RailSessionViewModel(Row(status: "Failed"), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(failed.Status.Kind).IsEqualTo(AgentStatusKind.Failed);
            await Assert.That(failed.Status.Label).IsEqualTo("Failed");

            using var idle = new RailSessionViewModel(Row(awaitingInput: true), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.Idle);
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            pending.OnNext(new HashSet<string>());
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.Idle);

            var asked = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string> { "a1" });
            using var question = new RailSessionViewModel(
                Row(awaitingInput: true), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { },
                TimeProvider.System, asked);
            await Assert.That(question.Status.Kind).IsEqualTo(AgentStatusKind.Answer);
            await Assert.That(question.Status.Label).IsEqualTo("Answer");
            await Assert.That(question.Status.AccessibleName).IsEqualTo("An answer is expected");
            await Assert.That(question.Tooltip).DoesNotContain("Pending response");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_greys_out_while_the_lane_is_stale_and_a_local_row_never_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var stale = new BehaviorSubject<bool>(true);
            using var remote = new RailSessionViewModel(RemoteRow("r1"), new BehaviorSubject<string?>(null), NoPending, stale, _ => { }, _ => { }, TimeProvider.System);
            using var local = new RailSessionViewModel(LocalRow("a1"), new BehaviorSubject<string?>(null), NoPending, stale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(remote.IsStale).IsTrue();
            await Assert.That(local.IsStale).IsFalse();

            stale.OnNext(false);
            await Assert.That(remote.IsStale).IsFalse();
        });
    }

    /// The daemon's count keeps the row visibly busy while only subagents run.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_subagents_pulse_the_dot_and_name_the_count() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var two   = new RailSessionViewModel(Row(liveSubagents: 2), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var one   = new RailSessionViewModel(Row(liveSubagents: 1), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var none  = new RailSessionViewModel(Row(liveSubagents: 0), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var older = new RailSessionViewModel(Row(liveSubagents: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);

            await Assert.That(two.Status.Pulses).IsTrue();
            await Assert.That(two.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(two.Meta).EndsWith(" · 2 subagents");
            await Assert.That(two.Tooltip).Contains("2 subagents running");
            await Assert.That(one.Status.Pulses).IsTrue();
            await Assert.That(one.Meta).EndsWith(" · 1 subagent");
            await Assert.That(one.Tooltip).Contains("1 subagent running");
            await Assert.That(none.Status.Pulses).IsFalse();
            await Assert.That(none.Meta).DoesNotContain("subagent");
            await Assert.That(none.Tooltip).DoesNotContain("subagent");
            await Assert.That(older.Status.Pulses).IsFalse();
            await Assert.That(older.Meta).DoesNotContain("subagent");
            await Assert.That(older.Tooltip).DoesNotContain("subagent");
        });
    }

    /// One verdict: live subagents are Working, and the tip still says the turn is waiting.
    /// A pending response outranks that.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_subagents_are_working_until_a_pending_response_outranks_them() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var waiting = new RailSessionViewModel(Row(id: "a1", awaitingInput: true, liveSubagents: 2), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var busy    = new RailSessionViewModel(Row(id: "a2", awaitingInput: false, liveSubagents: 2), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var idle    = new RailSessionViewModel(Row(id: "a3", awaitingInput: true, liveSubagents: 0), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);

            await Assert.That(waiting.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(waiting.Status.Pulses).IsTrue();
            await Assert.That(waiting.Tooltip).Contains("Waiting for input");
            await Assert.That(waiting.Tooltip).Contains("2 subagents running");
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(waiting.Status.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            await Assert.That(waiting.Status.Pulses).IsFalse();

            await Assert.That(busy.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(busy.Status.Pulses).IsTrue();
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.Idle);
            await Assert.That(idle.Status.Pulses).IsFalse();
        });
    }

    /// A remote row carries no count, and a pending row pulses for its start.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_is_unchanged_and_a_pending_row_still_pulses() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var remote = new RailSessionViewModel(RemoteRow("r1"), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            var pendingRow = AgentRow.FromPending(new PendingLaunchDto("p1", "claude", "/repo", "t", DateTime.UtcNow, "spawned"), Repo);
            using var pending = new RailSessionViewModel(pendingRow, new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);

            await Assert.That(remote.Status.Pulses).IsFalse();
            await Assert.That(remote.Status.Label).IsEqualTo("Running");
            await Assert.That(remote.Meta).DoesNotContain("subagent");
            await Assert.That(remote.Tooltip).DoesNotContain("subagent");
            await Assert.That(pending.Status.Kind).IsEqualTo(AgentStatusKind.Starting);
            await Assert.That(pending.Status.Pulses).IsTrue();
            await Assert.That(pending.Tooltip).Contains("Process started");
        });
    }
}
