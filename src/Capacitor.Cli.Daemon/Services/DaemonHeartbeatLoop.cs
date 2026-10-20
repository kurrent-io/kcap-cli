using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Daemon-driven liveness probe — replaces the previous fire-and-forget
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
    /// <summary>The hub is <c>Connected</c>. False while SignalR is Connecting/Reconnecting or the
    /// connection is Disconnected — states where <c>WithAutomaticReconnect</c> and <c>OnClosed</c>
    /// already own recovery, and the heartbeat must not force a reconnect of its own.</summary>
    bool       IsConnected { get; }
    Task<bool> PingAsync(CancellationToken ct);
    Task       ReRegisterAsync();
    Task       ForceReconnectAsync();

    /// <summary>The stored sign-in now names an account other than the one the live hub negotiated as.
    /// The hub keeps its handshake identity for the life of the socket, so only a reconnect picks it up.</summary>
    Task<bool> CredentialChangedAsync(CancellationToken ct);
}

internal sealed class DaemonHeartbeatLoop(
        IDaemonHeartbeatPort port,
        TimeSpan             pingDeadline,
        ILogger              logger,
        TimeProvider         time,
        TimeSpan?            slowPingThreshold = null
    ) {
    /// <summary>
    /// Round-trip time at or above which a <c>DaemonPing</c> is logged as a
    /// warning rather than at debug (diagnostics). Defaults to half the
    /// per-tick deadline: pings that take this long aren't yet failing, but the
    /// transport latency is climbing toward the deadline that triggers a forced
    /// reconnect, so surfacing them shows degradation building up *before* the
    /// connection actually flaps — the key signal for deciding whether the
    /// cloudflared tunnel (vs the server itself) is the latency source.
    /// </summary>
    readonly TimeSpan _slowPingThreshold = slowPingThreshold ?? TimeSpan.FromTicks(pingDeadline.Ticks / 2);

    /// A read that outlived its tick's wait, joined by the next tick rather than raced by a second read —
    /// until it has run this many deadlines, when it is abandoned so one hung read cannot end detection.
    const int CredentialReadAbandonedAfterDeadlines = 5;

    Task<bool>? _credentialRead;
    long        _credentialReadStarted;

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
        // Stand down unless the hub is Connected. While it is Connecting/Reconnecting/Disconnected,
        // WithAutomaticReconnect and OnClosed own recovery; a ping here would throw "connection is not
        // active" and the catch below would force a reconnect that races the one already in flight —
        // the self-sustaining storm. Skipping keeps the loop quiet until the connection is back.
        if (!port.IsConnected) {
            logger.LogDebug("Heartbeat: hub is not connected — automatic reconnect owns recovery; skipping tick");

            return;
        }

        var started = time.GetTimestamp();

        try {
            using var cap = new CancellationTokenSource(pingDeadline, time);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, cap.Token);

            var alive = await port.PingAsync(cts.Token);
            var rtt   = time.GetElapsedTime(started);

            if (rtt >= _slowPingThreshold) {
                logger.LogWarning(
                    "Heartbeat: DaemonPing slow — {RttMs:F0} ms RTT (deadline {DeadlineMs:F0} ms); transport latency climbing toward a forced reconnect",
                    rtt.TotalMilliseconds, pingDeadline.TotalMilliseconds
                );
            } else {
                logger.LogDebug("Heartbeat: DaemonPing ok — {RttMs:F0} ms RTT", rtt.TotalMilliseconds);
            }

            if (!alive) {
                logger.LogWarning("Heartbeat: server does not recognise this connection (cause=slot_displaced) — re-registering daemon");
                await SafeReRegisterAsync();
            } else if (await SafeCredentialChangedAsync(ct)) {
                logger.LogWarning("Heartbeat: the stored sign-in no longer matches the one the hub negotiated (cause=credential_changed) — reconnecting");
                await SafeForceReconnectAsync();
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
            // A ping that THROWS (as opposed to hanging until the deadline) means the SignalR client
            // already knows the connection is unusable — "connection is not active" is exactly its
            // signal that the hub has dropped — and OnClosed plus automatic reconnect are already
            // reacting to the same condition. Forcing a reconnect here only races that recovery, and
            // reading HubState to decide is unsafe because the invoke failure can be observed before
            // the state transitions. Only a hung ping (the deadline above) needs the heartbeat, since
            // SignalR still believes a half-open transport is fine. So stand down on any throw.
            logger.LogWarning(ex, "Heartbeat: DaemonPing threw after {RttMs:F0} ms (cause=ping_threw) — standing down for automatic reconnect", time.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    async Task SafeReRegisterAsync() {
        try {
            await port.ReRegisterAsync();
        } catch (HubException ex) when (ex.Message.StartsWith(ServerConnection.NameInUseErrorCode, StringComparison.Ordinal)) {
            // the server explicitly told us our (owner, name) slot
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

    async Task<bool> SafeCredentialChangedAsync(CancellationToken ct) {
        try {
            // Bounded like the ping: a stalled token-store read must not stop the loop probing the hub.
            // Only the wait is bounded: a refresh under way must still save the token it rotated.
            using var cap = new CancellationTokenSource(pingDeadline, time);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, cap.Token);

            if (_credentialRead is not { IsCompleted: false }
             || time.GetElapsedTime(_credentialReadStarted) >= pingDeadline * CredentialReadAbandonedAfterDeadlines) {
                _credentialReadStarted = time.GetTimestamp();
                _credentialRead        = port.CredentialChangedAsync(ct);
            }

            return await _credentialRead.WaitAsync(cts.Token);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException) {
            logger.LogWarning("Heartbeat: reading the stored sign-in exceeded {DeadlineMs:F0} ms — leaving the hub connected", pingDeadline.TotalMilliseconds);

            return false;
        } catch (Exception ex) {
            // An unreadable token store says nothing about who the hub is signed in as.
            logger.LogWarning(ex, "Heartbeat: could not read the stored sign-in — leaving the hub connected");

            return false;
        }
    }

    async Task SafeForceReconnectAsync() {
        // Reached only from the two live-connection paths — a hung ping (deadline) and a failed
        // re-register — so a reconnect is genuinely wanted here; the ping-threw path stands down
        // before reaching this. No state sample gates the force, since a hung transport must be
        // reconnected whatever HubState momentarily reads.
        try {
            await port.ForceReconnectAsync();
        } catch (Exception ex) {
            // Last line of defence. Log and let the next tick retry; with
            // no rethrow the unobserved heartbeat Task stays alive.
            logger.LogWarning(ex, "Heartbeat: force reconnect failed — will retry next tick");
        }
    }
}
