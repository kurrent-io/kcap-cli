# Resilient Hosted-Agent Permission Requests Across SignalR Reconnects — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A transient SignalR disconnect during a hosted-agent permission wait no longer falls back to `deny`; the daemon waits for the connection to recover (connected *and* re-registered) and retries, only denying on daemon shutdown or a genuinely non-recoverable failure.

**Architecture:** Add reconnect-aware retry inside `ServerConnection.RequestPermissionAsync` via two small, isolated, unit-testable units: a `RegistrationGate` (tracks "connected and registered" readiness) and a `ConnectionRetry` helper (the retry loop over injected delegates). `LocalPermissionBridge` is unchanged — its `catch → deny` becomes the final fallback that now only fires on non-recoverable outcomes.

**Tech Stack:** .NET 10 (NativeAOT), `Microsoft.AspNetCore.SignalR.Client`, TUnit + `Assert.That(...)` on Microsoft Testing Platform.

**Design doc:** `docs/superpowers/specs/2026-06-13-permission-bridge-reconnect-retry-design.md`

---

## File Structure

- **Create** `src/Capacitor.Cli.Daemon/Services/RegistrationGate.cs` — `internal sealed` readiness flag (connected + registered). One responsibility: answer "is the hub ready to carry a daemon-scoped invocation?"
- **Create** `src/Capacitor.Cli.Daemon/Services/ConnectionRetry.cs` — `internal static` retry helper + transient-failure classifier. One responsibility: retry an invocation across reconnects until it succeeds, `ct` fires, or a non-transient error surfaces.
- **Create** `test/Capacitor.Cli.Tests.Unit/RegistrationGateTests.cs` — gate transition tests.
- **Create** `test/Capacitor.Cli.Tests.Unit/ConnectionRetryTests.cs` — retry-loop behaviour tests.
- **Modify** `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` — hold a `RegistrationGate`, expose `IsReady`, drive the gate from the connection lifecycle (`Reconnecting`/`OnClosed`/`RegisterDaemon`), and route `RequestPermissionAsync` through `ConnectionRetry`. Add `LogPermissionRetry`.

These units are deliberately standalone (not `ServerConnection` privates) so they can be unit-tested without building a real `HubConnection`. The `Capacitor.Cli.Tests.Unit` assembly already has `InternalsVisibleTo` access to the daemon assembly (`src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj:20`).

---

## Task 1: `RegistrationGate` readiness flag

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/RegistrationGate.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/RegistrationGateTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/RegistrationGateTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit;

public class RegistrationGateTests {
    [Test]
    public async Task Not_ready_until_registered() {
        var gate = new RegistrationGate();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();

        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task Connection_loss_clears_readiness_even_if_state_still_connected() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();

        gate.MarkUnregistered();

        // Models the Reconnected-before-RegisterDaemon window: state reports
        // Connected but we have not re-registered yet.
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();
    }

