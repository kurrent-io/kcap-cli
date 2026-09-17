using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// The per-workspace tracker as a pure state machine over projection results: signals first,
/// then envelopes; an ended row is never reopened or re-ended.
public class SessionSubagentsTests {
    static readonly DateTimeOffset T0 = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    static FakeTimeProvider Clock() => new(T0);

    static ChatProjectionResult Signals(params SubagentSignal[] signals) => new([], [], signals);

    static ChatProjectionResult Result(string callId, bool isError = false, DateTimeOffset? at = null) =>
        new([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId, ToolIsError: isError, TimestampIso: at?.ToString("O"))], [], []);

    static ChatProjectionResult Mixed(IReadOnlyList<AcpEventEnvelope> envelopes, params SubagentSignal[] signals) => new(envelopes, [], signals);

    static SubagentSignal.Started Started(string callId, DateTimeOffset? at = null, string name = "Explore") =>
        new(callId, name, "Map the UI", at ?? T0);

    static SubagentSignal.Detached Detached(string callId, string agentId) => new(callId, agentId);

    static SubagentSignal.Finished Finished(string? callId, string? agentId, SubagentOutcome outcome = SubagentOutcome.Done, DateTimeOffset? at = null) =>
        new(callId, agentId, outcome, at ?? T0.AddMinutes(2));

    static SubagentRow Only(SessionSubagents s) => s.Rows.Single();

    [Test]
    public async Task A_foreground_start_and_its_result_make_one_done_row() {
        var s = new SessionSubagents(Clock());
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Started("c1")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");
        await Assert.That(Only(s).Description).IsEqualTo("Map the UI");
        await Assert.That(Only(s).IsBackground).IsFalse();
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Result("c1", at: T0.AddSeconds(48)));
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("48s");
        await Assert.That(changes).IsEqualTo(2);
    }

    [Test]
    public async Task A_background_launch_stays_running_past_its_result_until_the_notification() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(Only(s).IsBackground).IsTrue();
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", "a1", at: T0.AddMinutes(2).AddSeconds(41))));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("2m 41s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_result_and_a_failed_notification_read_as_failed() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Result("c1", isError: true, at: T0.AddSeconds(48)));
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Failed);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("failed · 48s");

        s.Apply(Signals(Detached("c2", "a2")));
        s.Apply(Signals(Finished("c2", "a2", SubagentOutcome.Failed, at: T0.AddSeconds(62))));
        await Assert.That(s.Rows[1].State).IsEqualTo(SubagentState.Failed);
        await Assert.That(s.Rows[1].StateText).IsEqualTo("failed · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_background_row_is_stopped_by_its_agent_id_alone() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", SubagentOutcome.Stopped, at: T0.AddSeconds(62))));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Stopped);
        await Assert.That(Only(s).StateText).IsEqualTo("stopped · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task Duplicate_started_detached_and_finished_change_nothing() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c1", name: "Other")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");

        s.Apply(Signals(Detached("c1", "a1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished("c1", "a1", at: T0.AddSeconds(10))));
        s.Apply(Signals(Finished("c1", "a1", SubagentOutcome.Failed, at: T0.AddSeconds(20))));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(10));

        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Result("c1", isError: true));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task A_signal_or_result_for_an_unknown_id_changes_nothing() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("zz", "a9"), Finished("zz", null), Finished(null, "a9"), Finished(null, null, SubagentOutcome.Stopped)));
        s.Apply(Result("zz"));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
        await Assert.That(Only(s).IsBackground).IsFalse();
    }

    static SessionSubagents SecondLaunch() {
        var s = new SessionSubagents(Clock());
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
        await Assert.That(byCall.Rows[0].State).IsEqualTo(SubagentState.Done);
        await Assert.That(byCall.Rows[1].State).IsEqualTo(SubagentState.Running);
        byCall.Apply(Signals(Finished("c2", "a", at: T0.AddMinutes(3))));
        await Assert.That(byCall.Rows[1].State).IsEqualTo(SubagentState.Done);

        var byAgent = SecondLaunch();
        byAgent.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(byAgent.Rows[0].State).IsEqualTo(SubagentState.Done);
        await Assert.That(byAgent.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(1));
        await Assert.That(byAgent.Rows[1].State).IsEqualTo(SubagentState.Done);
        await Assert.That(byAgent.Rows[1].EndedAt).IsEqualTo(T0.AddMinutes(3));
    }

    [Test]
    public async Task After_a_second_launch_stale_signals_for_the_first_leave_the_second_running() {
        var repeated = SecondLaunch();
        repeated.Apply(Signals(Finished("c1", "a", at: T0.AddMinutes(3))));
        await Assert.That(repeated.Rows[1].State).IsEqualTo(SubagentState.Running);

        var delayed = SecondLaunch();
        delayed.Apply(Signals(Detached("c1", "a")));
        delayed.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(delayed.Rows[1].State).IsEqualTo(SubagentState.Done);

        var older = SecondLaunch();
        older.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(1).AddSeconds(30))));
        await Assert.That(older.Rows[1].State).IsEqualTo(SubagentState.Running);
        await Assert.That(older.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_later_result_for_a_detached_call_finishes_or_fails_the_row() {
        var done = new SessionSubagents(Clock());
        done.Apply(Signals(Started("c1")));
        done.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(done.RunningCount).IsEqualTo(1);
        done.Apply(Result("c1", at: T0.AddSeconds(5)));
        await Assert.That(Only(done).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(done).EndedAt).IsEqualTo(T0.AddSeconds(5));

        var failed = new SessionSubagents(Clock());
        failed.Apply(Signals(Started("c1")));
        failed.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        failed.Apply(Result("c1", isError: true));
        await Assert.That(Only(failed).State).IsEqualTo(SubagentState.Failed);
    }

    [Test]
    public async Task Two_results_in_one_projection_result_one_detached_and_one_not() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Mixed(
            [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1"), new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c2")],
            Detached("c1", "a1")));
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.Rows[0].IsBackground).IsTrue();
        await Assert.That(s.Rows[1].State).IsEqualTo(SubagentState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_drops_the_rows_and_the_bindings() {
        var s = new SessionSubagents(Clock());
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
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
    }

    [Test]
    public async Task Session_over_presents_running_rows_as_stopped_without_ending_them() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        var changes = 0;
        s.Changed += () => changes++;

        s.SessionOver = true;
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Stopped);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("stopped");
        await Assert.That(s.Rows[0].Outcome).IsEqualTo(SubagentState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", null, at: T0.AddSeconds(30))));
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Done);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("30s");

        s.Apply(Signals(Started("c3")));
        await Assert.That(s.Rows[2].State).IsEqualTo(SubagentState.Stopped);
        await Assert.That(s.RunningCount).IsEqualTo(0);

        s.SessionOver = false;
        await Assert.That(s.Rows[1].State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.Rows[2].State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(2);
        s.SessionOver = false;
        await Assert.That(changes).IsEqualTo(3);
    }

    [Test]
    public async Task Running_count_follows_the_rows_and_rows_keep_arrival_order() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c2", name: "second"), Started("c1", name: "first"), Started("c3", name: "third")));
        await Assert.That(s.RunningCount).IsEqualTo(3);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
        s.Apply(Result("c1"));
        s.Apply(Result("c3", isError: true));
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Elapsed_runs_on_the_clock_for_a_running_row_and_freezes_at_its_end() {
        var clock = Clock();
        var s = new SessionSubagents(clock);
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
        await Assert.That(raised).Contains(nameof(SubagentRow.State));
        await Assert.That(raised).Contains(nameof(SubagentRow.StateText));

        clock.Advance(TimeSpan.FromHours(1));
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("7m 00s");

        var unstamped = new SessionSubagents(clock);
        unstamped.Apply(Signals(Started("c9", clock.GetUtcNow())));
        clock.Advance(TimeSpan.FromSeconds(9));
        unstamped.Apply(Result("c9"));
        await Assert.That(Only(unstamped).StateText).IsEqualTo("9s");
    }
}
