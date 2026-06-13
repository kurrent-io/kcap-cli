# Resilient hosted-agent permission requests across SignalR reconnects

**Date:** 2026-06-13
**Status:** Approved (design)

## Problem

When a daemon-hosted agent triggers a permission prompt, the CLI hook POSTs to
`LocalPermissionBridge`, which invokes the server's `RequestPermission` SignalR hub
method over the daemon's persistent connection and blocks until the user decides.

When the underlying WebSocket blips mid-wait (the recurring cloudflared instability
pattern), SignalR cancels the in-flight `InvokeAsync` with `TaskCanceledException`.
`LocalPermissionBridge.HandleAsync` catches it and falls back to `deny`, which
auto-rejects the user's tool call and breaks the conversation with the hosted agent —
even though the daemon's automatic reconnect (`RetryPolicy`, which retries forever at
≤30s intervals) recovers the connection seconds later.

Observed log:

```
2026-06-13 10:33:49 warn: RequestPermission via SignalR failed for session ...; falling back to deny
  System.Threading.Tasks.TaskCanceledException: A task was canceled.
     at Microsoft.AspNetCore.SignalR.Client.HubConnection...InvokeCoreAsync...
2026-06-13 10:33:50 info: ...WebSocketsTransport...   <-- reconnect, one second later
```

The drop and the reconnect are one second apart, confirming the cancellation is a
transient connection loss, not a real denial.

## Goal

A transient connection drop during a permission wait must **not** turn into a silent
deny. The request should survive the blip: wait for the connection to recover, then
re-issue the request so the user's prompt reappears and the flow continues.

Per decision, the conditions that end the wait and fall back to deny are:

1. **Daemon shutdown** — the bridge's cancellation token (`ct`) fires.
2. **Genuine server rejection** — the hub method throws a `HubException` (server-side
   logic error), which retrying cannot fix.
3. **An unrecognized fault** — any exception that isn't a connection-drop signal
   (anything other than `OperationCanceledException`/`InvalidOperationException`).
   Retrying these would not recover, so they propagate immediately (see "Core logic").

There is **no daemon-side attempt cap** for the transient-disconnect case: as long as
the daemon is alive and the hub is recovering, we keep waiting and retrying. Auto-reconnect
never permanently gives up, so a prolonged outage keeps the request waiting rather than
denying — this is the explicitly chosen behavior (a hosted-agent permission prompt can
legitimately wait a long time).

### End-to-end bound and orphan handler (known tradeoff)

The daemon-side wait is bounded only by `ct`, but the **end-to-end** flow has a real
ceiling: the CLI hook's HTTP client (`PermissionRequestCommand.cs:96`) caps the POST at
`10h + 1m`, after which it tears down the request, prints `permission-request timed out`,
and exits non-zero. So the practical upper bound on a hosted-agent permission wait is
~10 hours regardless of daemon-side retrying.

The bridge has **no per-request "client disconnected" signal** — `HttpListener` doesn't
expose one and the `RequestPermission` invoke is bound to the daemon-shutdown token only
(`LocalPermissionBridge.cs:202-207`). Consequently, if the hook client gives up (10h
timeout, or Claude exits mid-wait), the daemon-side retry loop keeps running and may
re-invoke `RequestPermission` after a reconnect, producing a fresh server-side prompt
that no client will ever consume (an orphan handler). Because this design deliberately
rides through reconnects and `ForceReconnect`, the orphan is **not** bounded by a single
SignalR connection's lifetime — it persists until the daemon shuts down (`ct`), the
server eventually responds, or a non-recoverable failure propagates, whichever comes
first. It is otherwise unaddressed here: switching the bridge to
Kestrel + `HttpContext.RequestAborted` for true per-request cancellation is explicitly
**out of scope** for this change (consistent with the existing code comment). We accept
the orphan-handler tradeoff for now.

## Approach

Add reconnect-aware retry **inside `ServerConnection.RequestPermissionAsync`**, which
owns the `HubConnection` and its reconnect lifecycle. `LocalPermissionBridge` is left
unchanged: its existing `catch → deny` becomes the *final* fallback that now only fires
on a non-recoverable outcome (see the Goal section's deny conditions), not on a
transient blip.

Rejected alternatives:

- **Retry in the bridge `HandleAsync`** — the bridge would need new `ServerConnection`
  surface (connection-state getter + wait), scattering connection logic across two
  types. The hub lifecycle lives in `ServerConnection`; the retry belongs there.
- **Generic hub-invoke wrapper for every hub call** — over-engineered. Only
  `RequestPermission` blocks long enough for a blip to be catastrophic; every other
  call is fire-and-forget or short. YAGNI.

## Core logic

Extract the retry loop into a small helper with **injected delegates** so it is unit
testable without a real `HubConnection`:

```
InvokeWithConnectionRetryAsync(invoke, isReady, pollInterval, onRetry, ct):
  loop:
    // Wait for readiness before EVERY attempt, including the first.
    while (!ct.IsCancellationRequested && !isReady()):
      await Task.Delay(pollInterval, ct)        // wait until connected AND re-registered
    ct.ThrowIfCancellationRequested()
    try:
      return await invoke()
    catch ex when (!ct.IsCancellationRequested && IsTransientDisconnect(ex)):
      onRetry(attempt)                          // log: interrupted by drop, will retry
      await Task.Delay(pollInterval, ct)        // brief delay; can't spin hot
    // loops until invoke() succeeds, or ct (daemon shutdown) fires,
    // or a non-transient exception propagates
```

The readiness wait sits at the **top of every attempt**, so the *first* invoke is gated
too — a request that arrives mid-reconnect/re-register doesn't fire against a connection
the server hasn't registered (which could surface as a `HubException` and become a
spurious deny). This is the single readiness gate; there is no separate ungated
first-attempt path.

