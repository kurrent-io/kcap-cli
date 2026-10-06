using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// The per-workspace tracker as a pure state machine over projection results: signals first,
/// then envelopes; an ended row is never reopened, and only an end with no outcome is revised.
public class SessionRunsTests {
    static readonly DateTimeOffset T0 = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    static FakeTimeProvider Clock() => new(T0);

    static ChatProjectionResult Signals(params RunSignal[] signals) => new([], [], signals);

    static ChatProjectionResult Result(string callId, bool isError = false, DateTimeOffset? at = null) =>
        new([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId, ToolIsError: isError, TimestampIso: at?.ToString("O"))], [], []);

    static ChatProjectionResult Mixed(IReadOnlyList<AcpEventEnvelope> envelopes, params RunSignal[] signals) => new(envelopes, [], signals);

    static RunSignal.Started Started(string callId, DateTimeOffset? at = null, string name = "Explore") =>
        new(callId, name, "Map the UI", at ?? T0);

    static RunSignal.Detached Detached(string callId, string agentId) => new(callId, agentId);

    static RunSignal.Finished Finished(string? callId, string? agentId, RunOutcome? outcome = RunOutcome.Done, DateTimeOffset? at = null) =>
        new(callId, agentId, outcome, at ?? T0.AddMinutes(2));

    static RunRow Only(SessionRuns s) => s.Rows.Single();

    static RunSignal.Started Shell(string callId, DateTimeOffset? at = null) =>
        new(callId, "Run the full suite", "dotnet test", at ?? T0, RunKind.Shell, Provisional: true);

    [Test]
    public async Task A_foreground_shell_call_leaves_no_row() {
        var s = new SessionRuns(Clock());
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Shell("c1")));
        await Assert.That(s.Rows).IsEmpty();
        s.Apply(Result("c1"));
        await Assert.That(s.Rows).IsEmpty();
        // The pending entry went with the result: a stray Detached later makes no row.
        s.Apply(Signals(Detached("c1", "b1")));
        await Assert.That(s.Rows).IsEmpty();
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task A_background_shell_runs_from_its_detach_until_its_notification() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "b1")));
        var row = Only(s);
        await Assert.That(row.Kind).IsEqualTo(RunKind.Shell);
        await Assert.That(row.IsShell).IsTrue();
        await Assert.That(row.Name).IsEqualTo("Run the full suite");
        await Assert.That(row.Description).IsEqualTo("dotnet test");
        await Assert.That(row.IsBackground).IsTrue();
        await Assert.That(row.StateText).IsEqualTo("running in background · 0s");
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", "b1", RunOutcome.Stopped, at: T0.AddSeconds(30))));
        await Assert.That(row.State).IsEqualTo(RunState.Stopped);
        await Assert.That(row.StateText).IsEqualTo("stopped · 30s");
    }

    [Test]
    public async Task A_timed_out_shell_is_dated_from_its_call() {
        var clock = Clock();
        var s = new SessionRuns(clock);
        s.Apply(Signals(Shell("c1", at: T0)));
        clock.Advance(TimeSpan.FromMinutes(2));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "b1")));
        await Assert.That(Only(s).StartedAt).IsEqualTo(T0);
        await Assert.That(Only(s).StateText).IsEqualTo("running in background · 2m 00s");
    }

    [Test]
    public async Task A_detach_for_an_unseen_call_makes_no_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Detached("c9", "b9"), Finished("c9", "b9")));
        await Assert.That(s.Rows).IsEmpty();
    }

    [Test]
    public async Task A_shell_stopped_by_its_task_id_alone_ends() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.Apply(Signals(Finished(null, "b1", RunOutcome.Stopped)));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
    }

    [Test]
    public async Task Session_over_presents_a_running_shell_as_stopped() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.SessionOver = true;
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(s).StateText).IsEqualTo("stopped");
    }

    [Test]
    public async Task A_repeated_provisional_start_for_a_live_row_changes_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.Apply(Signals(Shell("c1")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).IsRunning).IsTrue();
    }

    [Test]
    public async Task Clear_drops_pending_shell_calls() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Clear();
        s.Apply(Signals(Detached("c1", "b1")));
        await Assert.That(s.Rows).IsEmpty();
    }

    [Test]
    public async Task An_agent_start_is_an_agent_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        await Assert.That(Only(s).Kind).IsEqualTo(RunKind.Agent);
        await Assert.That(Only(s).IsShell).IsFalse();
    }

    [Test]
    public async Task A_foreground_start_and_its_result_make_one_done_row() {
        var s = new SessionRuns(Clock());
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Started("c1")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");
        await Assert.That(Only(s).Description).IsEqualTo("Map the UI");
        await Assert.That(Only(s).IsBackground).IsFalse();
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Result("c1", at: T0.AddSeconds(48)));
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("48s");
        await Assert.That(changes).IsEqualTo(2);
    }

    [Test]
    public async Task A_background_launch_stays_running_past_its_result_until_the_notification() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(Only(s).IsBackground).IsTrue();
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(Only(s).StateText).IsEqualTo("running in background · 0s");
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", "a1", at: T0.AddMinutes(2).AddSeconds(41))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("2m 41s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_result_and_a_failed_notification_read_as_failed() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Result("c1", isError: true, at: T0.AddSeconds(48)));
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Failed);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("failed · 48s");

        s.Apply(Signals(Detached("c2", "a2")));
        s.Apply(Signals(Finished("c2", "a2", RunOutcome.Failed, at: T0.AddSeconds(62))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Failed);
        await Assert.That(s.Rows[1].StateText).IsEqualTo("failed · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_background_row_is_stopped_by_its_agent_id_alone() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", RunOutcome.Stopped, at: T0.AddSeconds(62))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(s).StateText).IsEqualTo("stopped · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    /// The server's stop lands ahead of the notification, which alone says how the run went.
    [Test]
    public async Task A_bare_stop_ends_a_background_row_as_done_until_a_notification_says_how() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddSeconds(62))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);

        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(90))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Failed);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(62));
        await Assert.That(Only(s).StateText).IsEqualTo("failed · 1m 02s");
    }

    /// The server dates a stop when it heard it: an import or a spooled hook lands it long after
    /// the run ended, and the notification's own time is the truer end.
    [Test]
    public async Task A_late_stamped_bare_stop_yields_to_the_earlier_end_that_says_how() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddDays(3))));
        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Done, at: T0.AddSeconds(90))));
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(90));
        await Assert.That(Only(s).StateText).IsEqualTo("1m 30s");
    }

    [Test]
    public async Task An_end_that_says_how_is_final_and_a_repeated_bare_stop_changes_nothing() {
        var said = new SessionRuns(Clock());
        said.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        said.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(10))));
        said.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddSeconds(20))));
        await Assert.That(Only(said).State).IsEqualTo(RunState.Failed);
        await Assert.That(Only(said).EndedAt).IsEqualTo(T0.AddSeconds(10));

        var bare = new SessionRuns(Clock());
        bare.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        bare.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddSeconds(10))));
        bare.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddSeconds(20))));
        await Assert.That(Only(bare).EndedAt).IsEqualTo(T0.AddSeconds(10));
        bare.Apply(Signals(Finished(null, "a1", RunOutcome.Stopped, at: T0.AddSeconds(30))));
        await Assert.That(Only(bare).State).IsEqualTo(RunState.Stopped);
        bare.Apply(Signals(Finished("c1", "a1", RunOutcome.Done, at: T0.AddSeconds(40))));
        await Assert.That(Only(bare).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(bare).EndedAt).IsEqualTo(T0.AddSeconds(10));
    }

    [Test]
    public async Task Duplicate_started_detached_and_finished_change_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c1", name: "Other")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");

        s.Apply(Signals(Detached("c1", "a1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished("c1", "a1", at: T0.AddSeconds(10))));
        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(20))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(10));

        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Result("c1", isError: true));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task A_signal_or_result_for_an_unknown_id_changes_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("zz", "a9"), Finished("zz", null), Finished(null, "a9"), Finished(null, null, RunOutcome.Stopped)));
        s.Apply(Result("zz"));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(Only(s).IsBackground).IsFalse();
    }

    [Test]
    public async Task A_finish_for_an_unseen_call_id_falls_back_to_the_bound_agent_id() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a")));
        await Assert.That(Only(s).IsBackground).IsTrue();
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c-unknown", "a", RunOutcome.Done, at: T0.AddMinutes(2))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task The_latest_detached_owns_the_agent_id_while_the_earlier_row_still_runs() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a")));
        s.Apply(Signals(Started("c2"), Detached("c2", "a")));
        await Assert.That(s.RunningCount).IsEqualTo(2);

        s.Apply(Signals(Finished(null, "a", RunOutcome.Done, at: T0.AddMinutes(2))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(1);
    }

    static SessionRuns SecondLaunch() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1", T0)));
        s.Apply(Signals(Detached("c1", "a")));
        s.Apply(Signals(Finished("c1", "a", at: T0.AddMinutes(1))));
        s.Apply(Signals(Started("c2", T0.AddMinutes(2))));
        s.Apply(Signals(Detached("c2", "a")));
        return s;
    }

    [Test]
    public async Task A_second_launch_of_the_same_agent_is_a_second_row_and_the_id_follows_it() {
        var byCall = SecondLaunch();
        await Assert.That(byCall.Rows).Count().IsEqualTo(2);
        await Assert.That(byCall.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(byCall.Rows[1].State).IsEqualTo(RunState.Running);
        byCall.Apply(Signals(Finished("c2", "a", at: T0.AddMinutes(3))));
        await Assert.That(byCall.Rows[1].State).IsEqualTo(RunState.Done);

        var byAgent = SecondLaunch();
        byAgent.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(byAgent.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(byAgent.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(1));
        await Assert.That(byAgent.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(byAgent.Rows[1].EndedAt).IsEqualTo(T0.AddMinutes(3));
    }

    [Test]
    public async Task After_a_second_launch_stale_signals_for_the_first_leave_the_second_running() {
        var repeated = SecondLaunch();
        repeated.Apply(Signals(Finished("c1", "a", at: T0.AddMinutes(3))));
        await Assert.That(repeated.Rows[1].State).IsEqualTo(RunState.Running);

        var delayed = SecondLaunch();
        delayed.Apply(Signals(Detached("c1", "a")));
        delayed.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(delayed.Rows[1].State).IsEqualTo(RunState.Done);

        var older = SecondLaunch();
        older.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(1).AddSeconds(30))));
        await Assert.That(older.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(older.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_later_result_for_a_detached_call_finishes_or_fails_the_row() {
        var done = new SessionRuns(Clock());
        done.Apply(Signals(Started("c1")));
        done.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(done.RunningCount).IsEqualTo(1);
        done.Apply(Result("c1", at: T0.AddSeconds(5)));
        await Assert.That(Only(done).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(done).EndedAt).IsEqualTo(T0.AddSeconds(5));

        var failed = new SessionRuns(Clock());
        failed.Apply(Signals(Started("c1")));
        failed.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        failed.Apply(Result("c1", isError: true));
        await Assert.That(Only(failed).State).IsEqualTo(RunState.Failed);
    }

    [Test]
    public async Task Two_results_in_one_projection_result_one_detached_and_one_not() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Mixed(
            [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1"), new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c2")],
            Detached("c1", "a1")));
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Running);
        await Assert.That(s.Rows[0].IsBackground).IsTrue();
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_drops_the_rows_and_the_bindings() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("c1", "a1")));
        var changes = 0;
        s.Changed += () => changes++;
        s.Clear();
        await Assert.That(s.Rows).IsEmpty();
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Signals(Started("c2")));
        s.Apply(Signals(Finished(null, "a1")));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
    }

    [Test]
    public async Task Session_over_presents_running_rows_as_stopped_without_ending_them() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        var changes = 0;
        s.Changed += () => changes++;

        s.SessionOver = true;
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("stopped");
        await Assert.That(s.Rows[0].Outcome).IsEqualTo(RunState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", null, at: T0.AddSeconds(30))));
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("30s");

        s.Apply(Signals(Started("c3")));
        await Assert.That(s.Rows[2].State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.RunningCount).IsEqualTo(0);

        s.SessionOver = false;
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(s.Rows[2].State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(2);
        s.SessionOver = false;
        await Assert.That(changes).IsEqualTo(4);
    }

    [Test]
    public async Task Running_count_follows_the_rows_and_rows_keep_arrival_order() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c2", name: "second"), Started("c1", name: "first"), Started("c3", name: "third")));
        await Assert.That(s.RunningCount).IsEqualTo(3);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
        s.Apply(Result("c1"));
        s.Apply(Result("c3", isError: true));
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_running_list_holds_the_rows_presenting_as_running_in_arrival_order() {
        var s = new SessionRuns(Clock());
        await Assert.That(s.Running).IsEmpty();

        s.Apply(Signals(Started("c1", name: "first"), Started("c2", name: "second"), Started("c3", name: "third")));
        await Assert.That(s.Running.Select(r => r.Name)).IsEquivalentTo(new[] { "first", "second", "third" }, CollectionOrdering.Matching);
        await Assert.That(ReferenceEquals(s.Running[1], s.Rows[1])).IsTrue();

        s.Apply(Result("c2"));
        await Assert.That(s.Running.Select(r => r.Name)).IsEquivalentTo(new[] { "first", "third" }, CollectionOrdering.Matching);

        s.SessionOver = true;
        await Assert.That(s.Running).IsEmpty();

        s.SessionOver = false;
        await Assert.That(s.Running.Select(r => r.Name)).IsEquivalentTo(new[] { "first", "third" }, CollectionOrdering.Matching);

        s.Clear();
        await Assert.That(s.Running).IsEmpty();
    }

    /// Taking a row out and putting it back would rebuild its container and restart its pulse,
    /// so a row that keeps running is never touched while others start and finish.
    [Test]
    public async Task A_row_that_keeps_running_keeps_its_place_while_others_start_and_finish() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2"), Started("c3")));
        var moved = new List<RunRow>();
        var resets = 0;
        s.Running.CollectionChanged += (_, e) => {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
            moved.AddRange((e.OldItems ?? Array.Empty<object>()).OfType<RunRow>());
            moved.AddRange((e.NewItems ?? Array.Empty<object>()).OfType<RunRow>());
        };

        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c2")], Started("c4")));
        s.Tick();

        await Assert.That(s.Running.Select(r => r.CallId)).IsEquivalentTo(new[] { "c1", "c3", "c4" }, CollectionOrdering.Matching);
        await Assert.That(resets).IsEqualTo(0);
        await Assert.That(moved.Select(r => r.CallId)).IsEquivalentTo(new[] { "c2", "c4" });
    }

    [Test]
    public async Task Every_presented_state_has_its_own_count_and_session_over_moves_running_to_stopped() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2"), Started("c3"), Started("c4"), Started("c5")));
        s.Apply(Result("c1"));
        s.Apply(Result("c2"));
        s.Apply(Result("c3", isError: true));
        s.Apply(Signals(Finished("c4", null, RunOutcome.Stopped)));
        await Assert.That(s.Count(RunState.Running)).IsEqualTo(1);
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(2);
        await Assert.That(s.Count(RunState.Failed)).IsEqualTo(1);
        await Assert.That(s.Count(RunState.Stopped)).IsEqualTo(1);

        s.SessionOver = true;
        await Assert.That(s.Count(RunState.Running)).IsEqualTo(0);
        await Assert.That(s.Count(RunState.Stopped)).IsEqualTo(2);

        s.Clear();
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(0);
        await Assert.That(s.Count(RunState.Stopped)).IsEqualTo(0);
    }

    /// The running count and the row count both stand still here, so only the per-state counts
    /// can tell a listener that the numbers it shows have moved.
    [Test]
    public async Task A_bare_stop_revised_to_failed_moves_the_counts_and_raises_changed() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddSeconds(62))));
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(1);
        var changes = 0;
        s.Changed += () => changes++;

        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(90))));
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(0);
        await Assert.That(s.Count(RunState.Failed)).IsEqualTo(1);
        await Assert.That(changes).IsEqualTo(1);
    }

    [Test]
    public async Task Elapsed_rolls_minutes_into_hours() {
        var clock = Clock();
        var s = new SessionRuns(clock);
        s.Apply(Signals(Started("c1", T0)));
        clock.Advance(TimeSpan.FromHours(25) + TimeSpan.FromMinutes(33) + TimeSpan.FromSeconds(30));
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("running · 25h 33m");
    }

    [Test]
    public async Task Elapsed_runs_on_the_clock_for_a_running_row_and_freezes_at_its_end() {
        var clock = Clock();
        var s = new SessionRuns(clock);
        s.Apply(Signals(Started("c1", T0)));
        await Assert.That(Only(s).StateText).IsEqualTo("running · 0s");

        clock.Advance(TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(18));
        await Assert.That(Only(s).StateText).IsEqualTo("running · 0s");
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("running · 6m 18s");

        var raised = new List<string?>();
        Only(s).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        s.Apply(Result("c1", at: T0.AddMinutes(7)));
        await Assert.That(Only(s).StateText).IsEqualTo("7m 00s");
        await Assert.That(raised).Contains(nameof(RunRow.State));
        await Assert.That(raised).Contains(nameof(RunRow.StateText));

        clock.Advance(TimeSpan.FromHours(1));
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("7m 00s");

        var unstamped = new SessionRuns(clock);
        unstamped.Apply(Signals(Started("c9", clock.GetUtcNow())));
        clock.Advance(TimeSpan.FromSeconds(9));
        unstamped.Apply(Result("c9"));
        await Assert.That(Only(unstamped).StateText).IsEqualTo("9s");
    }
}
