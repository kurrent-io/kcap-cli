using System.Text;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class CloudTerminalSinkTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    static readonly CloudTerminalSinkOptions Fast = new() {
        RetryDelay        = TimeSpan.FromMilliseconds(5),
        CancellationGrace = TimeSpan.FromMilliseconds(50),
    };

    sealed class Rig(CloudTerminalSinkOptions? options = null, TimeProvider? time = null) : IAsyncDisposable {
        public Lock                    SinksLock { get; } = new();
        public TerminalOutputBuffer    Ring      { get; } = new();
        public CloudTerminalSinkMirror Mirror    { get; } = new();
        public CancellationTokenSource Shutdown  { get; } = new();

        CloudTerminalSink? _sink;

        public CloudTerminalSink Sink => _sink ??= new CloudTerminalSink(
            "agent-1", SinksLock, Ring, Mirror.Send, () => Mirror.Ready,
            NullLogger.Instance, time ?? TimeProvider.System, options ?? Fast, Shutdown.Token);

        /// <summary>The read loop's fan-out: ring append and sink enqueue under one lock.</summary>
        public void Emit(string text) {
            var chunk = Encoding.UTF8.GetBytes(text);

            lock (SinksLock) {
                Ring.Append(chunk);
                Sink.TryEnqueue(chunk);
            }
        }

        public async ValueTask DisposeAsync() {
            if (_sink is { } sink) await sink.DisposeAsync();
            Shutdown.Dispose();
        }
    }

    /// <summary>Lets a test release sends one at a time.</summary>
    sealed class StepGate : IDisposable {
        readonly SemaphoreSlim _permits = new(0);

        public Task Wait(byte[] _, CancellationToken ct) => _permits.WaitAsync(ct);
        public void Release(int count = 1) => _permits.Release(count);
        public void Open() => _permits.Release(1_000_000);
        public void Dispose() => _permits.Dispose();
    }

    [Test]
    public async Task Chunks_reach_the_mirror_in_order() {
        await using var rig = new Rig();
        _ = rig.Sink;

        for (var i = 0; i < 50; i++) rig.Emit($"chunk-{i}");

        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        var sent = rig.Mirror.SentText();
        await Assert.That(sent.Length).IsEqualTo(50);
        for (var i = 0; i < 50; i++) await Assert.That(sent[i]).IsEqualTo($"chunk-{i}");
    }

    [Test]
    public async Task Nothing_is_sent_until_the_connection_is_ready() {
        await using var rig = new Rig();
        rig.Mirror.Ready = false;
        _ = rig.Sink;

        rig.Emit("a");
        rig.Emit("b");

        // Several retry delays pass; an ineligible sink must not have called the delegate.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await Assert.That(rig.Mirror.Entered).IsEqualTo(0);

        rig.Mirror.Ready = true;
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 2);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("a,b");

        await rig.Sink.StopAsync(TimeSpan.Zero).WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_send_that_fails_while_not_ready_is_held_and_delivered_in_order() {
        await using var rig   = new Rig();
        var             fails = 8;
        rig.Mirror.OnSend = (_, _) => {
            if (Interlocked.Decrement(ref fails) < 0) return Task.CompletedTask;

            rig.Mirror.Ready = false;
            _ = Task.Delay(20, CancellationToken.None).ContinueWith(_ => rig.Mirror.Ready = true, TaskScheduler.Default);

            throw new InvalidOperationException("transport down");
        };
        _ = rig.Sink;

        rig.Emit("a");
        rig.Emit("b");
        rig.Emit("c");

        // Wait for the held chunks to actually land before stopping: a stop issued while a chunk
        // is mid-retry now gives up on it the instant it next finds the connection not ready
        // (the sink is already completing), racing the very recovery this test exercises.
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 3);
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        var sent = rig.Mirror.SentText();
        await Assert.That(sent.Length).IsEqualTo(3);
        await Assert.That(string.Concat(sent)).IsEqualTo("abc");
    }

    [Test]
    public async Task Stop_cancels_a_send_blocked_in_the_transport_when_the_bound_passes() {
        var              time      = new FakeTimeProvider();
        await using var  rig       = new Rig(Fast, time);
        var              cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mirror.OnSend = async (_, ct) => {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        _ = rig.Sink;

        rig.Emit("stuck");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);

        var stop = rig.Sink.StopAsync(TimeSpan.FromSeconds(2));
        await Assert.That(stop.IsCompleted).IsFalse();

        time.Advance(TimeSpan.FromSeconds(2));

        await cancelled.Task.WaitAsync(HangGuard);
        await stop.WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_later_call_with_a_shorter_bound_shortens_the_pending_deadline() {
        var              time      = new FakeTimeProvider();
        await using var  rig       = new Rig(Fast, time);
        var              cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mirror.OnSend = async (_, ct) => {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        _ = rig.Sink;

        rig.Emit("stuck");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);

        var slow = rig.Sink.StopAsync(TimeSpan.FromSeconds(10));
        var fast = rig.Sink.StopAsync(TimeSpan.FromSeconds(1));
        await Assert.That(slow.IsCompleted).IsFalse();
        await Assert.That(fast.IsCompleted).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));

        await cancelled.Task.WaitAsync(HangGuard);
        await slow.WaitAsync(HangGuard);
        await fast.WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_later_call_with_a_longer_bound_does_not_extend_the_pending_deadline() {
        var              time      = new FakeTimeProvider();
        await using var  rig       = new Rig(Fast, time);
        var              cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mirror.OnSend = async (_, ct) => {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        _ = rig.Sink;

        rig.Emit("stuck");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);

        var fast = rig.Sink.StopAsync(TimeSpan.FromSeconds(1));
        var slow = rig.Sink.StopAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(1));

        await cancelled.Task.WaitAsync(HangGuard);
        await fast.WaitAsync(HangGuard);
        await slow.WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_stop_after_termination_arms_no_deadline_timer() {
        var             time = new FakeTimeProvider();
        await using var rig  = new Rig(Fast, time);

        // An idle sink terminates as soon as its channel completes, disposing what it owns.
        await rig.Sink.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(HangGuard);

        // A shorter bound would supersede the deadline — but nothing is left to dispose a new timer.
        await rig.Sink.StopAsync(TimeSpan.FromSeconds(1)).WaitAsync(HangGuard);

        await Assert.That(rig.Sink.HasDeadlineTimerForTest).IsFalse();
    }

    [Test]
    public async Task A_zero_bound_stop_shortens_a_stop_that_is_still_draining() {
        var             time = new FakeTimeProvider();
        await using var rig  = new Rig(Fast, time);
        rig.Mirror.OnSend = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        _ = rig.Sink;

        rig.Emit("stuck");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);

        var slow = rig.Sink.StopAsync(TimeSpan.FromSeconds(2));
        var fast = rig.Sink.StopAsync(TimeSpan.Zero);

        // No time advanced: only the zero bound can have ended the pump.
        await Task.WhenAll(slow, fast).WaitAsync(HangGuard);

        // A later caller finds the sink already terminated.
        await rig.Sink.StopAsync(TimeSpan.FromSeconds(2)).WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_send_that_ignores_its_token_is_abandoned_without_holding_the_stop() {
        var             time    = new FakeTimeProvider();
        var             log     = new CountingLogger();
        await using var rig     = new Rig();
        var             release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mirror.OnSend = async (_, _) => {
            await release.Task;

            throw new InvalidOperationException("faults after it was abandoned");
        };

        await using var sink = new CloudTerminalSink(
            "agent-1", rig.SinksLock, rig.Ring, rig.Mirror.Send, () => rig.Mirror.Ready,
            log, time, Fast, CancellationToken.None);

        lock (rig.SinksLock) sink.TryEnqueue("stuck"u8.ToArray());
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);

        // The grace timer is created after the stop call returns, so keep advancing until it fires.
        var stop = sink.StopAsync(TimeSpan.Zero);
        while (!stop.IsCompleted) {
            time.Advance(Fast.CancellationGrace);
            await Task.Delay(5);
        }

        await stop;
        await Assert.That(log.Warnings).IsEqualTo(1);
        await Assert.That(sink.PumpForTest.IsCompleted).IsFalse();

        // The abandoned send finishes later, and faults: the pump ends on its own, observed.
        release.SetResult();
        await sink.PumpForTest.WaitAsync(HangGuard);
    }

    sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger {
        int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Interlocked.Increment(ref _warnings);
        }
    }

    [Test]
    public async Task Daemon_shutdown_ends_a_pump_that_is_holding_a_chunk() {
        await using var rig = new Rig();
        rig.Mirror.Ready = false;
        _ = rig.Sink;

        rig.Emit("held");
        await rig.Shutdown.CancelAsync();

        // Shutdown alone must end the pump: no stop has been requested yet.
        await rig.Sink.PumpForTest.WaitAsync(HangGuard);
        await Assert.That(rig.Mirror.Entered).IsEqualTo(0);

        await rig.Sink.StopAsync(TimeSpan.Zero).WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_stop_while_not_ready_does_not_wait_out_the_drain_bound() {
        await using var rig = new Rig();
        rig.Mirror.Ready = false;
        _ = rig.Sink;

        rig.Emit("held");

        // A connection that never becomes ready cannot deliver this chunk, and the server deletes
        // the mirror when the agent unregisters — a long drain bound must not be waited out for it.
        await rig.Sink.StopAsync(TimeSpan.FromMinutes(10)).WaitAsync(HangGuard);
        await Assert.That(rig.Mirror.Entered).IsEqualTo(0);
    }

    [Test]
    public async Task A_backlog_past_the_budget_is_replaced_by_a_reset_and_the_ring() {
        using var gate = new StepGate();
        await using var rig = new Rig(Fast with { BacklogBudgetBytes = 8 });
        rig.Mirror.OnSend = gate.Wait;
        _ = rig.Sink;

        rig.Emit("aaaa");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1); // dequeued, parked on the gate
        rig.Emit("bbbb");
        rig.Emit("cccc"); // 8 queued: exactly the budget
        rig.Emit("dddd"); // would exceed it

        gate.Open();
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 6);
        rig.Emit("eeee");
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        // "aaaa" was already in the transport, so it may land before the reset. Nothing queued
        // before the overflow follows it.
        await Assert.That(string.Join(",", rig.Mirror.SentText()))
            .IsEqualTo("aaaa,<RIS>,aaaa,bbbb,cccc,dddd,eeee");
    }

    [Test]
    public async Task A_chunk_larger_than_the_budget_is_accepted_on_an_empty_queue() {
        await using var rig = new Rig(Fast with { BacklogBudgetBytes = 2 });
        _ = rig.Sink;

        rig.Emit("much-larger-than-two-bytes");
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("much-larger-than-two-bytes");
    }

    [Test]
    public async Task Appends_racing_resyncs_leave_no_gap_and_no_duplicate() {
        await using var rig = new Rig(Fast with { BacklogBudgetBytes = 64 });
        rig.Mirror.OnSend = async (_, _) => await Task.Yield();
        _ = rig.Sink;

        var emitted = new StringBuilder();
        for (var i = 0; i < 2_000; i++) {
            var text = $"c{i:D5};";
            emitted.Append(text);
            rig.Emit(text);
        }

        // The ring never evicted (14 KB total), so a terminal fed this stream must end up holding
        // every chunk exactly once, in order — whatever resyncs happened on the way. Wait for it
        // before stopping: a sink stopped while desynced exits without replaying.
        var expected = emitted.ToString();
        await WaitHarness.PollUntilAsync(() => string.Concat(rig.Mirror.Reconstruct()) == expected);

        await Assert.That(rig.Mirror.SentText().Count(t => t == "<RIS>")).IsGreaterThan(0);
        await rig.Sink.StopAsync(TimeSpan.Zero).WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_send_that_keeps_failing_while_ready_desyncs_even_with_an_empty_queue() {
        await using var rig = new Rig(Fast with { FailingSendAttempts = 3 });
        var failures = 3;
        rig.Mirror.OnSend = (_, _) =>
            Interlocked.Decrement(ref failures) >= 0
                ? throw new InvalidOperationException("rejected while connected")
                : Task.CompletedTask;
        _ = rig.Sink;

        rig.Emit("only");

        // The failing chunk was the last one; the pump must not park on the empty channel.
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 2);
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("<RIS>,only");
    }

    [Test]
    public async Task A_send_that_exhausts_after_the_sink_already_completed_logs_no_warning() {
        var             log     = new CountingLogger();
        await using var rig     = new Rig();
        var             entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             attempt = 0;
        rig.Mirror.OnSend = async (_, _) => {
            if (Interlocked.Increment(ref attempt) == 1) entered.TrySetResult();
            else await proceed.Task;

            throw new InvalidOperationException("always fails while ready");
        };

        await using var sink = new CloudTerminalSink(
            "agent-1", rig.SinksLock, rig.Ring, rig.Mirror.Send, () => rig.Mirror.Ready,
            log, TimeProvider.System, Fast with { FailingSendAttempts = 2 }, CancellationToken.None);

        lock (rig.SinksLock) sink.TryEnqueue("x"u8.ToArray());
        await entered.Task.WaitAsync(HangGuard);

        // StopAsync sets completion synchronously, before the gated (exhausting) attempt below can
        // run: the sink is already completed by the time the attempts run out.
        var stop = sink.StopAsync(TimeSpan.FromMinutes(10));
        proceed.SetResult();
        await stop.WaitAsync(HangGuard);

        await Assert.That(log.Warnings).IsEqualTo(0);
        await Assert.That(sink.WakeupsWrittenForTest).IsEqualTo(0L);
    }

    [Test]
    public async Task An_overflow_during_a_replay_does_not_interrupt_it() {
        using var gate = new StepGate();
        await using var rig = new Rig(Fast with { BacklogBudgetBytes = 4 });
        rig.Mirror.OnSend = gate.Wait;
        _ = rig.Sink;

        rig.Emit("r1");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);
        rig.Emit("r2");
        rig.Emit("r3");
        rig.Emit("r4"); // overflow → desync; ring = r1..r4

        gate.Release(2); // stale r1, then the reset
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 2);

        rig.Emit("x1");
        rig.Emit("x2");
        rig.Emit("x3"); // overflows again, mid-replay

        gate.Open();

        // Wait for the second resync to have begun before stopping: a completed sink never
        // begins a resync, so a stop racing the lock ahead of it would cut this short.
        await WaitHarness.PollUntilAsync(() => rig.Mirror.SentText().Count(t => t == "<RIS>") >= 2);
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        var sent = string.Join(",", rig.Mirror.SentText());
        await Assert.That(sent).IsEqualTo("r1,<RIS>,r1,r2,r3,r4,<RIS>,r1,r2,r3,r4,x1,x2,x3");
        await Assert.That(rig.Mirror.SentText().Count(t => t == "<RIS>")).IsEqualTo(2);
    }

    [Test]
    public async Task Sustained_overload_ends_synced_once_production_stops() {
        // A tight budget, not a slow pump: a yielding send still lets a fast producer outrun it,
        // so overload doesn't depend on wall-clock pacing.
        await using var rig = new Rig(Fast with { BacklogBudgetBytes = 8 });
        rig.Mirror.OnSend = async (_, _) => await Task.Yield();
        _ = rig.Sink;

        var emitted = new StringBuilder();
        for (var i = 0; i < 120; i++) {
            var text = $"o{i:D4};";
            emitted.Append(text);
            rig.Emit(text);
        }

        rig.Emit("tail;");
        emitted.Append("tail;");

        // Production has stopped: the last replay completes, the queue stays within budget, and
        // the mirror converges on everything emitted.
        var expected = emitted.ToString();
        await WaitHarness.PollUntilAsync(() => string.Concat(rig.Mirror.Reconstruct()) == expected);

        // The overload was real, not just possible in principle.
        await Assert.That(rig.Mirror.SentText().Count(t => t == "<RIS>")).IsGreaterThanOrEqualTo(1);

        rig.Emit("live;");
        await WaitHarness.PollUntilAsync(() => string.Concat(rig.Mirror.Reconstruct()) == expected + "live;");
        await rig.Sink.StopAsync(TimeSpan.Zero).WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_resync_request_on_an_idle_sink_wakes_the_pump() {
        await using var rig = new Rig();
        _ = rig.Sink;

        rig.Emit("a");
        rig.Emit("b");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 2);

        rig.Sink.RequestResync();

        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 5);
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("a,b,<RIS>,a,b");
    }

    [Test]
    public async Task A_resync_request_aborts_a_replay_in_progress() {
        using var gate = new StepGate();
        await using var rig = new Rig();
        rig.Mirror.OnSend = gate.Wait;
        _ = rig.Sink;

        lock (rig.SinksLock) {
            foreach (var t in new[] { "p1", "p2", "p3" }) rig.Ring.Append(Encoding.UTF8.GetBytes(t));
        }

        rig.Sink.RequestResync();
        gate.Release(2); // reset + p1

        // Wait until p2 is inside the transport: a request that lands before p2's send starts
        // would supersede p2 as well, and the sequence below would differ.
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 3);

        rig.Sink.RequestResync(); // the connection the replay started on is gone
        gate.Open();

        await WaitHarness.PollUntilAsync(() => rig.Mirror.SentText().Count(t => t == "<RIS>") == 2);
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        // p2 was already in the transport when the request arrived; p3 of the first replay never goes out.
        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("<RIS>,p1,p2,<RIS>,p1,p2,p3");
    }

    [Test]
    public async Task Many_resync_requests_against_a_held_pump_coalesce_into_one() {
        using var gate = new StepGate();
        await using var rig = new Rig();
        rig.Mirror.OnSend = gate.Wait;
        _ = rig.Sink;

        rig.Emit("a");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);

        for (var i = 0; i < 10_000; i++) rig.Sink.RequestResync();

        await Assert.That(rig.Sink.WakeupsWrittenForTest).IsEqualTo(1L);

        gate.Open();

        // Wait for the resync to have begun before stopping: a completed sink never begins a
        // resync, so a stop racing the lock ahead of it would cut this short.
        await WaitHarness.PollUntilAsync(() => rig.Mirror.SentText().Count(t => t == "<RIS>") == 1);
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("a,<RIS>,a");
    }

    [Test]
    public async Task A_stop_wins_over_a_resync_that_is_waiting_for_readiness() {
        await using var rig = new Rig();
        rig.Mirror.Ready = false;
        _ = rig.Sink;

        rig.Emit("a");
        rig.Sink.RequestResync();

        var stop = rig.Sink.StopAsync(HangGuard);
        rig.Mirror.Ready = true;
        await stop.WaitAsync(HangGuard);

        await Assert.That(rig.Mirror.Entered).IsEqualTo(0);
    }

    [Test]
    public async Task A_stop_lets_a_replay_that_has_begun_finish_inside_the_bound() {
        using var gate = new StepGate();
        await using var rig = new Rig();
        rig.Mirror.OnSend = gate.Wait;
        _ = rig.Sink;

        rig.Emit("a");
        rig.Emit("b");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);
        rig.Sink.RequestResync();

        gate.Release(2); // stale "a", then the reset: the replay has begun
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Sent.Count == 2);

        var stop = rig.Sink.StopAsync(HangGuard);
        gate.Open();
        await stop.WaitAsync(HangGuard);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("a,<RIS>,a,b");
    }

    [Test]
    public async Task Desync_warnings_are_limited_to_one_per_interval() {
        var time = new FakeTimeProvider();
        var log  = new CountingLogger();
        await using var rig = new Rig();
        await using var sink = new CloudTerminalSink(
            "agent-1", rig.SinksLock, rig.Ring, rig.Mirror.Send, () => rig.Mirror.Ready,
            log, time, Fast, CancellationToken.None);

        for (var i = 0; i < 5; i++) {
            sink.RequestResync();
            var resets = i + 1;
            await WaitHarness.PollUntilAsync(() => rig.Mirror.SentText().Count(t => t == "<RIS>") == resets);
        }

        await Assert.That(log.Warnings).IsEqualTo(1);

        time.Advance(Fast.WarningInterval);
        sink.RequestResync();
        await WaitHarness.PollUntilAsync(() => rig.Mirror.SentText().Count(t => t == "<RIS>") == 6);

        await Assert.That(log.Warnings).IsEqualTo(2);
        await sink.StopAsync(TimeSpan.Zero).WaitAsync(HangGuard);
    }
}
