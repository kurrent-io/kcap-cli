using Kapacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kapacitor.Cli.Tests.Unit.Daemon;

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

        public Task<bool> PingAsync(CancellationToken ct)
            => PingHandler is null ? Task.FromResult(true) : PingHandler(ct);

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
    public async Task Tick_PingThrows_ForcesReconnect() {
        var port = new FakePort {
            PingHandler = _ => Task.FromException<bool>(new InvalidOperationException("hub closed"))
        };
        var loop = CreateLoop(port);

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(port.ReRegisterCalls).IsEqualTo(0);
        await Assert.That(port.ForceReconnectCalls).IsEqualTo(1);
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
        // Qodo finding: ForceReconnectAsync ultimately calls _hub.StopAsync,
        // which can throw (invalid state, transient SignalR cancellation).
        // The heartbeat loop runs as an unobserved background Task — if
        // TickAsync rethrows, the loop dies and the daemon stops probing
        // for liveness forever. TickAsync must be total.
        var port = new FakePort {
            PingHandler           = _ => Task.FromException<bool>(new InvalidOperationException("hub closed")),
            ForceReconnectHandler = () => Task.FromException(new InvalidOperationException("StopAsync from invalid state"))
        };
        var loop = CreateLoop(port);

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