**Classifying the failure** — `IsTransientDisconnect(ex)` (note: no `isReady`
dependency — see below):

- `ex is OperationCanceledException` (includes `TaskCanceledException`) — SignalR
  killed an **in-flight** invoke when the transport dropped. The exact failure in the
  bug log; unambiguously a connection loss → retry. (The `when` clause excludes the
  daemon-shutdown token via `!ct.IsCancellationRequested`, so a real shutdown
  `OperationCanceledException` propagates → bridge denies.)
- `ex is InvalidOperationException` (SignalR's "connection is not active") — because we
  only invoke *after* observing readiness, an IOE here means the transport dropped in
  the gap between the readiness check and the call. That is a transient disconnect, so
  it is retried too. The classifier deliberately does **not** consult `isReady()` at
  catch time: doing so was racy (readiness could flip between the throw and the filter,
  misclassifying a transient drop as final — Qodo #152 Bug 3) and is unnecessary once
  the first attempt is gated.
- Anything else — `HubException` (server rejected) or any unrecognized exception —
  propagates → deny (safe default).

Why no spin and no generic cap: `InvokeAsync`'s IOE is purely a connection-state signal,
so an IOE only occurs around a real drop; the next loop iteration re-waits for readiness
before retrying, and the unconditional post-failure `Task.Delay` guarantees the loop can
never spin hot. A genuine permanent fault surfaces as `HubException` or another type and
propagates → deny. The transient path is bounded by `ct` on the daemon side and by the
~10h hook-client timeout end-to-end.

- A `ForceReconnect` (heartbeat-detected wedged transport) does *not* cancel the bridge's
  `ct`, so we correctly wait through the brief not-ready window it produces rather than
  denying.
- After the wait loop exits we `ct.ThrowIfCancellationRequested()` before invoking, so a
  wait that ended because the daemon is shutting down (rather than because the hub became
  ready) propagates an `OperationCanceledException` → bridge denies, instead of invoking
  against a dead connection.

### Readiness signal: connected AND re-registered

The wait predicate is **not** raw `_hub.State == HubConnectionState.Connected`. After a
reconnect the daemon must re-run `RegisterDaemon()` before the server can route a
`RequestPermission` for this daemon's sessions (`ServerConnection.cs:229` on first
connect, `:322` on reconnect). Retrying the instant `_hub.State` flips to `Connected` —
before re-registration completes — could race the server and surface as a `HubException`,
which we'd then treat as a final deny. That reintroduces the very bug class we're fixing.

So `ServerConnection` exposes readiness as **connected and registered**, with the flag
logic isolated in a tiny testable seam rather than scattered as a raw field:

```csharp
sealed class RegistrationGate {
    volatile bool _registered;
    public void MarkUnregistered() => _registered = false;
    public void MarkRegistered()   => _registered = true;
    public bool IsReady(HubConnectionState state) =>
        state == HubConnectionState.Connected && _registered;
}
```

Wiring in `ServerConnection`:

- **Bracket every `RegisterDaemon()` with the gate**: call `_gate.MarkUnregistered()` at
  its *start* and `_gate.MarkRegistered()` only on *success*. Because every registration
  attempt flows through `RegisterDaemon()` (`ConnectWithRetryAsync`, `OnReconnected`, and
  the heartbeat's `ReRegisterAsync`), this single point guarantees `IsReady` is `false`
  for the whole duration of any re-registration and only true once it completes.
  Critically, this covers the **heartbeat slot-displaced** path
  (`DaemonHeartbeatLoop.cs:77`): when `PingAsync` returns `!alive` the transport is still
  up, so no `Reconnecting`/`OnClosed` event fires — clearing at `RegisterDaemon()` start
  is the only thing that drops readiness during that re-registration window, preventing a
  retry from invoking against a connection the server no longer recognizes.
- Also call `_gate.MarkUnregistered()` on transport loss — subscribe `_hub.Reconnecting`
  and reset at the start of `OnClosed`. This closes the window where `_hub.State` has
  flipped to `Connected` (auto-reconnect's `Reconnected`) but `OnReconnected`'s
  `RegisterDaemon()` has not yet started.
- `bool IsReady => _gate.IsReady(_hub.State);`

The retry helper receives `() => IsReady` as its `isReady` delegate. This is the same
"connected + registered" point already signaled by `OnReconnectedCallback`, expressed as
a pollable predicate for the retry loop.

### Wiring

```csharp
public virtual Task<PermissionDecision> RequestPermissionAsync(
        string sessionId, string? toolName,
        JsonElement? toolInput, JsonElement? suggestions,
        CancellationToken ct = default) =>
    InvokeWithConnectionRetryAsync(
        () => _hub.InvokeAsync<PermissionDecision>(
                  "RequestPermission", sessionId, toolName, toolInput, suggestions, ct),
        () => IsReady,                               // connected AND re-registered
        ConnectionRetryPollInterval,                 // 500 ms
        attempt => LogPermissionRetry(_logger, sessionId, attempt),
        ct);
```

The public signature is **unchanged**, so `FakeServerConnection` (which overrides the
whole method) and all `LocalPermissionBridgeTests` compile and pass untouched.

## Logging

- Add `LogPermissionRetry` (Information/Debug): "RequestPermission for session
  {SessionId} interrupted by a connection drop (retry {Attempt}); waiting for reconnect."
  This replaces the current per-blip `LogRequestPermissionFailed` warning-with-full-
  stack-trace, which is noise for an expected, now-recovered condition.
- Keep the bridge's deny-fallback warning, but it now only fires on a genuinely
  non-recoverable outcome: daemon shutdown, a `HubException`, or any other unrecognized
  fault — not on a transient blip that recovered.

## Tests

Unit tests against the helper with fakes (`isReady` delegate + a scripted `invoke`):

1. **Waits for readiness before the first invoke** — `isReady` is `false` for the first
   few polls then `true`; `invoke` is called exactly once and only after readiness was
   reached (proves attempt 1 is gated, not just retries).
2. **Recovers after a blip** — `invoke` throws `TaskCanceledException` once while ready,
   then succeeds → helper returns the decision; `onRetry` called once.
3. **Cancellation during readiness wait** — never ready, `ct` cancels while waiting →
   helper throws `OperationCanceledException` and `invoke` is never called.
4. **Cancellation after a transient failure** — ready, `invoke` throws
   `TaskCanceledException`, `onRetry` cancels `ct` → helper throws
   `OperationCanceledException`.
5. **Server rejection not retried** — `invoke` throws `HubException` → rethrown
   immediately, `onRetry` never called.
6. **`InvalidOperationException` is transient** — `invoke` throws IOE once then succeeds
   → retried and returns the decision (IOE is a connection-state signal; always retried).
7. **Already-recovered race** — `invoke` throws `TaskCanceledException` once while
   `isReady` already returns `true` → after the poll delay it re-invokes and succeeds
   (no spin, no deny).

Focused unit tests on `RegistrationGate` (the helper tests above only validate a
*supplied* `isReady` delegate and would not catch mis-wiring of the real flag, so the
gate needs its own coverage):

8. **Not ready until registered** — fresh gate with `state = Connected` →
   `IsReady` is `false`; after `MarkRegistered()` → `true`.
9. **Connection loss clears readiness** — after `MarkRegistered()`, `MarkUnregistered()`
   makes `IsReady` `false` even when `state` is still reported `Connected` (the
   `Reconnected`-before-`RegisterDaemon` window).
10. **Disconnected state is never ready** — `state = Reconnecting`/`Disconnected` →
    `IsReady` is `false` regardless of the registered flag.
11. **Re-registration restores readiness** — `MarkUnregistered()` then `MarkRegistered()`
    (the `ReRegisterAsync`/`OnReconnected` path) → `IsReady` is `true` again.
12. **Heartbeat slot-displacement without transport loss** — model the
    `RegisterDaemon()` bracket directly: from a ready gate (`state` stays `Connected`,
    no transport event), `MarkUnregistered()` (re-register start) makes `IsReady` `false`,
    and it returns to `true` only after `MarkRegistered()` (re-register success). Proves
    readiness drops during a `PingAsync()==false` re-register even though the transport
    never dropped.

Existing `LocalPermissionBridgeTests` remain green (they mock `RequestPermissionAsync`
and never exercise the helper), including `ServerFailureFallsBackToDeny`.

## AOT

Helper is plain generic async over delegates — no reflection, no dynamic code. No new
IL2026/IL3050 surface. The changed code ships in the separate **daemon** AOT binary, so
verify against that project explicitly:

```bash
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

## Accepted tradeoff

Re-invoking after reconnect issues a fresh server-side `RequestPermission`, so if the
user happened to answer in the instant before the drop, the prompt may reappear once
and need answering again. Minor, and strictly better than a silent deny.
