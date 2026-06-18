using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

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

internal sealed class DaemonHeartbeatLoop(
        IDaemonHeartbeatPort port,
        TimeSpan             pingDeadline,
        ILogger              logger,
        TimeSpan?            slowPingThreshold = null
    ) {
    /// <summary>
    /// Round-trip time at or above which a <c>DaemonPing</c> is logged as a
    /// warning rather than at debug (AI-840 diagnostics). Defaults to half the
    /// per-tick deadline: pings that take this long aren't yet failing, but the
    /// transport latency is climbing toward the deadline that triggers a forced
    /// reconnect, so surfacing them shows degradation building up *before* the
    /// connection actually flaps — the key signal for deciding whether the
    /// cloudflared tunnel (vs the server itself) is the latency source.
    /// </summary>
    readonly TimeSpan _slowPingThreshold = slowPingThreshold ?? TimeSpan.FromTicks(pingDeadline.Ticks / 2);

    /// <summary>
    /// Total — never throws (modulo outer cancellation, which is expected at
    /// shutdown). The loop in <c>AgentOrchestrator.RunDaemonHeartbeatLoopAsync</c>
    /// runs as an unobserved background Task; if a tick faulted, the loop
    /// would die silently and the daemon would stop probing for liveness
    /// forever. Recovery actions (<see cref="IDaemonHeartbeatPort.ReRegisterAsync"/>,
    /// <see cref="IDaemonHeartbeatPort.ForceReconnectAsync"/>) are individually
    /// guarded since both ultimately call into SignalR (<c>InvokeAsync</c> /
    /// <c>StopAsync</c>) and can throw on transient transport state.
    ///
    /// Each tick also records the <c>DaemonPing</c> round-trip time and the
    /// cause of any forced reconnect, so the daemon log carries the data needed
    /// to characterise transport stability (RTT distribution over time, and
    /// whether reconnects are deadline-exceeded vs ping-threw vs slot-displaced).
    /// </summary>
    public async Task TickAsync(CancellationToken ct) {
        var sw = Stopwatch.StartNew();

        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(pingDeadline);

            var alive = await port.PingAsync(cts.Token);
            sw.Stop();

            if (sw.Elapsed >= _slowPingThreshold) {
                logger.LogWarning(
                    "Heartbeat: DaemonPing slow — {RttMs:F0} ms RTT (deadline {DeadlineMs:F0} ms); transport latency climbing toward a forced reconnect",
                    sw.Elapsed.TotalMilliseconds, pingDeadline.TotalMilliseconds
                );
            } else {
                logger.LogDebug("Heartbeat: DaemonPing ok — {RttMs:F0} ms RTT", sw.Elapsed.TotalMilliseconds);
            }

            if (!alive) {
                logger.LogWarning("Heartbeat: server does not recognise this connection (cause=slot_displaced) — re-registering daemon");
                await SafeReRegisterAsync();
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            // Per-tick deadline fired before the server answered. The
            // WebSocket is hung; force WithAutomaticReconnect by stopping
            // the hub and letting OnClosed → ConnectWithRetryAsync rebuild
            // it under a fresh conn id.
            logger.LogWarning(
                "Heartbeat: DaemonPing exceeded {DeadlineMs:F0} ms deadline (cause=ping_deadline_exceeded) — forcing reconnect",
                pingDeadline.TotalMilliseconds
            );
            await SafeForceReconnectAsync();
        } catch (OperationCanceledException) {
            // Outer cancellation (process shutting down) — let the loop exit.
        } catch (Exception ex) {
            sw.Stop();
            logger.LogWarning(ex, "Heartbeat: DaemonPing threw after {RttMs:F0} ms (cause=ping_threw) — forcing reconnect", sw.Elapsed.TotalMilliseconds);
            await SafeForceReconnectAsync();
        }
    }

    async Task SafeReRegisterAsync() {
        try {
            await port.ReRegisterAsync();
        } catch (HubException ex) when (ex.Message.StartsWith(ServerConnection.NameInUseErrorCode, StringComparison.Ordinal)) {
            // AI-630: the server explicitly told us our (owner, name) slot
            // is held by another live daemon. Force-reconnecting would just
            // re-trigger the same rejection. ServerConnection already fired
            // OnNameInUse — DaemonRunner has called lifetime.StopApplication()
            // and the outer loop will exit on its next WaitForNextTickAsync.
            // No-op here so we don't escalate.
        } catch (Exception ex) {
            // Re-register itself failed. Escalate to a full reconnect — if
            // SignalR's InvokeAsync rejected, the transport is likely in
            // bad shape too. Both calls are guarded so the tick is total.
            logger.LogWarning(ex, "Heartbeat: re-register failed — escalating to forced reconnect");
            await SafeForceReconnectAsync();
        }
    }

    async Task SafeForceReconnectAsync() {
        try {
            await port.ForceReconnectAsync();
        } catch (Exception ex) {
            // Last line of defence. Log and let the next tick retry; with
            // no rethrow the unobserved heartbeat Task stays alive.
            logger.LogWarning(ex, "Heartbeat: force reconnect failed — will retry next tick");
        }
    }
}
