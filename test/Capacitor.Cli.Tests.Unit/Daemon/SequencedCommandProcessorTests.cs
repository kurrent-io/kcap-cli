using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class SequencedCommandProcessorTests {
    sealed class Harness {
        public readonly List<CommandAck> Acks = [];
        public readonly List<CommandRejected> Rejects = [];
        public readonly ConcurrentQueue<long> ExecOrder = new();
        public SequencedCommandProcessor P(string epoch = "e1", int bound = 256) => new(
            epoch, _ => AgentLiveness.Live,
            a => { lock (Acks) Acks.Add(a); return Task.CompletedTask; },
            r => { lock (Rejects) Rejects.Add(r); return Task.CompletedTask; },
            NullLogger.Instance, bound);
        public SequencedItem Launch(long seq, string epoch = "e1", string id = "cmd", string agent = "a")
            => new(SequencedKind.Launch, epoch, seq, id + seq, agent + seq);
    }

    [Test] public async Task Exact_next_commands_execute_serially_and_advance_the_watermark() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => { h.ExecOrder.Enqueue(1); return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await p.SubmitAsync(h.Launch(2), () => { h.ExecOrder.Enqueue(2); return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { 1L, 2L });
    }

    [Test] public async Task Out_of_order_command_is_not_accepted() {
        var h = new Harness(); await using var p = h.P();
        var ran = false;
        await p.SubmitAsync(h.Launch(2), () => { ran = true; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(0L); // Seq 2 while next is 1 -> not accepted
        await Assert.That(ran).IsFalse();
    }

    [Test] public async Task Execute_fault_becomes_internal_error_and_still_advances_the_watermark() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => throw new InvalidOperationException("boom"));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.InternalError);
    }

    [Test] public async Task Forced_item_creation_failure_synthesizes_a_terminal_item_and_advances_monotonically() {
        // Parent §8: forced item-creation failure AFTER counter reservation -> synthesized errored terminal
        // item at N, watermark advances. AND the monotonicity hazard: when the lane is completing while an
        // earlier accepted item is still draining, the synthesized advance must NOT jump past it (which the
        // draining item's later advance would then regress below).
        var h = new Harness(); await using var p = h.P();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Item 1 accepted + enqueued; its execute BLOCKS mid-flight (still draining).
        var t1 = p.SubmitAsync(h.Launch(1),
            async () => { started.SetResult(); await gate.Task; return new CommandOutcome(CommandOutcomeKind.LaunchExecuted); });
        await started.Task;                 // item 1 is dequeued and executing

        // Complete the lane while item 1 drains, then submit item 2 -> TryWrite fails -> SynthesizeErrorLocked.
        p.CompleteLaneForTest();
        var t2 = p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await t2;                           // the synthesized terminal completes immediately
        var afterSynth = p.LastProcessedSeq;
        await Assert.That(afterSynth).IsEqualTo(0L);   // synth at N=2 did NOT skip past the still-draining N=1

        gate.SetResult();                   // item 1 drains; contiguous prefix now reaches 1 then 2
        await t1;
        await Assert.That(afterSynth).IsLessThanOrEqualTo(p.LastProcessedSeq); // monotonic — never regressed
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);                    // contiguous prefix reaches 2
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.InternalError); // synth emitted the reject
    }

    [Test] public async Task Duplicate_of_a_processed_command_is_acked_with_outcome_and_live_state_not_reexecuted() {
        var h = new Harness(); await using var p = h.P();
        var runs = 0;
        var item = h.Launch(1);
        await p.SubmitAsync(item, () => { runs++; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "a", "sess")); });
        await p.SubmitAsync(item, () => { runs++; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(runs).IsEqualTo(1);                                    // no re-execution
        var ack = h.Acks.Single();
        await Assert.That(ack.State).IsEqualTo(CommandAckState.Processed);
        await Assert.That(ack.OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchExecuted);
        await Assert.That(ack.CurrentState).IsEqualTo(AgentLiveness.Live);       // read live at ack time
    }

    [Test] public async Task Different_command_id_at_an_accepted_seq_is_a_duplicate_collision() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        // Same Seq, different CommandId:
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 1, "OTHER", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DuplicateCollision);
    }

    [Test] public async Task Backpressure_rejects_when_the_cache_is_full_and_ack_prefix_reopens_capacity() {
        var h = new Harness(); await using var p = h.P(bound: 2);
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.Backpressure);
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(2L);       // 3 not accepted (unacked identity kept)

        p.AckPrefix(new AckProcessedPrefix("e1", 2));                // retire <= 2
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(3L);
    }

    [Test] public async Task AckPrefix_rejects_over_ahead_regressing_and_stale_epoch_without_eviction() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        p.AckPrefix(new AckProcessedPrefix("e1", 5));   // over-ahead (> LastProcessedSeq) -> ignored
        p.AckPrefix(new AckProcessedPrefix("WRONG", 1));// stale epoch -> ignored
        // A duplicate is still answerable (identity not evicted):
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Acks.Count).IsEqualTo(1);
    }

    [Test] public async Task Non_next_future_seq_is_rejected_wrong_next_without_accepting() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted))); // gap
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.WrongNext);
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(1L);
    }

    [Test] public async Task Execution_time_daemon_capacity_rejection_advances_watermark_and_emits_reject() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(
            new CommandOutcome(CommandOutcomeKind.LaunchRejected, "a", RejectReason: CommandRejectedReason.DaemonCapacity)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);        // rejected-as-item is terminal
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DaemonCapacity);
    }
}
