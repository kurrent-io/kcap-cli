using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The clock's subagent sets: what a report adds or removes, which reports it drops, and that
/// nothing here moves with time alone. Stamps are the hooks' wall clock and are inputs here; the
/// clock reads only its monotonic time.
public class AgentActivityClockSubagentTests {
    static readonly TimeSpan Liveness  = AgentActivityClock.SubagentLiveness;
    static readonly TimeSpan Retention = AgentActivityClock.SubagentStampRetention;
    static readonly TimeSpan Window    = AgentActivityClock.SubagentRestartWindow;

    static long Ms(FakeTimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();

    [Test]
    public async Task Null_before_any_report_and_a_number_after_the_first_of_either_kind() {
        var seen    = new AgentActivityClock(new FakeTimeProvider());
        var stopped = new AgentActivityClock(new FakeTimeProvider());

        await Assert.That(seen.LiveSubagents).IsNull();
        await Assert.That(stopped.LiveSubagents).IsNull();

        await Assert.That(seen.SubagentSeen("a", 1_000)).IsTrue();
        await Assert.That(seen.LiveSubagents).IsEqualTo(1);

        await Assert.That(stopped.SubagentStopped("a", 1_000)).IsTrue();
        await Assert.That(stopped.LiveSubagents).IsEqualTo(0);
    }

    [Test]
    public async Task Seen_refreshed_and_stopped_report_only_the_changes_to_the_live_set() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        await Assert.That(clock.SubagentSeen("a", 1_000)).IsTrue();
        await Assert.That(clock.SubagentSeen("a", 2_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
        await Assert.That(clock.SubagentStopped("a", 3_000)).IsTrue();
        await Assert.That(clock.SubagentStopped("a", 4_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
    }

    [Test]
    public async Task A_live_report_stamped_before_the_latest_applied_one_is_dropped_and_so_is_such_a_stop() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        clock.SubagentSeen("a", 1_000);
        clock.SubagentStopped("a", 3_000);
        await Assert.That(clock.SubagentSeen("a", 2_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);

        clock.SubagentSeen("b", 5_000);
        await Assert.That(clock.SubagentStopped("b", 4_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task A_start_stamped_within_the_window_after_its_stop_is_ignored_and_a_later_one_counts_again() {
        var clock  = new AgentActivityClock(new FakeTimeProvider());
        var stopAt = 10_000L;
        var window = (long) Window.TotalMilliseconds;

        await Assert.That(clock.SubagentStopped("a", stopAt)).IsTrue();
        await Assert.That(clock.SubagentSeen("a", stopAt + window)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
        await Assert.That(clock.SubagentSeen("a", stopAt + window + 1)).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task A_duplicate_stop_does_not_move_the_window() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        clock.SubagentSeen("a", 1_000);
        clock.SubagentStopped("a", 10_000);
        await Assert.That(clock.SubagentStopped("a", 40_000)).IsFalse();
        await Assert.That(clock.SubagentSeen("a", 45_000)).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    /// The bridge stamps a report that carries no sent_at with its arrival time, so such reports
    /// reach the clock in arrival order.
    [Test]
    public async Task A_report_stamped_on_arrival_is_ordered_by_its_arrival() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        clock.SubagentSeen("a", Ms(time));
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentStopped("a", Ms(time))).IsTrue();
        time.Advance(Window + TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentSeen("a", Ms(time))).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task After_a_backward_wall_clock_step_the_resumed_id_is_rejected_until_the_stops_stamp_expires() {
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);
        var stopAt = 1_000_000L;
        clock.SubagentSeen("a", stopAt - 1_000);
        clock.SubagentStopped("a", stopAt);
        var steppedBack = stopAt - (long) TimeSpan.FromMinutes(11).TotalMilliseconds;

        await Assert.That(clock.SubagentSeen("a", steppedBack)).IsFalse();
        time.Advance(Retention - TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentSeen("a", steppedBack + 1_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentSeen("a", steppedBack + 2_000)).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task The_count_does_not_move_with_time_alone() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));

        time.Advance(Liveness + TimeSpan.FromMinutes(1));

        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task Take_retires_each_aged_id_exactly_once_and_returns_the_time_to_the_oldest_survivor() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(TimeSpan.FromMinutes(1));
        clock.SubagentSeen("b", Ms(time));
        time.Advance(Liveness - TimeSpan.FromMinutes(1));

        var first = clock.TakeSubagentExpiries();
        await Assert.That(first.RetiredAny).IsTrue();
        await Assert.That(first.NextDue).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);

        var again = clock.TakeSubagentExpiries();
        await Assert.That(again.RetiredAny).IsFalse();
        await Assert.That(again.NextDue).IsEqualTo(TimeSpan.FromMinutes(1));

        time.Advance(TimeSpan.FromMinutes(1));
        var last = clock.TakeSubagentExpiries();
        await Assert.That(last.RetiredAny).IsTrue();
        await Assert.That(last.NextDue).IsNull();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
        await Assert.That(clock.TakeSubagentExpiries()).IsEqualTo(new SubagentExpiry(false, null));
    }

    [Test]
    public async Task A_heartbeat_renews_an_ids_deadline() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(TimeSpan.FromMinutes(9));

        await Assert.That(clock.SubagentSeen("a", Ms(time))).IsFalse();
        await Assert.That(clock.TakeSubagentExpiries().NextDue).IsEqualTo(Liveness);

        time.Advance(TimeSpan.FromMinutes(9));
        var expiry = clock.TakeSubagentExpiries();
        await Assert.That(expiry.RetiredAny).IsFalse();
        await Assert.That(expiry.NextDue).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task A_heartbeat_for_an_aged_id_not_yet_retired_keeps_it_counted_and_reports_no_change() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(Liveness + TimeSpan.FromMinutes(1));

        await Assert.That(clock.SubagentSeen("a", Ms(time))).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
        var expiry = clock.TakeSubagentExpiries();
        await Assert.That(expiry.RetiredAny).IsFalse();
        await Assert.That(expiry.NextDue).IsEqualTo(Liveness);
    }

    [Test]
    public async Task A_stop_for_an_aged_id_not_yet_retired_removes_it_and_reports_the_change() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(Liveness + TimeSpan.FromMinutes(1));

        await Assert.That(clock.SubagentStopped("a", Ms(time))).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
        await Assert.That(clock.TakeSubagentExpiries()).IsEqualTo(new SubagentExpiry(false, null));
    }

    [Test]
    public async Task The_parent_waiting_while_a_child_keeps_reporting_stays_waiting_with_the_child_counted() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetAwaitingInput(true);

        clock.SubagentSeen("child", Ms(time));
        time.Advance(TimeSpan.FromSeconds(30));
        clock.SubagentSeen("child", Ms(time));

        await Assert.That(clock.AwaitingInput).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task Subagent_reports_leave_the_wait_generation_the_seq_and_the_idle_time_alone() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetAwaitingInput(true);
        time.Advance(TimeSpan.FromSeconds(5));
        var (generation, seq, idle) = (clock.WaitGeneration, clock.ActivitySeq, clock.IdleForMs);

        clock.SubagentSeen("a", Ms(time));
        clock.SubagentStopped("a", Ms(time) + 1);
        clock.TakeSubagentExpiries();

        await Assert.That(clock.WaitGeneration).IsEqualTo(generation);
        await Assert.That(clock.ActivitySeq).IsEqualTo(seq);
        await Assert.That(clock.IdleForMs).IsEqualTo(idle);
        await Assert.That(clock.AwaitingInput).IsTrue();
    }

    [Test]
    public async Task No_input_or_turn_signal_alters_the_subagent_sets() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        clock.SetAwaitingInput(true);
        var sampled = clock.WaitGeneration;
        clock.SetAwaitingInput(true);

        clock.ClearAwaitingInputSince(sampled);
        await Assert.That(clock.AwaitingInput).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);

        clock.ClearAwaitingInputSince(clock.WaitGeneration);
        clock.SetTurnInFlight(true);
        clock.SetTurnInFlight(false);
        await Assert.That(clock.AwaitingInput).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }
}
