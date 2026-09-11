using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Unit tests for the daemon-side heartbeat tick. The loop runs every ~15 s
/// and must, in three distinct cases:
///
/// <list type="bullet">
/// <item>healthy — server returns true; do nothing.</item>
/// <item>slot displaced — server returns false; re-register so the
/// orchestrator-visible registry tracks the live conn id.</item>
/// <item>connection hung — ping throws or times out; force a SignalR
/// reconnect by stopping the hub so <c>WithAutomaticReconnect</c> kicks in.
/// This is the failure mode that wedged staging: the server timed the
/// daemon out, but the daemon's WebSocket transport never noticed.</item>
/// </list>
///
/// The loop is exercised through a fake <see cref="IDaemonHeartbeatPort"/>
/// so we don't need a real SignalR HubConnection.
/// </summary>
public class DaemonHeartbeatLoopTests {
    sealed class FakePort : IDaemonHeartbeatPort {
        public Func<CancellationToken, Task<bool>>? PingHandler              { get; set; }
        public Func<Task>?                          ReRegisterHandler        { get; set; }
        public Func<Task>?                          ForceReconnectHandler    { get; set; }
        public int                                  ReRegisterCalls;
        public int                                  ForceReconnectCalls;
        public int                                  PingCalls;
        // Defaults to the Connected steady state the ping and force-reconnect cases assume.
        public bool                                 IsConnected { get; set; } = true;

        public Task<bool> PingAsync(CancellationToken ct) {
            PingCalls++;

            return PingHandler is null ? Task.FromResult(true) : PingHandler(ct);
        }

        public Task ReRegisterAsync() {
            ReRegisterCalls++;

            return ReRegisterHandler is null ? Task.CompletedTask : ReRegisterHandler();
        }

        public Task ForceReconnectAsync() {
            ForceReconnectCalls++;

            return ForceReconnectHandler is null ? Task.CompletedTask : ForceReconnectHandler();
        }
    }

    static DaemonHeartbeatLoop CreateLoop(FakePort port, TimeSpan? deadline = null)
        => new(port, deadline ?? TimeSpan.FromSeconds(10), NullLogger.Instance);

