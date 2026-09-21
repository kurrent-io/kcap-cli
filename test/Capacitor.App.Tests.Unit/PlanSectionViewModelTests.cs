using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The pane's plan section: which plan it shows, what it counts, the session-id lease that owns
/// each read, and the transcript-driven refresh. Every read settles through Dispatcher.UIThread,
/// so every test runs under RunOnUiAsync and carries [NotInParallel("AvaloniaSession")].
public class PlanSectionViewModelTests {
    const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    sealed class Harness {
        public FakePlanSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public PlanActivity Activity { get; } = new();
        public PlanSectionViewModel Vm { get; }

        public Harness() => Vm = new PlanSectionViewModel(Source, Activity, Time);

        public async Task SwitchAsync(string sessionId) {
            Vm.SwitchSession(sessionId);
            await SettledAsync();
        }

        public async Task RefreshAsync() {
            Vm.Refresh();
            await SettledAsync();
        }

        public Task SettledAsync() => Vm.PendingReadForTesting ?? Task.CompletedTask;

        /// A plan write whose result lands in one projection, as the chat tab would apply it.
        public void WritePlan(string callId = "c1") => Activity.Apply([new ChatProjectionResult([
            new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: callId, ToolName: PlanToolNames.UpdateTask),
            new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId),
        ], [], [])]);
    }

    static PlanLedgerTaskDto Todo(string id, int ordinal, string status, string? note = null) =>
        new() { TaskId = id, Ordinal = ordinal, Title = $"Task {ordinal}", Status = status, Note = note };

    static PlanDocumentDto Doc(string kind, string path) => new() { DocumentKey = path, Kind = kind, Path = path };

    static SessionPlansRead Ready(params SessionPlanDto[] plans) => new(SessionPlansReadKind.Ready, plans);

    static SessionPlanDto Plan(string id, bool current = false, PlanDocumentDto[]? documents = null, params PlanLedgerTaskDto[] tasks) =>
        new() { PlanId = id, IsCurrent = current, Documents = [.. documents ?? []], Tasks = [.. tasks] };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_section_is_hidden_until_a_read_names_a_plan_then_lists_tasks_in_order_and_counts_them() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.HasPlan).IsFalse();

            h.Source.Enqueue(Ready(Plan("p1", tasks: [
                Todo("t3", 3, "pending"), Todo("t1", 1, "completed"), Todo("t2", 2, "in_progress", "half way"), Todo("t4", 4, "skipped"),
            ])));
            await h.SwitchAsync(SessionA);

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA });
            await Assert.That(h.Vm.HasPlan).IsTrue();
            await Assert.That(h.Vm.HasTasks).IsTrue();
            await Assert.That(h.Vm.Tasks.Select(t => t.Title)).IsEquivalentTo(new[] { "Task 1", "Task 2", "Task 3", "Task 4" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(h.Vm.Tasks.Select(t => t.State)).IsEquivalentTo(
                new[] { PlanTaskState.Completed, PlanTaskState.InProgress, PlanTaskState.Pending, PlanTaskState.Skipped }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(h.Vm.Tasks[1].Note).IsEqualTo("half way");
            await Assert.That(h.Vm.DoneCount).IsEqualTo(2);
            await Assert.That(h.Vm.OpenCount).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_sessions_current_plan_wins_and_the_most_recently_touched_stands_in_without_one() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(
                Ready(Plan("recent", tasks: Todo("r1", 1, "pending")), Plan("mine", current: true, tasks: [Todo("m1", 1, "completed"), Todo("m2", 2, "pending")])),
                Ready(Plan("recent", tasks: Todo("r1", 1, "pending")), Plan("older", tasks: Todo("o1", 1, "completed"))));

            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.Tasks.Select(t => t.TaskId)).IsEquivalentTo(new[] { "m1", "m2" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

            await h.RefreshAsync();
            await Assert.That(h.Vm.Tasks.Select(t => t.TaskId)).IsEquivalentTo(new[] { "r1" });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Documents_list_plan_then_spec_then_design_by_file_name_and_a_plan_of_documents_alone_still_shows() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Plan("p1", documents: [
                Doc("design", "docs/superpowers/specs/2026-09-21-widget-design.md"), Doc("plan", "docs/plans/widget.md"), Doc("spec", "SPEC.md"),
            ])));

            await h.SwitchAsync(SessionA);

            await Assert.That(h.Vm.HasPlan).IsTrue();
            await Assert.That(h.Vm.HasTasks).IsFalse();
            await Assert.That(h.Vm.HasDocuments).IsTrue();
            await Assert.That(h.Vm.Documents.Select(d => d.Kind)).IsEquivalentTo(new[] { "plan", "spec", "design" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(h.Vm.Documents.Select(d => d.FileName)).IsEquivalentTo(
                new[] { "widget.md", "SPEC.md", "2026-09-21-widget-design.md" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(h.Vm.Documents[2].Path).IsEqualTo("docs/superpowers/specs/2026-09-21-widget-design.md");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_status_the_app_does_not_know_presents_as_pending_and_counts_as_open() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Plan("p1", tasks: Todo("t1", 1, "parked"))));

            await h.SwitchAsync(SessionA);

            await Assert.That(h.Vm.Tasks.Single().State).IsEqualTo(PlanTaskState.Pending);
            await Assert.That(h.Vm.OpenCount).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_read_that_settles_after_the_session_changed_is_discarded() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var stale = h.Source.Gate();
            h.Vm.SwitchSession(SessionA);
            var staleRead = h.Vm.PendingReadForTesting!;
            h.Source.Enqueue(Ready(Plan("pb", tasks: Todo("b1", 1, "pending"))));

            await h.SwitchAsync(SessionB);
            stale.SetResult(Ready(Plan("pa", tasks: Todo("a1", 1, "completed"))));
            await staleRead;

            await Assert.That(h.Vm.Tasks.Select(t => t.TaskId)).IsEquivalentTo(new[] { "b1" });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_sessions_empties_the_section_before_the_new_read_answers() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Plan("pa", tasks: Todo("a1", 1, "pending"))));
            await h.SwitchAsync(SessionA);
            var gate = h.Source.Gate();

            h.Vm.SwitchSession(SessionB);

            await Assert.That(h.Vm.HasPlan).IsFalse();
            gate.SetResult(Ready());
            await h.SettledAsync();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_plan_write_in_the_transcript_reads_at_once_and_once_more_after_the_settle_delay() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);

            h.Source.Enqueue(Ready(Plan("p1", tasks: Todo("t1", 1, "in_progress"))), Ready(Plan("p1", tasks: Todo("t1", 1, "completed"))));
            h.WritePlan();
            await h.SettledAsync();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await Assert.That(h.Vm.Tasks.Single().State).IsEqualTo(PlanTaskState.InProgress);

            h.Time.Advance(PlanSectionViewModel.SettleDelay);
            await h.SettledAsync();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            await Assert.That(h.Vm.Tasks.Single().State).IsEqualTo(PlanTaskState.Completed);

            h.Time.Advance(PlanSectionViewModel.SettleDelay);
            await h.SettledAsync();
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Writes_that_land_while_a_read_is_in_flight_queue_one_follow_up_read() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.Gate();
            h.Vm.SwitchSession(SessionA);

            h.WritePlan("c1");
            h.WritePlan("c2");
            h.WritePlan("c3");
            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);

            var first = h.Vm.PendingReadForTesting!;
            gate.SetResult(Ready());
            await first;
            await h.SettledAsync();

            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_outage_keeps_the_last_plan_while_a_refusal_or_a_sign_out_clears_it() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(
                Ready(Plan("p1", tasks: Todo("t1", 1, "pending"))),
                SessionPlansRead.Of(SessionPlansReadKind.Unreachable),
                SessionPlansRead.Of(SessionPlansReadKind.Unavailable),
                Ready(Plan("p1", tasks: Todo("t1", 1, "pending"))),
                SessionPlansRead.Of(SessionPlansReadKind.SignedOut));

            await h.SwitchAsync(SessionA);
            await h.RefreshAsync();
            await Assert.That(h.Vm.Tasks.Count).IsEqualTo(1);

            await h.RefreshAsync();
            await Assert.That(h.Vm.HasPlan).IsFalse();

            await h.RefreshAsync();
            await Assert.That(h.Vm.HasPlan).IsTrue();

            await h.RefreshAsync();
            await Assert.That(h.Vm.HasPlan).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    /// Replacing the rows would rebuild their containers and restart the in-progress pulse on
    /// every poll, so an unchanged task list must keep its row objects.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_re_read_of_the_same_tasks_updates_the_rows_in_place() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(
                Ready(Plan("p1", tasks: [Todo("t1", 1, "in_progress"), Todo("t2", 2, "pending")])),
                Ready(Plan("p1", tasks: [Todo("t1", 1, "completed"), Todo("t2", 2, "in_progress")])));
            await h.SwitchAsync(SessionA);
            var rows = h.Vm.Tasks.ToArray();

            await h.RefreshAsync();

            await Assert.That(ReferenceEquals(h.Vm.Tasks[0], rows[0])).IsTrue();
            await Assert.That(ReferenceEquals(h.Vm.Tasks[1], rows[1])).IsTrue();
            await Assert.That(rows[0].IsCompleted).IsTrue();
            await Assert.That(rows[1].IsInProgress).IsTrue();
            await Assert.That(h.Vm.DoneCount).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_task_in_progress_stops_reading_as_active_once_the_session_is_over() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(Plan("p1", tasks: Todo("t1", 1, "in_progress"))));
            await h.SwitchAsync(SessionA);
            await Assert.That(h.Vm.Tasks.Single().IsActive).IsTrue();

            h.Activity.SessionOver = true;

            await Assert.That(h.Vm.Tasks.Single().IsInProgress).IsTrue();
            await Assert.That(h.Vm.Tasks.Single().IsActive).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_section_starts_expanded_and_the_toggle_folds_it() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.IsExpanded).IsTrue();

            h.Vm.ToggleCommand.Execute().Subscribe();

            await Assert.That(h.Vm.IsExpanded).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_source_the_section_never_reads_and_stays_hidden() {
        await RunOnUiAsync(async () => {
            var vm = new PlanSectionViewModel(null, new PlanActivity(), new FakeTimeProvider());

            vm.SwitchSession(SessionA);
            vm.Refresh();

            await Assert.That(vm.PendingReadForTesting is null).IsTrue();
            await Assert.That(vm.HasPlan).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_cancels_the_read_in_flight_and_later_writes_read_nothing() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Gate();
            h.Vm.SwitchSession(SessionA);

            await h.Vm.TeardownAsync();
            h.WritePlan();
            h.Time.Advance(PlanSectionViewModel.SettleDelay);

            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);
            await Assert.That(h.Vm.HasPlan).IsFalse();
        });
    }
}
