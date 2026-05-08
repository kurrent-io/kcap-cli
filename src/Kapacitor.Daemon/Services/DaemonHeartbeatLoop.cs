using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

/// <summary>
/// Daemon-driven liveness probe — replaces the pre-AI-79 fire-and-forget
/// <c>SendHeartbeatAsync</c>. The server-side <c>DaemonPing</c> hub method
/// returns <c>true</c> if the calling connection is still the registered
/// daemon for its <c>(owner, name)</c> slot, and <c>false</c> if it isn't —
/// either because the daemon never called <c>DaemonConnect</c> on this
/// connection, or because a subsequent <c>DaemonConnect</c> displaced this
/// connection's registry entry. The displacement case is the staging
/// incident: the orchestrator's view of the daemon is bound to the new
/// conn id while the daemon's WebSocket transport keeps believing the old
/// one is fine. A round-trip ping surfaces that mismatch within one tick
/// instead of waiting for the WebSocket to teach the daemon.
/// </summary>
internal interface IDaemonHeartbeatPort {
    Task<bool> PingAsync(CancellationToken ct);
    Task       ReRegisterAsync();
    Task       ForceReconnectAsync();
}

internal sealed class DaemonHeartbeatLoop(IDaemonHeartbeatPort port, TimeSpan pingDeadline, ILogger logger) {
    public async Task TickAsync(CancellationToken ct) {
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(pingDeadline);

            var alive = await port.PingAsync(cts.Token);

            if (!alive) {
                logger.LogWarning("Heartbeat: server does not recognise this connection — re-registering daemon");
                await port.ReRegisterAsync();
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            // Per-tick deadline fired before the server answered. The
            // WebSocket is hung; force WithAutomaticReconnect by stopping
            // the hub and letting OnClosed → ConnectWithRetryAsync rebuild
            // it under a fresh conn id.
            logger.LogWarning("Heartbeat: ping exceeded deadline — forcing reconnect");
            await port.ForceReconnectAsync();
        } catch (OperationCanceledException) {
            // Outer cancellation (process shutting down) — let the loop exit.
        } catch (Exception ex) {
            logger.LogWarning(ex, "Heartbeat: ping threw — forcing reconnect");
            await port.ForceReconnectAsync();
        }
    }
}
