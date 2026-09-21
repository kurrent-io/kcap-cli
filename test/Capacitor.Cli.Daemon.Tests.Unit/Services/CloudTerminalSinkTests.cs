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
        var             fails = 3;
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

        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);
        await Assert.That(rig.Mirror.Entered).IsEqualTo(0);
    }
}