    [Test]
    public async Task Disconnected_states_are_never_ready() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Reconnecting)).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Disconnected)).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Connecting)).IsFalse();
    }

    [Test]
    public async Task Re_registration_restores_readiness() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();
        gate.MarkUnregistered();

        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task Slot_displacement_without_transport_loss_drops_readiness_until_reregistered() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();

        // Heartbeat sees PingAsync()==false and re-registers. RegisterDaemon()
        // brackets the call: MarkUnregistered() at start, MarkRegistered() on
        // success — the transport never dropped, so state stays Connected.
        gate.MarkUnregistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();

        gate.MarkRegistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RegistrationGateTests/*"`
Expected: FAIL — `RegistrationGate` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Capacitor.Cli.Daemon/Services/RegistrationGate.cs`:

```csharp
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Tracks whether the daemon's hub connection is ready to carry a daemon-scoped
/// invocation — i.e. the transport is <see cref="HubConnectionState.Connected"/>
/// AND <c>DaemonConnect</c> (re-registration) has completed on the current
/// connection. Used by the permission-request retry loop so a retry never fires
/// against a connection the server has not (re-)registered, which would surface
/// as a HubException and be mistaken for a final deny.
///
/// The flag is reset on every connection drop (Reconnecting/Closed) and at the
/// start of every re-registration (including the heartbeat slot-displacement
/// path, which re-registers without any transport-loss event), and set only
/// after a successful registration.
/// </summary>
internal sealed class RegistrationGate {
    volatile bool _registered;

    public void MarkUnregistered() => _registered = false;
    public void MarkRegistered()   => _registered = true;

    public bool IsReady(HubConnectionState state) =>
        state == HubConnectionState.Connected && _registered;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RegistrationGateTests/*"`
Expected: PASS — 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/RegistrationGate.cs test/Capacitor.Cli.Tests.Unit/RegistrationGateTests.cs
git commit -m "Add RegistrationGate readiness flag for hosted-agent retry"
```

---

## Task 2: `ConnectionRetry` helper

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/ConnectionRetry.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/ConnectionRetryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/ConnectionRetryTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.Cli.Tests.Unit;

public class ConnectionRetryTests {
    static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(1);

    [Test]
    public async Task Recovers_after_transient_disconnect_once_ready() {
        var invokeCalls = 0;
        var pollCount   = 0;
        // Not ready for the first two readiness polls, then ready.
        Func<bool> isReady = () => ++pollCount > 2;
        var retries = new List<int>();

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new TaskCanceledException();
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, isReady, FastPoll, retries.Add, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
        await Assert.That(retries).IsEquivalentTo(new[] { 1 });
    }

    [Test]
    public async Task Cancellation_during_wait_propagates() {
        using var cts = new CancellationTokenSource();
        var retries = 0;

        Func<Task<string>> invoke = () => throw new TaskCanceledException();
        Func<bool>         isReady = () => false; // would otherwise wait forever
        Action<int>        onRetry = _ => { retries++; cts.Cancel(); };

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, isReady, FastPoll, onRetry, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(retries).IsEqualTo(1);
    }

    [Test]
    public async Task HubException_is_not_retried() {
        var retries = 0;

        Func<Task<string>> invoke = () => throw new HubException("server rejected");

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, () => true, FastPoll, _ => retries++, CancellationToken.None))
            .Throws<HubException>();

        await Assert.That(retries).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidOperationException_while_not_ready_is_retried() {
        var invokeCalls = 0;
        var ready       = false;
        var retries     = 0;

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new InvalidOperationException("connection is not active");
            return Task.FromResult("decision");
        };
        Func<bool>  isReady = () => ready;
        Action<int> onRetry = _ => { retries++; ready = true; };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, isReady, FastPoll, onRetry, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
        await Assert.That(retries).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidOperationException_while_ready_is_final() {
        var retries = 0;

        Func<Task<string>> invoke = () => throw new InvalidOperationException("boom");

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, () => true, FastPoll, _ => retries++, CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(retries).IsEqualTo(0);
    }

    [Test]
    public async Task Transient_failure_while_already_ready_retries_without_hanging() {
        var invokeCalls = 0;

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new TaskCanceledException();
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, () => true, FastPoll, _ => { }, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ConnectionRetryTests/*"`
Expected: FAIL — `ConnectionRetry` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Capacitor.Cli.Daemon/Services/ConnectionRetry.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Retries a hub invocation across transient connection drops. Used by
/// <see cref="ServerConnection.RequestPermissionAsync"/> so a SignalR blip
/// during a (potentially hours-long) permission wait doesn't surface to the
/// caller as a failure — and therefore doesn't get turned into a silent deny.
///
/// A failure is treated as a transient disconnect (→ wait for readiness, retry)
/// when it is an <see cref="OperationCanceledException"/> (SignalR cancels
/// in-flight invocations when the transport drops) or an
/// <see cref="InvalidOperationException"/> raised while the connection is NOT
/// ready (hub down / re-registering). An <see cref="InvalidOperationException"/>
/// raised while the connection IS ready signals a permanent client/protocol
/// fault that retrying cannot recover, so it propagates — mirroring the
/// <see cref="TerminalOutputSender"/> "connected yet throwing → don't spin"
/// safety valve. Every other exception (e.g. <c>HubException</c>) propagates.
///
/// The loop is bounded only by <paramref name="ct"/> (daemon shutdown). The
/// caller's shutdown token is excluded from the transient classification, so a
/// shutdown <see cref="OperationCanceledException"/> propagates rather than
/// being retried.
/// </summary>
internal static class ConnectionRetry {
    public static async Task<T> InvokeWithConnectionRetryAsync<T>(
            Func<Task<T>>     invoke,
            Func<bool>        isReady,
            TimeSpan          pollInterval,
            Action<int>       onRetry,
            CancellationToken ct
        ) {
        for (var attempt = 1; ; attempt++) {
            ct.ThrowIfCancellationRequested();

            try {
                return await invoke();
            } catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientDisconnect(ex, isReady)) {
                onRetry(attempt);

                // Delay once unconditionally: it covers the race where the
                // connection already recovered by the time we caught (the wait
                // loop would exit immediately) and guarantees the loop can never
                // spin hot.
                await Task.Delay(pollInterval, ct);

                while (!ct.IsCancellationRequested && !isReady())
                    await Task.Delay(pollInterval, ct);
            }
        }
    }

    static bool IsTransientDisconnect(Exception ex, Func<bool> isReady) =>
        ex switch {
            OperationCanceledException => true,
            InvalidOperationException  => !isReady(),
            _                          => false
        };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ConnectionRetryTests/*"`
Expected: PASS — 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ConnectionRetry.cs test/Capacitor.Cli.Tests.Unit/ConnectionRetryTests.cs
git commit -m "Add ConnectionRetry helper for reconnect-aware hub invocations"
```

---

## Task 3: Wire the `RegistrationGate` into `ServerConnection`'s connection lifecycle

This task adds the gate field, the `IsReady` property, the `Reconnecting` handler, and the gate transitions in `OnClosed` and `RegisterDaemon`. There is no isolated unit test for this wiring — constructing `ServerConnection` builds a real `HubConnection` and the lifecycle events can't be driven without a live server, so correctness here is verified by compilation plus the full existing test suite (which exercises `ServerConnection` construction via `FakeServerConnection`). The gate logic itself is covered by Task 1.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs`

- [ ] **Step 1: Add the gate field**

In `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs`, immediately after the existing fields (after line 19, `readonly ILogger<ServerConnection> _logger;`), add:

```csharp
    readonly RegistrationGate _gate = new();
```

- [ ] **Step 2: Subscribe the `Reconnecting` event**

In the constructor, find (around line 126-127):

```csharp
        _hub.Reconnected += OnReconnected;
        _hub.Closed      += OnClosed;
```

Replace with:

```csharp
        _hub.Reconnecting += OnReconnecting;
        _hub.Reconnected  += OnReconnected;
        _hub.Closed       += OnClosed;
```

- [ ] **Step 3: Add the `OnReconnecting` handler and `IsReady` property**

Directly above the existing `async Task OnReconnected(string? connectionId)` method (line 320), add:

```csharp
    /// <summary>
    /// Auto-reconnect started: the transport is no longer Connected and the
    /// server-side registration for this connection is stale. Clear readiness so
    /// nothing invokes a daemon-scoped hub method until <see cref="OnReconnected"/>
    /// re-runs <see cref="RegisterDaemon"/>.
    /// </summary>
    Task OnReconnecting(Exception? error) {
        _gate.MarkUnregistered();

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the hub is Connected AND this connection has completed
    /// <c>DaemonConnect</c>. The permission-request retry loop waits on this
    /// rather than raw <see cref="HubConnectionState.Connected"/> so a retry can't
    /// race re-registration. Mirrors the point already signalled by
    /// <see cref="OnReconnectedCallback"/>, as a pollable predicate.
    /// </summary>
    internal bool IsReady => _gate.IsReady(_hub.State);
```

- [ ] **Step 4: Clear readiness at the start of `OnClosed`**

Find the start of `OnClosed` (line 255):

```csharp
    async Task OnClosed(Exception? ex) {
        if (_disposed || _ct.IsCancellationRequested) {
            return;
        }
```

Replace with:

```csharp
    async Task OnClosed(Exception? ex) {
        _gate.MarkUnregistered();

        if (_disposed || _ct.IsCancellationRequested) {
            return;
        }
```

- [ ] **Step 5: Bracket `RegisterDaemon` with the gate**

Find `RegisterDaemon` (line 276):

```csharp
    async Task RegisterDaemon() {
        var platform  = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";
        var repoPaths = await MergeRepoPathsAsync();
        var liveIds   = GetLiveAgentIds?.Invoke() ?? [];

        try {
            await _hub.InvokeAsync(
                "DaemonConnect",
                new DaemonConnect(
                    _config.Name, platform, repoPaths, _config.MaxConcurrentAgents, liveIds,
                    _config.InstanceId, _config.Version, _config.SupportedVendors
                ),
                cancellationToken: _ct
            );
        } catch (Exception ex) when (IsNameInUse(ex)) {
```

Replace with (adds `_gate.MarkUnregistered()` at the top and `_gate.MarkRegistered()` after a successful invoke):

```csharp
    async Task RegisterDaemon() {
        // Drop readiness for the whole duration of (re-)registration. This is the
        // ONLY thing that clears readiness on the heartbeat slot-displacement path
        // (DaemonHeartbeatLoop.cs:77 → ReRegisterAsync), where the transport stays
        // up and no Reconnecting/Closed event fires.
        _gate.MarkUnregistered();

        var platform  = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";
        var repoPaths = await MergeRepoPathsAsync();
        var liveIds   = GetLiveAgentIds?.Invoke() ?? [];

        try {
            await _hub.InvokeAsync(
                "DaemonConnect",
                new DaemonConnect(
                    _config.Name, platform, repoPaths, _config.MaxConcurrentAgents, liveIds,
                    _config.InstanceId, _config.Version, _config.SupportedVendors
                ),
                cancellationToken: _ct
            );

            _gate.MarkRegistered();
        } catch (Exception ex) when (IsNameInUse(ex)) {
```

(Leave the rest of the `catch` block unchanged. `MarkRegistered()` runs only when `InvokeAsync` returns normally; any thrown exception — name-in-use or otherwise — leaves the gate unregistered.)

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ServerConnection.cs
git commit -m "Drive RegistrationGate from ServerConnection connection lifecycle"
```

---

## Task 4: Route `RequestPermissionAsync` through `ConnectionRetry`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs`

- [ ] **Step 1: Add the poll-interval constant**

In `ServerConnection`, directly above the `readonly RegistrationGate _gate = new();` field added in Task 3, add:

```csharp
    static readonly TimeSpan PermissionRetryPollInterval = TimeSpan.FromMilliseconds(500);
```

- [ ] **Step 2: Replace the `RequestPermissionAsync` body**

Find the current implementation (lines 427-441):

```csharp
    public virtual Task<PermissionDecision> RequestPermissionAsync(
            string            sessionId,
            string?           toolName,
            JsonElement?      toolInput,
            JsonElement?      suggestions,
            CancellationToken ct = default
        ) =>
        _hub.InvokeAsync<PermissionDecision>(
            "RequestPermission",
            sessionId,
            toolName,
            toolInput,
            suggestions,
            ct
        );
```

Replace with:

```csharp
    public virtual Task<PermissionDecision> RequestPermissionAsync(
            string            sessionId,
            string?           toolName,
            JsonElement?      toolInput,
            JsonElement?      suggestions,
            CancellationToken ct = default
        ) =>
        ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => _hub.InvokeAsync<PermissionDecision>(
                "RequestPermission",
                sessionId,
                toolName,
                toolInput,
                suggestions,
                ct
            ),
            () => IsReady,
            PermissionRetryPollInterval,
            attempt => LogPermissionRetry(sessionId, attempt),
            ct
        );
```

- [ ] **Step 3: Add the `LogPermissionRetry` logger message**

In the `LoggerMessage` block at the bottom of the file, directly after `LogReconnected` (line 598-599):

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to server, re-registering daemon")]
    partial void LogReconnected();
```

add:

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "RequestPermission for session {SessionId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogPermissionRetry(string sessionId, int attempt);
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run the bridge tests to confirm no regression**

The bridge mocks `RequestPermissionAsync` via `FakeServerConnection`, so its tests must still pass unchanged — including `ServerFailureFallsBackToDeny` (which throws synchronously from the override, never touching `ConnectionRetry`).

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LocalPermissionBridgeTests/*"`
Expected: PASS — all bridge tests green.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ServerConnection.cs
git commit -m "Retry RequestPermission across reconnects instead of denying on a blip"
```

---

## Task 5: Full verification (test suite + AOT)

**Files:** none (verification only).

- [ ] **Step 1: Run the full unit test suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS — all tests, including the 11 new ones (5 `RegistrationGate` + 6 `ConnectionRetry`).

- [ ] **Step 2: Verify no AOT trimming warnings in the daemon binary**

The changed code ships in the daemon AOT binary. AOT warnings only surface on publish, not build.

Run: `dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output (no `IL2026`/`IL3050` matches).

- [ ] **Step 3: Confirm no README/help changes are required**

This change is internal daemon behaviour — no new command, flag, default, or prerequisite on the user-facing CLI surface. Per `CLAUDE.md`, README sync applies only to user-facing CLI changes; none here. No doc edit needed. (Note this explicitly so a reviewer doesn't flag a missing README update.)

- [ ] **Step 4: Final commit (if any uncommitted verification artifacts)**

```bash
git status
# Expected: clean — all changes already committed in Tasks 1-4.
```

---

## Self-Review

**1. Spec coverage:**
- "Wait for reconnect & retry" → Task 2 (`ConnectionRetry`) + Task 4 (wiring). ✓
- Transient classification (`OperationCanceledException` always; `InvalidOperationException` only while not ready; everything else propagates) → Task 2 `IsTransientDisconnect` + tests 3/4/5. ✓
- No daemon-side attempt cap, `ct`-bounded → Task 2 loop (no cap) + test 2 (cancellation). ✓
- Readiness = connected AND registered → Task 1 (`RegistrationGate`) + Task 3 (`IsReady`, lifecycle wiring). ✓
- Heartbeat slot-displacement window closed → Task 3 Step 5 (`RegisterDaemon` bracket) + Task 1 test `Slot_displacement_without_transport_loss_...`. ✓
- Logging: add `LogPermissionRetry`, keep bridge deny warning → Task 4 Step 3; bridge untouched (Task 4 Step 5 confirms). ✓
- Signature unchanged so `FakeServerConnection`/bridge tests pass → Task 4 Step 2 keeps `virtual` + signature; Step 5 verifies. ✓
- End-to-end ~10h bound / orphan handler → documented tradeoff in spec; no code change in scope (no task needed). ✓
- AOT verified against daemon csproj → Task 5 Step 2. ✓

**2. Placeholder scan:** No TBD/TODO/"handle errors appropriately"; every code step shows complete code and exact commands. ✓

**3. Type consistency:** `RegistrationGate` methods `MarkUnregistered()`/`MarkRegistered()`/`IsReady(HubConnectionState)` are identical across Tasks 1 and 3. `ConnectionRetry.InvokeWithConnectionRetryAsync<T>(invoke, isReady, pollInterval, onRetry, ct)` signature is identical across Tasks 2 and 4. `IsReady` (property on `ServerConnection`) vs `IsReady(state)` (method on `RegistrationGate`) — the property delegates to the method (`_gate.IsReady(_hub.State)`); consistent and intentional. `LogPermissionRetry(string sessionId, int attempt)` defined in Task 4 Step 3 matches the call site in Step 2. ✓
