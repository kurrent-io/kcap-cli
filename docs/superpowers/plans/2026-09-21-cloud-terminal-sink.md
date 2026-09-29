# Cloud Terminal Sink Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the cloud terminal mirror from back-pressuring an agent's PTY read loop, so local clients stay responsive whatever the cloud lane is doing (#1022).

**Architecture:** Each agent registered with the server gets its own non-blocking `CloudTerminalSink`, fed under `SinksLock` beside the local sinks and drained by its own pump straight onto the hub. A backlog past 2 MB, a send that keeps failing, or a re-registration marks the sink desynced; the pump repairs the mirror in-band with `ESC c` followed by a replay of the daemon's output ring. The shared `TerminalOutputSender` is deleted.

**Tech Stack:** .NET 10, NativeAOT, SignalR client 10.0.12, TUnit on Microsoft Testing Platform, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/specs/2026-09-20-cloud-terminal-sink-design.md` — read it before starting; every rule below comes from it.

## Global Constraints

- The PTY read loop never awaits a consumer. No `await` on anything cloud-related inside the `await foreach` over `ReadOutputAsync`.
- Backlog budget is `TerminalOutputBuffer.MaxBytes` (2 MB raw bytes). Queued chunks count; the chunk being sent does not. A chunk arriving on an empty queue is always accepted.
- Reset bytes are exactly `0x1B 0x63`, sent as their own chunk. Replays send the ring's original chunks, never a flattened buffer.
- Send eligibility is `ServerConnection.IsReady`, never `IsConnected`.
- Defaults: retry delay 500 ms, failing-send attempts 5, drain bound 2 s, cancellation grace 500 ms, warning interval 1 min.
- A `--private` agent (`AgentInstance.IsPrivate`) gets no sink. Owner-only visibility agents are registered and do get one.
- Flags `_desynced`, `_abortReplay`, `_completed` are `volatile`, set only under `SinksLock`, cleared only by the pump under `SinksLock`. The byte counter changes only through `Interlocked`.
- One type per file. Comments are scarce: state a constraint a reader cannot see from the code, never history (see `CLAUDE.md` › Comments). Rewrite the comments you touch to that standard.
- Commit subjects: one imperative clause ending ` (#1022)`, at most 80 characters, plus the `Co-Authored-By` trailer the session specifies. Body only when it names a constraint the diff does not show.
- This checkout is a git worktree with a guard on bare `git`: run git as `/usr/bin/git -C <worktree-path> …`, one command per call, no heredocs.
- Run one test class with `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/<ClassName>/*"`. `--filter` and a bare `"*Name*"` glob match nothing.
- Tests assert through gates and `WaitHarness.PollUntilAsync`, never through wall-clock sleeps: the Windows and macOS CI legs fail timing budgets.

## File Structure

| File | Responsibility |
|---|---|
| Create `src/Capacitor.Cli.Daemon/Services/CloudTerminalSink.cs` | The sink: queue, pump, send routine, desync/resync, stop |
| Create `src/Capacitor.Cli.Daemon/Services/CloudTerminalSinkOptions.cs` | Tunables record |
| Modify `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` | `TerminalOutputBuffer.MaxBytes` public; `AgentInstance.CloudSink`; sink registry + admission; read loop fan-out and stop; `DisposeAsync`; `ReRegisterAgentsAsync` hook; drop `IsLocalSpawned` |
| Modify `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` | Drop the `IsLocalSpawned = true` assignment |
| Modify `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` | Raw `SendTerminalOutputAsync`; remove the shared sender |
| Delete `src/Capacitor.Cli.Daemon/Services/TerminalOutputSender.cs` | — |
| Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkTests.cs` | Sink unit tests |
| Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkMirror.cs` | Test double for the send delegate + mirror reconstruction |
| Delete `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TerminalOutputSenderTests.cs` | — |
| Create `test/Capacitor.Cli.Daemon.Tests.Unit/Pty/ScriptedPtyProcess.cs` | PTY fake that emits on demand |
| Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/RecordingTerminalSink.cs` | Local sink fake |
| Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorCloudSinkTests.cs` | Orchestrator regression |
| Modify `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CaptureServerConnection.cs` | Record and gate terminal sends; settable readiness; status failure injection |
| Modify `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorVendorTests.cs` | Migrate the stop test |
| Modify `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ServerConnectionDisposeTests.cs` | Replace the sender-CTS witness |
| Modify `test/Capacitor.App.Tests.Unit/TerminalTranscriptTests.cs` | RIS test for XTerm.NET |
| Modify `CLAUDE.md`, `docs/CHANGES.md` | Invariant + change note |

---

### Task 1: `CloudTerminalSink` — ordered delivery, eligibility, stop

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/CloudTerminalSinkOptions.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/CloudTerminalSink.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:399-403` (`TerminalOutputBuffer.MaxBytes`)
- Create: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkMirror.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkTests.cs`

**Interfaces:**
- Consumes: `ITerminalSink` (`void TryEnqueue(byte[] chunk); bool Detached { get; }`), `TerminalOutputBuffer` (`Append`, `GetAll`).
- Produces:
  - `internal sealed record CloudTerminalSinkOptions` with `long BacklogBudgetBytes`, `TimeSpan RetryDelay`, `int FailingSendAttempts`, `TimeSpan DrainBound`, `TimeSpan CancellationGrace`, `TimeSpan WarningInterval`.
  - `internal sealed partial class CloudTerminalSink : ITerminalSink` — ctor `(string agentId, Lock sinksLock, TerminalOutputBuffer ring, Func<string, string, CancellationToken, Task> send, Func<bool> isReady, ILogger logger, TimeProvider time, CloudTerminalSinkOptions options, CancellationToken shutdown)`; the ctor starts the pump. `void TryEnqueue(byte[] chunk)` (caller holds `sinksLock`), `Task StopAsync(TimeSpan drainBound)`.
  - `public const int TerminalOutputBuffer.MaxBytes`.
  - Test helper `CloudTerminalSinkMirror` (below), reused by Task 2.

- [ ] **Step 1: Expose the ring size**

In `AgentOrchestrator.cs`, change `TerminalOutputBuffer`'s constant:

```csharp
public class TerminalOutputBuffer {
    readonly List<byte[]> _chunks = [];
    int                   _totalBytes;
    public const int      MaxBytes = 2 * 1024 * 1024;
```

- [ ] **Step 2: Write the options record**

`src/Capacitor.Cli.Daemon/Services/CloudTerminalSinkOptions.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

internal sealed record CloudTerminalSinkOptions {
    // Past the ring's size a replay is cheaper than delivering the backlog.
    public long     BacklogBudgetBytes  { get; init; } = TerminalOutputBuffer.MaxBytes;
    public TimeSpan RetryDelay          { get; init; } = TimeSpan.FromMilliseconds(500);
    public int      FailingSendAttempts { get; init; } = 5;
    public TimeSpan DrainBound          { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan CancellationGrace   { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan WarningInterval     { get; init; } = TimeSpan.FromMinutes(1);
}
```

- [ ] **Step 3: Write the test double**

`test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkMirror.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Stands in for the hub send. <see cref="OnSend"/> runs before a chunk is recorded, so a
/// test can gate it, fail it, or watch its token.</summary>
sealed class CloudTerminalSinkMirror {
    static readonly byte[] ResetBytes = [0x1B, 0x63];

    int           _entered;
    volatile bool _ready = true;

    public ConcurrentQueue<byte[]>                Sent   { get; } = new();
    public Func<byte[], CancellationToken, Task>? OnSend { get; set; }
    public int                                    Entered => Volatile.Read(ref _entered);
    public bool                                   Ready   { get => _ready; set => _ready = value; }

    public async Task Send(string agentId, string base64, CancellationToken ct) {
        var bytes = Convert.FromBase64String(base64);
        Interlocked.Increment(ref _entered);
        if (OnSend is { } hook) await hook(bytes, ct);
        Sent.Enqueue(bytes);
    }

    public static bool IsReset(byte[] chunk) => chunk.AsSpan().SequenceEqual(ResetBytes);

    public string[] SentText() => [.. Sent.Select(c => IsReset(c) ? "<RIS>" : Encoding.UTF8.GetString(c))];

    /// <summary>What a terminal fed this stream would hold: everything after the last reset.</summary>
    public string[] Reconstruct() {
        var screen = new List<string>();

        foreach (var chunk in Sent) {
            if (IsReset(chunk)) screen.Clear();
            else screen.Add(Encoding.UTF8.GetString(chunk));
        }

        return [.. screen];
    }
}
```

- [ ] **Step 4: Write the failing tests**

`test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkTests.cs`:

```csharp
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

    sealed class Rig(CloudTerminalSinkOptions? options = null, TimeProvider? time = null) {
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
    }

    [Test]
    public async Task Chunks_reach_the_mirror_in_order() {
        var rig = new Rig();
        _ = rig.Sink;

        for (var i = 0; i < 50; i++) rig.Emit($"chunk-{i}");

        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        var sent = rig.Mirror.SentText();
        await Assert.That(sent.Length).IsEqualTo(50);
        for (var i = 0; i < 50; i++) await Assert.That(sent[i]).IsEqualTo($"chunk-{i}");
    }

    [Test]
    public async Task Nothing_is_sent_until_the_connection_is_ready() {
        var rig = new Rig();
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
        var rig   = new Rig();
        var fails = 3;
        rig.Mirror.OnSend = (_, _) => {
            if (Interlocked.Decrement(ref fails) < 0) return Task.CompletedTask;

            rig.Mirror.Ready = false;
            _ = Task.Delay(20).ContinueWith(_ => rig.Mirror.Ready = true);

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
        var time      = new FakeTimeProvider();
        var rig       = new Rig(Fast, time);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        var time = new FakeTimeProvider();
        var rig  = new Rig(Fast, time);
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
        var time    = new FakeTimeProvider();
        var log     = new CountingLogger();
        var rig     = new Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mirror.OnSend = async (_, _) => {
            await release.Task;

            throw new InvalidOperationException("faults after it was abandoned");
        };

        var sink = new CloudTerminalSink(
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
        var rig = new Rig();
        rig.Mirror.Ready = false;
        _ = rig.Sink;

        rig.Emit("held");
        await rig.Shutdown.CancelAsync();

        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);
        await Assert.That(rig.Mirror.Entered).IsEqualTo(0);
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/CloudTerminalSinkTests/*"`
Expected: build FAILS — `CloudTerminalSink` does not exist.

- [ ] **Step 6: Write the sink**

`src/Capacitor.Cli.Daemon/Services/CloudTerminalSink.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The cloud mirror's end of an agent's PTY fan-out. <see cref="TryEnqueue"/> never blocks, so a
/// slow or absent server cannot back-pressure the PTY; the pump sends one chunk at a time, in
/// order, and only to a connection that has finished registering.
/// </summary>
internal sealed partial class CloudTerminalSink : ITerminalSink {
    readonly string                                        _agentId;
    readonly Lock                                          _sinksLock;
    readonly TerminalOutputBuffer                          _ring;
    readonly Func<string, string, CancellationToken, Task> _send;
    readonly Func<bool>                                    _isReady;
    readonly ILogger                                       _logger;
    readonly TimeProvider                                  _time;
    readonly CloudTerminalSinkOptions                      _options;
    readonly CancellationTokenSource                       _pumpCts;
    readonly CancellationTokenSource                       _deadlineCts;
    readonly Task                                          _pump;

    readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    volatile bool _completed;
    long          _queuedBytes;
    long          _deadline = long.MaxValue;
    Task?         _termination;

    public CloudTerminalSink(
            string                                        agentId,
            Lock                                          sinksLock,
            TerminalOutputBuffer                          ring,
            Func<string, string, CancellationToken, Task> send,
            Func<bool>                                    isReady,
            ILogger                                       logger,
            TimeProvider                                  time,
            CloudTerminalSinkOptions                      options,
            CancellationToken                             shutdown
        ) {
        _agentId     = agentId;
        _sinksLock   = sinksLock;
        _ring        = ring;
        _send        = send;
        _isReady     = isReady;
        _logger      = logger;
        _time        = time;
        _options     = options;
        _pumpCts     = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _deadlineCts = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        _pump        = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    public bool Detached => _completed;

    internal Task PumpForTest => _pump;

    /// <summary>Caller holds the sinks lock.</summary>
    public void TryEnqueue(byte[] chunk) {
        if (_completed) return;

        Interlocked.Add(ref _queuedBytes, chunk.Length);
        _queue.Writer.TryWrite(chunk);
    }

    /// <summary>
    /// Idempotent and safe to call concurrently: every caller awaits the one termination, and the
    /// earliest deadline any of them asked for wins.
    /// </summary>
    public Task StopAsync(TimeSpan drainBound) {
        lock (_sinksLock) {
            if (!_completed) {
                _completed = true;
                _queue.Writer.TryComplete();
            }

            var immediate = drainBound <= TimeSpan.Zero;
            var deadline  = immediate
                ? long.MinValue
                : _time.GetTimestamp() + (long)(drainBound.TotalSeconds * _time.TimestampFrequency);

            if (deadline < _deadline) {
                _deadline = deadline;

                try {
                    if (immediate) _deadlineCts.Cancel();
                    else _deadlineCts.CancelAfter(drainBound);
                } catch (ObjectDisposedException) {
                    // Termination already finished.
                }
            }

            return _termination ??= TerminateAsync();
        }
    }

    async Task TerminateAsync() {
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (_deadlineCts.Token.Register(() => deadline.TrySetResult())) {
            await Task.WhenAny(_pump, deadline.Task);
        }

        if (!_pump.IsCompleted) {
            LogDrainCutShort(_agentId, Interlocked.Read(ref _queuedBytes));
            await _pumpCts.CancelAsync();
            await Task.WhenAny(_pump, Task.Delay(_options.CancellationGrace, _time));
        }

        if (_pump.IsCompleted) {
            DisposeSources();

            return;
        }

        // The send ignored its token. Finalization must not wait on it: let it finish on its own
        // and observe whatever it ends with.
        LogPumpAbandoned(_agentId);
        _ = _pump.ContinueWith(
            t => {
                _ = t.Exception;
                DisposeSources();
            },
            TaskScheduler.Default
        );
    }

    void DisposeSources() {
        _pumpCts.Dispose();
        _deadlineCts.Dispose();
    }

    async Task PumpAsync(CancellationToken ct) {
        try {
            while (true) {
                ct.ThrowIfCancellationRequested();

                if (_queue.Reader.TryRead(out var chunk)) {
                    Interlocked.Add(ref _queuedBytes, -chunk.Length);
                    await SendAsync(chunk, ct);

                    continue;
                }

                if (!await _queue.Reader.WaitToReadAsync(ct)) return;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Stop deadline or daemon shutdown.
        } catch (Exception ex) {
            LogPumpFaulted(ex, _agentId);
        }
    }

    async Task SendAsync(byte[] chunk, CancellationToken ct) {
        var base64 = Convert.ToBase64String(chunk);

        while (true) {
            if (!_isReady()) {
                await Task.Delay(_options.RetryDelay, _time, ct);

                continue;
            }

            try {
                await _send(_agentId, base64, ct);

                return;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                LogSendRetry(ex, _agentId);
                await Task.Delay(_options.RetryDelay, _time, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal output send for agent {AgentId} failed, holding the chunk and retrying")]
    partial void LogSendRetry(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal mirror drain for agent {AgentId} cut short with {QueuedBytes} bytes unsent")]
    partial void LogDrainCutShort(string agentId, long queuedBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal mirror pump for agent {AgentId} did not end after cancellation; abandoning it")]
    partial void LogPumpAbandoned(string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Terminal mirror pump for agent {AgentId} faulted")]
    partial void LogPumpFaulted(Exception ex, string agentId);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/CloudTerminalSinkTests/*"`
Expected: 7 passed, 0 failed. Run it three times; a test that fails once in three has a race — fix it, do not re-run past it.

- [ ] **Step 8: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli.Daemon/Services/CloudTerminalSink.cs src/Capacitor.Cli.Daemon/Services/CloudTerminalSinkOptions.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkMirror.cs
/usr/bin/git -C <worktree> commit -m "Add a per-agent cloud terminal sink with a bounded stop (#1022)"
```

---

### Task 2: Desync and resync

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/CloudTerminalSink.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkTests.cs`

**Interfaces:**
- Consumes: everything Task 1 produced.
- Produces: `public void RequestResync()` on `CloudTerminalSink` (takes the sinks lock itself). Budget enforcement inside `TryEnqueue`.

- [ ] **Step 1: Add a stepping gate to the test class**

Inside `CloudTerminalSinkTests`, next to `Rig`:

```csharp
    /// <summary>Lets a test release sends one at a time.</summary>
    sealed class StepGate {
        readonly SemaphoreSlim _permits = new(0);

        public Task Wait(byte[] _, CancellationToken ct) => _permits.WaitAsync(ct);
        public void Release(int count = 1) => _permits.Release(count);
        public void Open() => _permits.Release(1_000_000);
    }
```

- [ ] **Step 2: Write the failing tests**

Append to `CloudTerminalSinkTests`:

```csharp
    [Test]
    public async Task A_backlog_past_the_budget_is_replaced_by_a_reset_and_the_ring() {
        var gate = new StepGate();
        var rig  = new Rig(Fast with { BacklogBudgetBytes = 8 });
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
        var rig = new Rig(Fast with { BacklogBudgetBytes = 2 });
        _ = rig.Sink;

        rig.Emit("much-larger-than-two-bytes");
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("much-larger-than-two-bytes");
    }

    [Test]
    public async Task Appends_racing_resyncs_leave_no_gap_and_no_duplicate() {
        var rig = new Rig(Fast with { BacklogBudgetBytes = 64 });
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
        var rig      = new Rig(Fast with { FailingSendAttempts = 3 });
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
    public async Task An_overflow_during_a_replay_does_not_interrupt_it() {
        var gate = new StepGate();
        var rig  = new Rig(Fast with { BacklogBudgetBytes = 4 });
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
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        var sent = string.Join(",", rig.Mirror.SentText());
        await Assert.That(sent).StartsWith("r1,<RIS>,r1,r2,r3,r4,<RIS>,r1,r2,r3,r4,x1,x2,x3");
    }

    [Test]
    public async Task Sustained_overload_ends_synced_once_production_stops() {
        var rig = new Rig(Fast with { BacklogBudgetBytes = 32 });
        rig.Mirror.OnSend = (_, _) => Task.Delay(1);
        _ = rig.Sink;

        var emitted = new StringBuilder();
        for (var i = 0; i < 400; i++) {
            var text = $"o{i:D4};";
            emitted.Append(text);
            rig.Emit(text);
            if (i % 40 == 0) await Task.Yield();
        }

        rig.Emit("tail;");
        emitted.Append("tail;");

        // Production has stopped: the last replay completes, the queue stays within budget, and
        // the mirror converges on everything emitted.
        var expected = emitted.ToString();
        await WaitHarness.PollUntilAsync(() => string.Concat(rig.Mirror.Reconstruct()) == expected);

        rig.Emit("live;");
        await WaitHarness.PollUntilAsync(() => string.Concat(rig.Mirror.Reconstruct()) == expected + "live;");
        await rig.Sink.StopAsync(TimeSpan.Zero).WaitAsync(HangGuard);
    }

    [Test]
    public async Task A_resync_request_on_an_idle_sink_wakes_the_pump() {
        var rig = new Rig();
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
        var gate = new StepGate();
        var rig  = new Rig();
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
        var gate = new StepGate();
        var rig  = new Rig();
        rig.Mirror.OnSend = gate.Wait;
        _ = rig.Sink;

        rig.Emit("a");
        await WaitHarness.PollUntilAsync(() => rig.Mirror.Entered == 1);

        for (var i = 0; i < 10_000; i++) rig.Sink.RequestResync();

        await Assert.That(rig.Sink.WakeupsWrittenForTest).IsEqualTo(1L);

        gate.Open();
        await rig.Sink.StopAsync(HangGuard).WaitAsync(HangGuard);

        await Assert.That(string.Join(",", rig.Mirror.SentText())).IsEqualTo("a,<RIS>,a");
    }

    [Test]
    public async Task A_stop_wins_over_a_resync_that_is_waiting_for_readiness() {
        var rig = new Rig();
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
        var gate = new StepGate();
        var rig  = new Rig();
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
        var rig  = new Rig();
        var sink = new CloudTerminalSink(
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
```

`CountingLogger` is the one Task 1 added to this class.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/CloudTerminalSinkTests/*"`
Expected: build FAILS — `RequestResync` and `WakeupsWrittenForTest` do not exist.

- [ ] **Step 4: Add the desync state**

In `CloudTerminalSink.cs`, add beside the existing fields:

```csharp
    static readonly byte[] Wake  = [];
    static readonly byte[] Reset = [0x1B, 0x63];

    volatile bool _desynced;
    volatile bool _abortReplay;
    int           _wakePending;
    long          _wakeupsWritten;

    // Guarded by the sinks lock.
    long _desyncs;
    long _unreportedDesyncs;
    long _lastWarning;
    bool _warned;

    // A single-reader unbounded channel cannot be counted, so the writes are.
    internal long WakeupsWrittenForTest => Interlocked.Read(ref _wakeupsWritten);
```

Add the two entry points and the transition:

```csharp
    /// <summary>The connection this sink was writing to is gone, so anything written to it may
    /// not have arrived, and a replay running on it is wasted.</summary>
    public void RequestResync() {
        lock (_sinksLock) {
            if (_completed) return;

            MarkDesyncedLocked("connection change", abortReplay: true);
        }
    }

    void MarkDesyncedLocked(string cause, bool abortReplay) {
        var wasSynced = !_desynced;

        _desynced = true;
        if (abortReplay) _abortReplay = true;

        // One sentinel at most, however many requests arrive while the pump is held.
        if (Interlocked.Exchange(ref _wakePending, 1) == 0 && _queue.Writer.TryWrite(Wake)) {
            Interlocked.Increment(ref _wakeupsWritten);
        }

        if (wasSynced) NoteDesyncLocked(cause);
    }

    void NoteDesyncLocked(string cause) {
        _desyncs++;

        var now = _time.GetTimestamp();

        if (_warned && _time.GetElapsedTime(_lastWarning, now) < _options.WarningInterval) {
            _unreportedDesyncs++;

            return;
        }

        LogDesynced(_agentId, cause, _desyncs, _unreportedDesyncs);
        _unreportedDesyncs = 0;
        _lastWarning       = now;
        _warned            = true;
    }
```

- [ ] **Step 5: Enforce the budget in `TryEnqueue`**

Replace `TryEnqueue`:

```csharp
    /// <summary>Caller holds the sinks lock.</summary>
    public void TryEnqueue(byte[] chunk) {
        if (_completed || _desynced) return;

        var queued = Interlocked.Read(ref _queuedBytes);

        // An empty queue always accepts, so an overflow implies a pump with work to wake on.
        if (queued > 0 && queued + chunk.Length > _options.BacklogBudgetBytes) {
            MarkDesyncedLocked("backlog over budget", abortReplay: false);

            return;
        }

        Interlocked.Add(ref _queuedBytes, chunk.Length);
        _queue.Writer.TryWrite(chunk);
    }
```

- [ ] **Step 6: Replace the pump loop and the send routine, and add the resync**

Replace `PumpAsync` and `SendAsync`:

```csharp
    async Task PumpAsync(CancellationToken ct) {
        try {
            while (true) {
                ct.ThrowIfCancellationRequested();

                // Pending resync work comes before waiting on, or exiting from, the channel.
                if (_desynced) {
                    if (_completed || !await ResyncAsync(ct)) return;

                    continue;
                }

                if (_queue.Reader.TryRead(out var chunk)) {
                    if (ReferenceEquals(chunk, Wake)) {
                        Interlocked.Exchange(ref _wakePending, 0);

                        continue;
                    }

                    Interlocked.Add(ref _queuedBytes, -chunk.Length);
                    await SendAsync(chunk, replay: false, ct);

                    continue;
                }

                if (!await _queue.Reader.WaitToReadAsync(ct)) return;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Stop deadline or daemon shutdown.
        } catch (Exception ex) {
            LogPumpFaulted(ex, _agentId);
        }
    }

    /// <summary>False when the pump must exit: the sink completed before the replay began.</summary>
    async Task<bool> ResyncAsync(CancellationToken ct) {
        // Wait here rather than after the snapshot, so the snapshot is fresh when sending starts.
        while (!_isReady()) {
            if (_completed) return false;

            await Task.Delay(_options.RetryDelay, _time, ct);
        }

        List<byte[]> replay;

        lock (_sinksLock) {
            // Completion is set under this lock, so beginning a replay is atomic with it.
            if (_completed) return false;

            while (_queue.Reader.TryRead(out var stale)) {
                if (ReferenceEquals(stale, Wake)) Interlocked.Exchange(ref _wakePending, 0);
            }

            Interlocked.Exchange(ref _queuedBytes, 0);
            replay       = _ring.GetAll();
            _desynced    = false;
            _abortReplay = false;
        }

        if (!await SendAsync(Reset, replay: true, ct)) return true;

        foreach (var chunk in replay) {
            if (!await SendAsync(chunk, replay: true, ct)) return true;
        }

        LogResynced(_agentId, replay.Count);

        return true;
    }

    /// <summary>False when the chunk was given up because a desync superseded it. A budget
    /// overflow does not supersede a replay chunk; only <c>_abortReplay</c> does.</summary>
    async Task<bool> SendAsync(byte[] chunk, bool replay, CancellationToken ct) {
        var base64   = Convert.ToBase64String(chunk);
        var attempts = 0;

        while (true) {
            if (replay ? _abortReplay : _desynced) return false;

            if (!_isReady()) {
                attempts = 0;
                await Task.Delay(_options.RetryDelay, _time, ct);

                continue;
            }

            try {
                await _send(_agentId, base64, ct);

                return true;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                if (!_isReady()) {
                    attempts = 0;
                } else if (++attempts >= _options.FailingSendAttempts) {
                    lock (_sinksLock) MarkDesyncedLocked("send kept failing", abortReplay: true);

                    return false;
                }

                LogSendRetry(ex, _agentId);
                await Task.Delay(_options.RetryDelay, _time, ct);
            }
        }
    }
```

Add the two log methods beside the others:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal mirror for agent {AgentId} desynced ({Cause}); it will be reset and replayed. Desyncs so far: {Desyncs}, of which {Unreported} went unreported since the last warning")]
    partial void LogDesynced(string agentId, string cause, long desyncs, long unreported);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal mirror for agent {AgentId} resynced with a reset and {Chunks} replayed chunks")]
    partial void LogResynced(string agentId, int chunks);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/CloudTerminalSinkTests/*"`
Expected: 19 passed, 0 failed. Run it three times. If `An_overflow_during_a_replay_does_not_interrupt_it` or `A_resync_request_aborts_a_replay_in_progress` fails on its exact sequence, read the spec's "Stale chunks" rule before touching the assertion: exactly one in-flight chunk may precede a reset, and the test sequences are built on that.

- [ ] **Step 8: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli.Daemon/Services/CloudTerminalSink.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/CloudTerminalSinkTests.cs
/usr/bin/git -C <worktree> commit -m "Resync the cloud terminal sink with a reset and a ring replay (#1022)"
```

---

### Task 3: Wire the sink into the read loop and remove the shared sender

This task cannot be split: the read loop, `ServerConnection` and the deleted sender only compile together.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`AgentInstance` ~246-277; read loop ~2998-3084; `DisposeAsync` ~5395-5460)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs:339`
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (ctor ~355-360; fields ~418-429; `ConnectAsync` ~465-470; `SendTerminalOutputAsync`/`TrySendTerminalOutput` ~1608-1632; `DisposeAsync` ~1736-1800)
- Delete: `src/Capacitor.Cli.Daemon/Services/TerminalOutputSender.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TerminalOutputSenderTests.cs`
- Create: `test/Capacitor.Cli.Daemon.Tests.Unit/Pty/ScriptedPtyProcess.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/RecordingTerminalSink.cs`
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CaptureServerConnection.cs:329-347`
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorVendorTests.cs:1055-1093`
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ServerConnectionDisposeTests.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorCloudSinkTests.cs`

**Interfaces:**
- Consumes: `CloudTerminalSink` (ctor, `TryEnqueue`, `StopAsync`), `CloudTerminalSinkOptions`.
- Produces:
  - `AgentInstance.CloudSink` — `internal CloudTerminalSink? CloudSink { get; set; }` over a `volatile` field.
  - `AgentOrchestrator.CloudSinkOptionsForTest` — `internal CloudTerminalSinkOptions CloudSinkOptionsForTest { get; set; } = new();`
  - `AgentOrchestrator.TryStartCloudSink(AgentInstance agent)` — `internal CloudTerminalSink?`, null for a private agent or a closed registry.
  - `ServerConnection.SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default)` — now the raw hub send.
  - Test fakes: `ScriptedPtyProcess` (`Emit(string)`, `Exit()`), `RecordingTerminalSink` (`Chunks`), and on `CaptureServerConnection`: `TerminalSends`, `TerminalSendStarts`, `TerminalSendGate`, `Ready`.

- [ ] **Step 1: Write the PTY and local-sink fakes**

`test/Capacitor.Cli.Daemon.Tests.Unit/Pty/ScriptedPtyProcess.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

/// <summary>Emits what the test tells it to, when it tells it to, and ends on <see cref="Exit"/>.</summary>
sealed class ScriptedPtyProcess : IPtyProcess {
    readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();

    public int  Pid       => 4343;
    public bool HasExited => _output.Reader.Completion.IsCompleted;
    public int? ExitCode  => 0;

    public void Emit(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    public void Exit()            => _output.Writer.TryComplete();

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => _output.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync() => default;
    public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? _) {
        Exit();

        return Task.CompletedTask;
    }

    public Task WriteAsync(string _) => Task.CompletedTask;
    public Task WriteAsync(byte[] _) => Task.CompletedTask;
    public void Resize(ushort     _, ushort __) { }
    public void SendInterrupt() { }
}
```

Remove the unused `System.Runtime.CompilerServices` import if the build flags it (IDE0005 is a build error here).

`test/Capacitor.Cli.Daemon.Tests.Unit/Services/RecordingTerminalSink.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

sealed class RecordingTerminalSink : ITerminalSink {
    public ConcurrentQueue<string> Chunks { get; } = new();

    public bool Detached => false;

    public void TryEnqueue(byte[] chunk) => Chunks.Enqueue(Encoding.UTF8.GetString(chunk));
}
```

- [ ] **Step 2: Extend `CaptureServerConnection`**

Replace the block from the `SendEntered` doc comment through the end of `SendTerminalOutputAsync` (lines ~329-347), and replace the fixed `internal override bool IsReady => true;` (line ~140):

```csharp
    /// <summary>Settable so a test can hold the daemon inside its registration bracket.</summary>
    public bool Ready { get; set; } = true;

    internal override bool IsReady => Ready;
```

```csharp
    /// <summary>Set both to make every terminal send block until its <c>ct</c> cancels.</summary>
    public TaskCompletionSource? SendEntered   { get; init; }
    public TaskCompletionSource? SendUnblocked { get; init; }

    /// <summary>Runs before a terminal send is recorded; a test gates or fails the send here.</summary>
    public Func<string, CancellationToken, Task>? TerminalSendGate { get; set; }

    public ConcurrentQueue<(string AgentId, byte[] Data)> TerminalSends { get; } = new();

    int _terminalSendStarts;
    public int TerminalSendStarts => Volatile.Read(ref _terminalSendStarts);

    public override async Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) {
        Interlocked.Increment(ref _terminalSendStarts);

        if (SendEntered is not null) {
            SendEntered.TrySetResult();

            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            } finally {
                SendUnblocked?.TrySetResult();
            }
        }

        if (TerminalSendGate is { } gate) await gate(agentId, ct);

        TerminalSends.Enqueue((agentId, Convert.FromBase64String(base64Data)));
    }
```

Add `using System.Collections.Concurrent;` if the file lacks it. The blocked send now lets its cancellation propagate: the sink's pump expects a cancelled send to throw.

- [ ] **Step 3: Write the failing orchestrator tests**

`test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorCloudSinkTests.cs`:

```csharp
using System.Text;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the read loop's fan-out against a slow or blocked cloud lane: local sinks and the PTY
/// drain never wait on it, one agent's backlog is its own, and the mirror recovers with a reset.
/// </summary>
public class AgentOrchestratorCloudSinkTests {
    static readonly byte[] ResetBytes = [0x1B, 0x63];

    static readonly CloudTerminalSinkOptions SmallBudget = new() {
        BacklogBudgetBytes = 64,
        RetryDelay         = TimeSpan.FromMilliseconds(5),
        DrainBound         = TimeSpan.FromMilliseconds(100),
        CancellationGrace  = TimeSpan.FromMilliseconds(100),
    };

    static AgentOrchestrator Build(CaptureServerConnection server) {
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.CloudSinkOptionsForTest = SmallBudget;

        return orch;
    }

    static RecordingTerminalSink AttachLocal(AgentInstance agent) {
        var sink = new RecordingTerminalSink();
        lock (agent.SinksLock) agent.LocalSinks.Add(sink);

        return sink;
    }

    static string[] MirrorOf(CaptureServerConnection server, string agentId) {
        var screen = new List<string>();

        foreach (var (id, data) in server.TerminalSends) {
            if (id != agentId) continue;

            if (data.AsSpan().SequenceEqual(ResetBytes)) screen.Clear();
            else screen.Add(Encoding.UTF8.GetString(data));
        }

        return [.. screen];
    }

    static int ResetsFor(CaptureServerConnection server, string agentId) =>
        server.TerminalSends.Count(s => s.AgentId == agentId && s.Data.AsSpan().SequenceEqual(ResetBytes));

    [Test]
    public async Task A_blocked_cloud_lane_stops_neither_local_output_nor_the_pty_drain() {
        var open   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new CaptureServerConnection { TerminalSendGate = (_, ct) => open.Task.WaitAsync(ct) };

        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("hosted", pty: pty); // server-launched shape: not private
        var local = AttachLocal(agent);
        var loop  = orch.ReadAgentOutputForTest(agent);

        var expected = new StringBuilder();
        for (var i = 0; i < 200; i++) {
            var text = $"line-{i:D4};";
            expected.Append(text);
            pty.Emit(text);
        }

        // 2 KB against a 64-byte cloud budget, with every cloud send blocked: the local sink
        // still receives all of it, which also proves the runtime was drained.
        await WaitHarness.PollUntilAsync(() => local.Chunks.Count == 200);
        await Assert.That(string.Concat(local.Chunks)).IsEqualTo(expected.ToString());

        open.SetResult();
        await WaitHarness.PollUntilAsync(() => string.Concat(MirrorOf(server, "hosted")) == expected.ToString());

        // Recovery went through a reset, and at most the one in-flight chunk preceded it.
        var firstReset = server.TerminalSends.ToList().FindIndex(s => s.Data.AsSpan().SequenceEqual(ResetBytes));
        await Assert.That(firstReset).IsGreaterThanOrEqualTo(0);
        await Assert.That(firstReset).IsLessThanOrEqualTo(1);

        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task One_agents_blocked_cloud_lane_does_not_touch_another_agent() {
        var never  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new CaptureServerConnection {
            TerminalSendGate = (agentId, ct) => agentId == "noisy" ? never.Task.WaitAsync(ct) : Task.CompletedTask
        };

        await using var orch = Build(server);

        var noisyPty = new ScriptedPtyProcess();
        var quietPty = new ScriptedPtyProcess();
        var noisy    = orch.SeedAgentForTest("noisy", pty: noisyPty);
        var quiet    = orch.SeedAgentForTest("quiet", pty: quietPty);
        var local    = AttachLocal(quiet);
        var loops    = new[] { orch.ReadAgentOutputForTest(noisy), orch.ReadAgentOutputForTest(quiet) };

        for (var i = 0; i < 200; i++) noisyPty.Emit($"flood-{i:D4};"); // far past noisy's budget
        quietPty.Emit("echo");

        await WaitHarness.PollUntilAsync(() => local.Chunks.Count == 1);
        await WaitHarness.PollUntilAsync(() => server.TerminalSends.Any(s => s.AgentId == "quiet"));

        await Assert.That(string.Concat(MirrorOf(server, "quiet"))).IsEqualTo("echo");
        await Assert.That(ResetsFor(server, "quiet")).IsEqualTo(0);

        noisyPty.Exit();
        quietPty.Exit();
        await Task.WhenAll(loops).WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task A_private_agent_never_sends_terminal_output() {
        var server = new CaptureServerConnection();

        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("private", isPrivate: true, pty: pty);
        var local = AttachLocal(agent);
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit("secret");
        await WaitHarness.PollUntilAsync(() => local.Chunks.Count == 1);
        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);

        await Assert.That(server.TerminalSendStarts).IsEqualTo(0);
        await Assert.That(agent.CloudSink).IsNull();
    }

    [Test]
    public async Task No_terminal_send_starts_after_the_agent_unregisters() {
        // OnAgentUnregistered is init-only, so the callback reaches the connection through the
        // variable it is being assigned to.
        var startsAtUnregister = -1;
        CaptureServerConnection? server = null;
        server = new CaptureServerConnection {
            TerminalSendGate    = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            OnAgentUnregistered = () => startsAtUnregister = server!.TerminalSendStarts
        };

        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("ending", pty: pty);
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit("one");
        pty.Emit("two");
        await WaitHarness.PollUntilAsync(() => server.TerminalSendStarts == 1);

        // The read loop ends with the cloud blocked: it must reach finalization within the stop
        // deadline (drain bound + cancellation grace), not wait for the transport.
        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);

        await Assert.That(startsAtUnregister).IsEqualTo(1);
        await Assert.That(server.TerminalSendStarts).IsEqualTo(1);
    }

    [Test]
    public async Task Disposal_cancels_a_gated_send_even_while_its_read_loop_is_draining() {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server    = new CaptureServerConnection {
            TerminalSendGate = async (_, ct) => {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
        };

        // Disposed explicitly below; the run-once guard makes the scope's second dispose a no-op.
        await using var orch = Build(server);
        orch.CloudSinkOptionsForTest = SmallBudget with { DrainBound = TimeSpan.FromMinutes(10) };

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("draining", pty: pty);
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit("stuck");
        await WaitHarness.PollUntilAsync(() => server.TerminalSendStarts == 1);
        pty.Exit(); // the read loop is now inside its own ten-minute StopAsync

        await orch.DisposeAsync().AsTask().WaitAsync(WaitHarness.Bounded);

        await Assert.That(cancelled.Task.IsCompleted).IsTrue();
        await loop.WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task No_sink_is_admitted_once_disposal_has_begun() {
        var server = new CaptureServerConnection();

        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("late", pty: new ScriptedPtyProcess());

        await orch.DisposeAsync().AsTask().WaitAsync(WaitHarness.Bounded);

        await Assert.That(orch.TryStartCloudSink(agent)).IsNull();
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorCloudSinkTests/*"`
Expected: build FAILS — `CloudSinkOptionsForTest`, `CloudSink` and `TryStartCloudSink` do not exist.

- [ ] **Step 5: Make `ServerConnection.SendTerminalOutputAsync` the raw send and remove the sender**

In `ServerConnection.cs`:

1. Delete the `_terminalSender = new TerminalOutputSender(…)` statement at the end of the constructor (~355-360).
2. Delete `TerminalSenderCtsForTests`, `_terminalSender`, `_terminalSenderTask`, `_terminalSenderCts` (~418-429).
3. In `ConnectAsync`, delete the comment and the two statements that create the linked CTS and start `RunAsync`:

```csharp
    public async Task ConnectAsync(CancellationToken ct) {
        _ct                 = ct;
        _eventProcessorTask = ProcessEventQueueAsync(ct);
        await ConnectWithRetryAsync(ct);
    }
```

4. Replace `SendTerminalOutputAsync` and delete `TrySendTerminalOutput`:

```csharp
    /// <summary>
    /// One terminal chunk, straight to the hub. Throws when the send fails and honours
    /// <paramref name="ct"/> while a write is blocked in the transport; the caller owns ordering
    /// and retry. Cancelling ends the wait, not necessarily the delivery — the bytes may already
    /// be in the transport pipe.
    /// </summary>
    public virtual Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) =>
        _hub.SendAsync("SendTerminalOutput", new TerminalOutput(agentId, base64Data), ct);
```

5. In `DisposeAsync`: delete `var cts = _terminalSenderCts;`, the `_terminalSender.Complete();` line, the whole `try { … } catch (Exception ex) { LogDisposeStepFailed(ex, "terminal-sender"); }` block, and the `try { cts?.Dispose(); } catch … "terminal-sender-cts"` block in the `finally`. The event-processor step, `_httpClient`, and `_hub.DisposeAsync()` stay exactly as they are.

Then delete `src/Capacitor.Cli.Daemon/Services/TerminalOutputSender.cs` and `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TerminalOutputSenderTests.cs`.

- [ ] **Step 6: Replace the dispose tests' witness**

`ServerConnectionDisposeTests` used the sender's CTS as proof that the `finally` ran. A disposed hub rejects a send with `ObjectDisposedException`, which is the same proof with no seam. In both `Second_dispose_does_not_reenter_the_body_and_the_cts_ends_cancelled_and_disposed` and `A_faulting_awaited_task_is_logged_and_later_cleanup_still_runs`:

- delete `var cts = conn.TerminalSenderCtsForTests;` and `await Assert.That(cts).IsNotNull();`
- replace the two CTS assertions with:

```csharp
        await Assert.That(async () => await conn.SendTerminalOutputAsync("agent", "AA=="))
            .Throws<ObjectDisposedException>();
```

Rename the first test to `Second_dispose_does_not_reenter_the_body_and_the_hub_ends_disposed`. Update the class doc comment and `DisposeTestConnection`'s comment so neither mentions a terminal-sender CTS: `IsReady` is overridden so `ConnectAsync` returns without a server.

- [ ] **Step 7: Add the sink to `AgentInstance` and drop `IsLocalSpawned`**

In `AgentOrchestrator.cs`, inside `AgentInstance`, after `SinksLock`:

```csharp
    volatile CloudTerminalSink? _cloudSink;

    /// <summary>The cloud mirror's sink while the read loop runs; null for a private agent. Kept
    /// out of <see cref="LocalSinks"/>, which feeds the local dims clamp.</summary>
    internal CloudTerminalSink? CloudSink { get => _cloudSink; set => _cloudSink = value; }
```

Delete the `IsLocalSpawned` property with its doc comment (~270-277), and the `IsLocalSpawned = true,` line in `AgentOrchestrator.LocalIpc.cs:339`.

- [ ] **Step 8: Add the sink registry and admission to the orchestrator**

Beside the orchestrator's other fields:

```csharp
    // Keyed by the sink, not the agent: a sink is tracked until its own stop returns, which can be
    // after its agent left _agents.
    readonly Lock                       _cloudSinksLock = new();
    readonly HashSet<CloudTerminalSink> _cloudSinks     = [];
    bool                                _cloudSinksClosed;

    internal CloudTerminalSinkOptions CloudSinkOptionsForTest { get; set; } = new();

    /// <summary>Null for a private agent, and once shutdown has closed the registry. Constructing
    /// the sink starts its pump, so admission under the lock leaves no untracked pump and no
    /// tracked sink that has not started.</summary>
    internal CloudTerminalSink? TryStartCloudSink(AgentInstance agent) {
        if (agent.IsPrivate) return null;

        lock (_cloudSinksLock) {
            if (_cloudSinksClosed) return null;

            var sink = new CloudTerminalSink(
                agent.Id, agent.SinksLock, agent.OutputBuffer,
                _server.SendTerminalOutputAsync, () => _server.IsReady,
                _logger, _time, CloudSinkOptionsForTest, _shutdownCts.Token);

            _cloudSinks.Add(sink);

            return sink;
        }
    }
```

Use the orchestrator's existing logger field — check its name at the top of the class (`_logger` or `logger`) and match it.

- [ ] **Step 9: Rewrite the read loop's fan-out and its `finally`**

In `ReadAgentOutputAsync`:

1. Delete the opening comment block and the `using var sendCts = …` statement. In their place:

```csharp
        var cloud = TryStartCloudSink(agent);
        agent.CloudSink = cloud;
```

2. Replace the fan-out block and the whole `if (!agent.IsPrivate) { … }` that follows it with:

```csharp
                // Ring append and every sink's enqueue happen under one lock, paired with attach
                // and with the cloud sink's resync taking their snapshots under it: a chunk lands
                // in exactly one of a snapshot and the live stream. Every TryEnqueue is
                // non-blocking — the read loop never awaits a consumer.
                lock (agent.SinksLock) {
                    agent.OutputBuffer.Append(data);
                    foreach (var sink in agent.LocalSinks) sink.TryEnqueue(data);
                    cloud?.TryEnqueue(data);
                }
```

3. Replace the `finally` block:

```csharp
        } finally {
            if (cloud is not null) {
                // Before finalization: the server deletes the mirror when the agent unregisters,
                // so nothing may be sent after that.
                try {
                    await cloud.StopAsync(_shutdownCts.IsCancellationRequested ? TimeSpan.Zero : CloudSinkOptionsForTest.DrainBound);
                } catch (Exception ex) {
                    LogOutputReadError(ex, agent.Id);
                }

                lock (_cloudSinksLock) _cloudSinks.Remove(cloud);
                agent.CloudSink = null;
            }

            // On daemon shutdown the hub is going away and DisposeAsync owns local cleanup; the
            // server ends the sessions itself when the daemon disconnects.
            if (!_shutdownCts.IsCancellationRequested) {
                await FinalizeAgentRunAsync(agent);
            }
        }
```

`FailWedgedLaunchAsync` returns out of the loop and reaches this `finally` unchanged.

- [ ] **Step 10: Stop every sink in `DisposeAsync`**

In `AgentOrchestrator.DisposeAsync`, immediately after the `try { await _shutdownCts.CancelAsync(); } catch … "shutdown-cancel"` block:

```csharp
            // Before the hub is disposed (DaemonRunner disposes it after this method returns):
            // close admission, then end every pump, including one whose read loop is mid-drain.
            try {
                CloudTerminalSink[] sinks;

                lock (_cloudSinksLock) {
                    _cloudSinksClosed = true;
                    sinks             = [.. _cloudSinks];
                }

                await Task.WhenAll(sinks.Select(s => s.StopAsync(TimeSpan.Zero)));
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "cloud-sinks");
            }
```

- [ ] **Step 11: Migrate the stop test**

In `AgentOrchestratorVendorTests.cs`, rename `Stopping_an_agent_releases_a_read_loop_blocked_on_a_full_terminal_queue` to `Stopping_an_agent_with_the_cloud_blocked_cancels_its_send_before_it_unregisters`. `OnAgentUnregistered` is init-only, so it goes in the connection's initializer:

```csharp
        var unblockedAtUnregister = false;

        var server = new CaptureServerConnection {
            SendEntered         = sendEntered,
            SendUnblocked       = sendUnblocked,
            OnAgentUnregistered = () => unblockedAtUnregister = sendUnblocked.Task.IsCompleted
        };
```

After `BuildOrchestrator`, add:

```csharp
        orch.CloudSinkOptionsForTest = new CloudTerminalSinkOptions {
            DrainBound        = TimeSpan.FromMilliseconds(100),
            CancellationGrace = TimeSpan.FromMilliseconds(100),
        };
```

Replace the comments and the tail of the test:

```csharp
        // The pump, not the read loop, is parked in the blocked send.
        await sendEntered.Task.WaitAsync(WaitHarness.Bounded);

        // Stopping ends the read loop at once; its sink gets the drain bound, then its send is
        // cancelled, and only then does the agent finalize and unregister.
        await orch.HandleStopAgentForTest("agent-bp");

        await sendUnblocked.Task.WaitAsync(WaitHarness.Bounded);
        await WaitHarness.PollUntilAsync(() => server.AgentUnregisteredCalls.Count == 1);
        await Assert.That(unblockedAtUnregister).IsTrue();
```

Rewrite the comment above `var sendEntered` to say the send blocks until its token cancels and the PTY keeps the stream open, with no mention of a queue.

- [ ] **Step 12: Build the solution and run the affected suites**

Run: `dotnet build Capacitor.slnx`
Expected: 0 errors, 0 warnings. A single-project build is not enough — other test projects may reference what was removed.

Run: `rtk proxy grep -rn -E "TerminalOutputSender|TrySendTerminalOutput|IsLocalSpawned|TerminalSenderCtsForTests" src test`
Expected: no matches.

Run each, expecting all tests to pass:

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorCloudSinkTests/*"
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerConnectionDisposeTests/*"
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorLocalAttachTests/*"
```

- [ ] **Step 13: Commit**

```bash
/usr/bin/git -C <worktree> add -A src/Capacitor.Cli.Daemon test/Capacitor.Cli.Daemon.Tests.Unit
/usr/bin/git -C <worktree> commit -m "Feed the cloud mirror from a per-agent sink off the read loop (#1022)" -m "The sink stops before finalization because the server deletes an agent's terminal buffer on unregister."
```

---

### Task 4: Resync after re-registration

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`ReRegisterAgentsAsync` ~4900-4985)
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/CaptureServerConnection.cs` (`AgentStatusChangedAsync` ~287)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorCloudSinkTests.cs`

**Interfaces:**
- Consumes: `AgentInstance.CloudSink`, `CloudTerminalSink.RequestResync()`, `CaptureServerConnection.Ready`, the existing `CaptureServerConnection.AgentRegisteredFailTimes` and `AgentOrchestrator.ReRegisterAgentsForTestAsync()`.
- Produces: `CaptureServerConnection.StatusChangedThrow` — `public Exception? StatusChangedThrow { get; set; }`.

- [ ] **Step 1: Add the status failure injection**

In `CaptureServerConnection`, above `AgentStatusChangedAsync`, add:

```csharp
    /// <summary>Thrown by every AgentStatusChangedAsync call while set.</summary>
    public Exception? StatusChangedThrow { get; set; }
```

and as the first statement of `AgentStatusChangedAsync`:

```csharp
        if (StatusChangedThrow is { } ex) return Task.FromException(ex);
```

- [ ] **Step 2: Write the failing tests**

Append to `AgentOrchestratorCloudSinkTests`:

```csharp
    static async Task<(ScriptedPtyProcess Pty, Task Loop)> StartWithOutputAsync(
            AgentOrchestrator orch, CaptureServerConnection server, string agentId) {
        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest(agentId, pty: pty);
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit("before;");
        await WaitHarness.PollUntilAsync(() => server.TerminalSends.Count(s => s.AgentId == agentId) == 1);

        return (pty, loop);
    }

    [Test]
    public async Task A_re_registered_agent_gets_one_reset_and_replay_once_the_daemon_is_ready() {
        var server = new CaptureServerConnection();

        await using var orch = Build(server);
        var (pty, loop) = await StartWithOutputAsync(orch, server, "rereg");

        // The registration bracket: readiness is down while agents re-register.
        server.Ready = false;
        await orch.ReRegisterAgentsForTestAsync();
        pty.Emit("during;");
        await Assert.That(ResetsFor(server, "rereg")).IsEqualTo(0);

        server.Ready = true;
        await WaitHarness.PollUntilAsync(() => string.Concat(MirrorOf(server, "rereg")) == "before;during;");
        await Assert.That(ResetsFor(server, "rereg")).IsEqualTo(1);

        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task A_registration_that_landed_is_resynced_even_when_its_status_report_never_does() {
        var server = new CaptureServerConnection();

        await using var orch = Build(server);
        var (pty, loop) = await StartWithOutputAsync(orch, server, "mixed");

        server.Ready              = false;
        server.StatusChangedThrow = new InvalidOperationException("status report rejected");
        await orch.ReRegisterAgentsForTestAsync(); // gives up after its retries

        server.StatusChangedThrow = null;
        server.Ready              = true;
        await WaitHarness.PollUntilAsync(() => ResetsFor(server, "mixed") == 1);
        await WaitHarness.PollUntilAsync(() => string.Concat(MirrorOf(server, "mixed")) == "before;");

        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task An_agent_whose_registration_never_succeeded_is_not_resynced() {
        var server = new CaptureServerConnection { AgentRegisteredFailTimes = int.MaxValue };

        await using var orch = Build(server);
        var (pty, loop) = await StartWithOutputAsync(orch, server, "refused");

        await orch.ReRegisterAgentsForTestAsync();

        pty.Emit("after;");
        await WaitHarness.PollUntilAsync(() => server.TerminalSends.Count(s => s.AgentId == "refused") == 2);
        await Assert.That(ResetsFor(server, "refused")).IsEqualTo(0);

        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);
    }
```

If `AgentRegisteredFailTimes` is `init`-only, the object initializer above is already correct; if it is a different type than `int`, use that type's maximum.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorCloudSinkTests/*"`
Expected: the first two new tests FAIL (no reset ever arrives; `PollUntilAsync` times out); the third passes.

- [ ] **Step 4: Request the resync**

In `ReRegisterAgentsAsync`, directly after the `await _server.AgentRegisteredAsync(…);` statement and before the comment that starts "Re-gate the status send":

```csharp
                    // The acknowledgement is what re-registered means: whatever was written to
                    // the old connection may not have arrived, and that holds even if the sends
                    // below then fail. The pump holds the replay until readiness returns.
                    agent.CloudSink?.RequestResync();
```

- [ ] **Step 5: Rewrite the reconnect comment**

Further down the same method, replace the comment block that begins "do NOT replay the full output buffer here" (above the `break;`) with:

```csharp
                    // No replay from here: the agent's cloud sink was asked to resync above, and
                    // it sends a reset ahead of the ring on the same ordered lane as live output.
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorCloudSinkTests/*"`
Expected: 9 passed, 0 failed.

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorFinalizerVerdictTests/*"`
Expected: all pass — these drive `ReRegisterAgentsForTestAsync` too.

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/CaptureServerConnection.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorCloudSinkTests.cs
/usr/bin/git -C <worktree> commit -m "Resync an agent's terminal mirror after it re-registers (#1022)" -m "A chunk written just before a connection drops can be lost with no error, so every connection change ends in a reset and a replay."
```

---

### Task 5: Pin the desktop terminal's reset handling

**Files:**
- Test: `test/Capacitor.App.Tests.Unit/TerminalTranscriptTests.cs` (after `Feed_renders_text_into_the_model`, ~line 346)

**Interfaces:**
- Consumes: `XtermTerminalSurface(int cols, int rows)`, `Feed(string)`, `Model.Terminal.Engine.GetLine(int)`, `AvaloniaSession.DispatchAsync`.
- Produces: nothing.

- [ ] **Step 1: Write the test**

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_reset_clears_what_was_fed_before_it() {
        // The daemon repairs a desynced mirror with ESC c followed by a replay, so a remote
        // viewer that ignored the reset would paint the replay over stale content.
        var (before, after) = await AvaloniaSession.DispatchAsync(() => {
            var surface = new XtermTerminalSurface(cols: 80, rows: 24);
            surface.Feed("stale");
            var first = surface.Model.Terminal.Engine.GetLine(0);

            surface.Feed("\u001bcfresh");

            return (first, surface.Model.Terminal.Engine.GetLine(0));
        });

        await Assert.That(before).IsEqualTo("stale");
        await Assert.That(after).IsEqualTo("fresh");
    }
```

Write the escape as `\u001b`, not `\x1b`: C#'s `\x` is greedy and `"\x1bc"` is the single character U+01BC.

- [ ] **Step 2: Run it**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/TerminalTranscriptTests/*"`
Expected: all pass, including the new test — XTerm.NET 1.2.0 routes `ESC c` to a full terminal reset.

If the new test fails, stop and report it: the design depends on remote viewers honouring the reset, and the fix belongs in `XtermTerminalSurface.Feed`, which is a decision for the person running this plan.

- [ ] **Step 3: Commit**

```bash
/usr/bin/git -C <worktree> add test/Capacitor.App.Tests.Unit/TerminalTranscriptTests.cs
/usr/bin/git -C <worktree> commit -m "Pin the desktop terminal clearing on a full reset (#1022)"
```

---

### Task 6: Invariant, change note, and full verification

**Files:**
- Modify: `CLAUDE.md` (the `## Invariants` list)
- Modify: `docs/CHANGES.md` (new entry at the top, under the intro)
- Modify: `src/Capacitor.Cli.Daemon/Services/ITerminalSink.cs`, `src/Capacitor.Cli.Daemon/Services/LocalSocketSink.cs` (comments only, if they still describe the old cloud path)

**Interfaces:** none.

- [ ] **Step 1: Add the invariant**

In `CLAUDE.md`, after the bullet that begins "**The daemon never stops a process in its own process group.**", add:

```markdown
- **The PTY read loop never awaits a consumer.** Local sinks and the cloud sink take chunks through
  a non-blocking `TryEnqueue` under `SinksLock`, and each drains on its own pump. A consumer that
  falls behind is cut off and replayed from the ring — a local client by reattaching, the cloud
  mirror by an in-band terminal reset — never allowed to back-pressure the PTY, which would freeze
  every other surface of that agent.
```

- [ ] **Step 2: Add the change note**

In `docs/CHANGES.md`, as the first `##` entry:

```markdown
## The cloud terminal mirror has its own lane per agent

An agent's PTY is drained by one loop that feeds every surface. When that loop awaited a shared
cloud queue, a slow server — or another agent's output filling the queue — froze `kcap agent
attach` and the desktop app along with the web mirror. Each registered agent now has a
`CloudTerminalSink`: the read loop hands it chunks without waiting, and its own pump sends them.

The mirror is a cursor-addressed byte stream, so a dropped chunk garbles everything after it, and
the hub protocol has no resynchronisation message. The sink repairs the mirror in-band instead: a
terminal reset (`ESC c`) followed by the daemon's 2 MB output ring, on the same ordered lane as
live output. That is what a local client gets by reattaching, with the same limits — the ring can
begin mid-sequence, and anything painted before its horizon and never repainted is gone. The
backlog budget equals the ring's size because past it a replay is cheaper than the backlog.

A replay runs to completion even if the backlog overflows again behind it; abandoning it would
let an agent that outruns the transport reset the mirror forever without ever painting it. Only a
send that keeps failing, or a connection change, abandons a replay.

Every re-registration asks for a resync. A chunk written just before a connection dies can be lost
with no error, and the server drops output from a connection that has not re-registered the agent,
so the pump waits for full readiness and then replays. The earlier reconnect replay garbled the
terminal because it had no reset ahead of it and raced the live sends; this one has neither flaw.

The sink stops before an agent finalizes, not after: the server deletes an agent's terminal buffer
when it unregisters, so output sent later is discarded. Ordered delivery still rests on the server
handling one connection's messages in arrival order, which holds by the shape of its hub method
rather than by any guarantee — the daemon's per-agent sends are exactly as serial as before.
```

- [ ] **Step 3: Sweep the comments the change made stale**

Run: `rtk proxy grep -rn -i -E "back-pressure|backpressure|only consumer|TerminalOutputSender|re-syncs from the server" src/Capacitor.Cli.Daemon`
Expected after editing: no comment describes the cloud lane as able to back-pressure the PTY, and none names the deleted sender. In `LocalSocketSink.cs`, reduce the type comment to what is true now: the producer never blocks, a full queue force-detaches the client because losing a chunk mid-stream desyncs a redraw TUI, and a detached client reattaches for a fresh replay. In `ITerminalSink.cs`, keep the two-line comment; it already holds.

- [ ] **Step 4: Verify the whole change**

Run: `dotnet build Capacitor.slnx`
Expected: 0 errors, 0 warnings.

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`
Expected: 0 failed. Known environmental failures on this Mac are process-flood and wall-clock budget tests unrelated to terminals; re-run any such failure alone before attributing it, and report it either way.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/TerminalTranscriptTests/*"`
Expected: all pass.

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. Do the same for the daemon project: `dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — also no output.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C <worktree> add CLAUDE.md docs/CHANGES.md src/Capacitor.Cli.Daemon/Services/LocalSocketSink.cs src/Capacitor.Cli.Daemon/Services/ITerminalSink.cs
/usr/bin/git -C <worktree> commit -m "Record why the PTY read loop never awaits a consumer (#1022)"
```
