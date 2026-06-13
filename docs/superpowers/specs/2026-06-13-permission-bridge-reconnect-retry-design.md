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

Per decision, the only conditions that end the wait and fall back to deny are:

1. **Daemon shutdown** — the bridge's cancellation token fires.
2. **Genuine server rejection** — the hub method throws a `HubException` (server-side
   logic error), which retrying cannot fix.

There is **no** attempt/time cap: as long as the daemon is alive and the hub is
recovering, we keep waiting. Auto-reconnect never permanently gives up, so a prolonged
outage keeps the hook waiting rather than denying — this is the explicitly chosen
behavior (a hosted-agent permission prompt can legitimately wait a long time).

## Approach

Add reconnect-aware retry **inside `ServerConnection.RequestPermissionAsync`**, which
owns the `HubConnection` and its reconnect lifecycle. `LocalPermissionBridge` is left
unchanged: its existing `catch → deny` becomes the *final* fallback that now only fires
on daemon shutdown or a `HubException`, not on a transient blip.

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
InvokeWithConnectionRetryAsync(invoke, getState, pollInterval, onRetry, ct):
  loop:
    ct.ThrowIfCancellationRequested()
    try:
      return await invoke()
    catch ex when (!ct.IsCancellationRequested && IsConnectionException(ex)):
      onRetry(attempt)                          // log: interrupted by drop, will retry
      await Task.Delay(pollInterval, ct)        // covers the "already recovered" race
                                                // and prevents a hot loop
      while (!ct.IsCancellationRequested && getState() != Connected):
        await Task.Delay(pollInterval, ct)      // wait out the reconnect
    // loops until invoke() succeeds, or ct (daemon shutdown) fires,
    // or a non-connection exception propagates
```

- **`IsConnectionException(ex)`** = `ex is OperationCanceledException` (includes
  `TaskCanceledException`) `or InvalidOperationException` (SignalR's "connection is not
  active" when the hub is down). `HubException` derives from neither, so a genuine
  server rejection propagates → bridge denies. Any other unrecognized exception also
  propagates → deny (safe default).
- The `when` clause already excludes caller cancellation
  (`!ct.IsCancellationRequested`), so a shutdown-token `OperationCanceledException` is
  never swallowed — it propagates and the bridge denies (correct: we're going away).
- **No attempt cap.** The loop is bounded only by `ct`. A `ForceReconnect`
  (heartbeat-detected wedged transport) does *not* cancel the bridge's `ct`, so we
  correctly wait through the brief `Disconnected` window it produces rather than denying.
- The unconditional `Task.Delay(pollInterval, ct)` before the state-wait handles the
  race where the connection already recovered by the time we catch (the `while` would
  exit immediately) and guarantees the loop can never spin hot.

### Wiring

```csharp
public virtual Task<PermissionDecision> RequestPermissionAsync(
        string sessionId, string? toolName,
        JsonElement? toolInput, JsonElement? suggestions,
        CancellationToken ct = default) =>
    InvokeWithConnectionRetryAsync(
        () => _hub.InvokeAsync<PermissionDecision>(
                  "RequestPermission", sessionId, toolName, toolInput, suggestions, ct),
        () => _hub.State,
        ConnectionRetryPollInterval,                 // e.g. 500 ms
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
- Keep the bridge's deny-fallback warning, but it now only fires on true exhaustion
  (shutdown) or a `HubException`.

## Tests

Unit tests against the helper with fakes (`getState` delegate + a scripted `invoke`):

1. **Recovers after a blip** — `invoke` throws `TaskCanceledException` N times while
   `getState` returns `Reconnecting`, then `getState` flips to `Connected` and `invoke`
   succeeds → helper returns the decision; `onRetry` called N times.
2. **Daemon shutdown mid-wait** — cancel `ct` while waiting → helper throws
   `OperationCanceledException` (bridge would deny).
3. **Server rejection not retried** — `invoke` throws `HubException` → rethrown
   immediately, `onRetry` never called.
4. **Already-recovered race** — `invoke` throws once while `getState` already returns
   `Connected` → after the poll delay it re-invokes and succeeds (no spin, no deny).

Existing `LocalPermissionBridgeTests` remain green (they mock `RequestPermissionAsync`
and never exercise the helper), including `ServerFailureFallsBackToDeny`.

## AOT

Helper is plain generic async over delegates — no reflection, no dynamic code. No new
IL2026/IL3050 surface. Verify with `dotnet publish -c Release` per project convention.

## Accepted tradeoff

Re-invoking after reconnect issues a fresh server-side `RequestPermission`, so if the
user happened to answer in the instant before the drop, the prompt may reappear once
and need answering again. Minor, and strictly better than a silent deny.