    /// <summary>Minimal <see cref="ILogger"/> that records the rendered message and level of every entry.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    [Test]
    public async Task Tick_HealthyPing_RecordsRttAtDebug() {
        var logger = new CaptureLogger();
        var port   = new FakePort { PingHandler = _ => Task.FromResult(true) };
        var loop   = new DaemonHeartbeatLoop(port, TimeSpan.FromSeconds(5), logger);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
        await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Debug && e.Message.Contains("DaemonPing ok") && e.Message.Contains("RTT"));
    }

    [Test]
    public async Task Tick_SlowButSuccessfulPing_WarnsBeforeDeadlineWithoutReconnecting() {
        var logger = new CaptureLogger();

        // Ping succeeds, but takes longer than the slow threshold (and less than
        // the deadline) — the loop must warn about climbing latency yet NOT force
        // a reconnect, since the ping still came back in time.
        var port = new FakePort {
            PingHandler = async ct => {
                await Task.Delay(60, ct);

                return true;
            }
        };
        var loop = new DaemonHeartbeatLoop(
            port,
            pingDeadline: TimeSpan.FromSeconds(5),
            logger,
            slowPingThreshold: TimeSpan.FromMilliseconds(20)
        );

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
        await Assert.That(port.ReRegisterCalls).IsEqualTo(0);
        await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Warning && e.Message.Contains("DaemonPing slow"));
    }

    [Test]
    public async Task Tick_DeadlineExceeded_LogsCauseAndForcesReconnect() {
        var logger = new CaptureLogger();
        var port = new FakePort {
            PingHandler = ct => {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));

                return tcs.Task;
            }
        };
        var loop = new DaemonHeartbeatLoop(port, TimeSpan.FromMilliseconds(50), logger);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ForceReconnectCalls).IsEqualTo(1);
        await Assert.That(logger.Entries).Contains(e => e.Message.Contains("cause=ping_deadline_exceeded"));
    }

    [Test]
    public async Task Tick_ServerSaysHealthy_DoesNothing() {
        var port = new FakePort { PingHandler = _ => Task.FromResult(true) };
        var loop = CreateLoop(port);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ReRegisterCalls).IsEqualTo(0);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Tick_ServerSaysFalse_ReRegistersWithoutForcingReconnect() {
        var port = new FakePort { PingHandler = _ => Task.FromResult(false) };
        var loop = CreateLoop(port);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ReRegisterCalls).IsEqualTo(1);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Tick_PingThrows_StandsDownForAutomaticReconnect() {
        // A ping that throws means SignalR already knows the connection is unusable; OnClosed and
        // automatic reconnect own recovery. Forcing here would race that, so the loop stands down.
        var port = new FakePort {
            PingHandler = _ => Task.FromException<bool>(new InvalidOperationException("connection is not active"))
        };
        var loop = CreateLoop(port);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ReRegisterCalls).IsEqualTo(0);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Tick_PingExceedsDeadline_ForcesReconnect() {
        // The ping never returns. The loop's per-tick deadline must fire so
        // the daemon doesn't sit on a hung WebSocket forever.
        var port = new FakePort {
            PingHandler = ct => {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));

                return tcs.Task;
            }
        };
        var loop = CreateLoop(port, deadline: TimeSpan.FromMilliseconds(50));

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ReRegisterCalls).IsEqualTo(0);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Tick_ForceReconnectThrows_DoesNotRethrow() {
        // ForceReconnectAsync ultimately calls _hub.StopAsync, which can throw (invalid state,
        // transient SignalR cancellation). The heartbeat loop runs as an unobserved background Task —
        // if TickAsync rethrows, the loop dies and the daemon stops probing for liveness forever.
        // TickAsync must be total. A hung ping (deadline) is the path that forces a reconnect.
        var port = new FakePort {
            PingHandler = ct => {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));

                return tcs.Task;
            },
            ForceReconnectHandler = () => Task.FromException(new InvalidOperationException("StopAsync from invalid state"))
        };
        var loop = CreateLoop(port, deadline: TimeSpan.FromMilliseconds(50));

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ForceReconnectCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Tick_ReRegisterAndForceReconnectBothThrow_DoesNotRethrow() {
        // Same reliability concern as Tick_ForceReconnectThrows: the
        // displaced-slot recovery path is also a SignalR call that can
        // throw transiently. ReRegister throwing escalates to the outer
        // catch, which calls ForceReconnect; if BOTH fail the tick still
        // must not fault the unobserved loop.
        var port = new FakePort {
            PingHandler           = _ => Task.FromResult(false),
            ReRegisterHandler     = () => Task.FromException(new InvalidOperationException("InvokeAsync during reconnect")),
            ForceReconnectHandler = () => Task.FromException(new InvalidOperationException("StopAsync from invalid state"))
        };
        var loop = CreateLoop(port);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ReRegisterCalls).IsEqualTo(1);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Tick_HubReconnecting_SkipsWithoutPingingOrForcing() {
        // While the hub is not Connected, SignalR's automatic reconnect (and OnClosed) own recovery.
        // A ping would throw "connection is not active" every tick and the loop would force a
        // reconnect that races the one already in flight, so the connection never converges. The
        // heartbeat must stand down: no ping, no force-reconnect.
        var port = new FakePort {
            IsConnected = false,
            PingHandler = _ => Task.FromException<bool>(new InvalidOperationException("connection is not active"))
        };
        var loop = CreateLoop(port);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.PingCalls).IsEqualTo(0);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
        await Assert.That(port.ReRegisterCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Tick_PingThrowsWhileStateStillReadsConnected_DoesNotForceReconnect() {
        // The invoke failure can be observed before HubState transitions, so IsConnected still reads
        // true when the throw is handled. The stand-down must key on the throw itself, not on that
        // sampled state, or the guard passes and the force races the reconnect SignalR is starting.
        var port = new FakePort { IsConnected = true };
        port.PingHandler = _ => Task.FromException<bool>(new InvalidOperationException("connection is not active"));
        var loop = CreateLoop(port);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.PingCalls).IsEqualTo(1);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Tick_OuterCancellation_DoesNotForceReconnect() {
        // Outer cancellation = process shutdown. The loop must not interpret
        // that as a stuck connection and try to reconnect during teardown.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var port = new FakePort {
            PingHandler = ct => {
                var tcs = new TaskCompletionSource<bool>();
                ct.Register(() => tcs.TrySetCanceled(ct));

                return tcs.Task;
            }
        };
        var loop = CreateLoop(port);

        await loop.TickAsync(cts.Token);

        await Assert.That(port.ReRegisterCalls).IsEqualTo(0);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(0);
    }
}
