# AI-688 Option B — Task 4: orchestrator wiring — report

## Summary

Wired task 1–3's pieces into `AgentOrchestrator`'s launch/teardown lifecycle:

- **Handoff (§2.4):** `HostedRuntimeStart` gained `IAcpTranscriptSource? Transcript = null`.
  `AcpHostedAgentRuntimeFactory.StartAsync` now returns `Transcript: runtime` (the runtime IS its
  own transcript source); every PTY factory (`PtyHostedAgentRuntimeFactory`) is unchanged — its
  2-arg constructor call keeps the default `null`.
- **Launch wiring (§2.3 bind ordering):** in `HandleLaunchAgent`, right after `RegisterAgentAsync`
  and the existing `EmitsTerminalOutput` Running-flip, `if (start.Transcript is { } transcript)`
  fires `StartAcpForwardingAsync` **fire-and-forget** (mirrors `_ = ReadAgentOutputAsync(agent)`
  below it). Inside `StartAcpForwardingAsync`: awaits the `AcpSessionStarted` bind, then
  `RegisterAcpBinding`, then builds the `SessionStarted@Seq0` envelope
  (`AcpEventTranslator.BuildSessionStarted`), then constructs the `AcpTranscriptForwarder` and starts
  `ForwardAcpTranscriptAsync` (kept as `AgentInstance.AcpForwarder`, a new
  `AcpForwarderHandle(Forwarder, RunTask)`). The whole method is wrapped in a try/catch — a bind
  failure is logged and degrades to "no live transcript this session," never routed through the
  failed-launch/worktree-cleanup path (the agent is already registered and its process is already
  running by this point).
- **Teardown (§2.3 terminal ownership, bounded final-drain):** in `FinalizeAgentRunAsync`, right
  before the existing `EndAgentSessionAsync` call, `if (agent.AcpForwarder is { } acpForwarder)`
  runs `FinalDrainAcpTranscriptAsync`: disposes the runtime first (completes task 2's transcript
  channel), then `Task.WhenAny(acpForwarder.RunTask, Task.Delay(AcpFinalDrainBudget))` — always
  returns within the budget (default 5s, settable for tests), logging on timeout. `EndAgentSessionAsync`
  then runs exactly as before (unconditionally, its own existing budget/backstop untouched), followed
  by `UnregisterAcpBinding` when an `AcpForwarder` was set. PTY agents take none of this path.
- **Failure isolation:** `ForwardAcpTranscriptAsync` wraps `forwarder.RunAsync` in a try/catch so a
  fault escaping the forwarder (e.g. a faulted transcript channel) can never crash the agent or the
  daemon — the run task always completes successfully.

## Files changed

- `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` — `HostedRuntimeStart.Transcript`.
- `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs` — returns `Transcript: runtime`.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `AgentInstance.AcpForwarder` +
  `AcpForwarderHandle` record; launch-time bind/forward wiring; teardown bounded final-drain +
  unregister; `AcpFinalDrainBudget` (test-settable); 3 new `LoggerMessage` partials.
- `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorVendorTests.cs` — extended `CaptureServerConnection`
  with `IsReady => true` override, the raw ACP hub-invoke seam overrides
  (`InvokeAcpSessionStartedRawAsync`/`InvokeAcpSessionEventsRawAsync`), `AcpCallOrder`/
  `AcpSessionStartedCalls`/`AcpEventsCalls`/`AcpEventsCallSignal`, `AcpEventsBlockUntil`, and the
  one-shot `PendingAcpEventsGate` (deterministic per-call control, used to avoid a race against
  `CleanupAgentAsync`'s own unrelated dispose/worktree-removal timing).
- `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorAcpForwardingTests.cs` (new) — 6 tests:
  bind ordering + `RegisterAcpBinding`; forwarding (`SessionStarted@0` first, monotonic seq);
  teardown ordering (gated, deterministic drain-before-`EndAgentSession`, then `UnregisterAcpBinding`);
  bounded drain under a permanently-hung send (timing-discriminated: asserts elapsed ≥ budget, not
  just "eventually finished"); PTY unaffected; forwarder-fault isolation. Plus `FakeAcpRuntime`
  (`IHostedAgentRuntime` + `IAcpTranscriptSource`, dispose completes the channel) and
  `SpyAcpHostedAgentRuntimeFactory`.

## TDD verification (genuine red→green)

Disabled the launch-time bind block (`if (false && start.Transcript is { } transcript)`) — 4 of 6
new tests failed for real (bind-ordering, forwarding, teardown-drain, forwarder-fault); the other 2
(PTY-unaffected, bounded-drain-under-hang) correctly stayed green since they don't exercise that
code path. Restored, then separately disabled ONLY the teardown final-drain block
(`if (false && agent.AcpForwarder is { } acpForwarder)`) with the launch wiring active — both
teardown tests initially still passed (a **false-positive**: `CleanupAgentAsync`'s own later
dispose + worktree-removal gave the forwarder enough incidental time to drain regardless, and the
budget test never actually waited near the budget). Rewrote both to be deterministic/discriminating:
the ordering test now gates the trailing send with a test-controlled `TaskCompletionSource`
(`PendingAcpEventsGate`) and asserts `EndAgentSession` has *not* fired while the gate holds; the
budget test asserts `stopwatch.Elapsed >= budget - 100ms` (a floor), not just "eventually completed."
Re-ran with the teardown block disabled — both failed for the right reason (`endSession` appeared
before the drain elsewhere; elapsed 97ms ≪ 200ms budget). Re-enabled — both green. All 50/50 in
`AgentOrchestratorVendorTests` (incl. the new file) pass; full suite 2710/2711 (1 skipped: gated live
E2E `KCAP_ACP_LIVE=1`). No `HttpClientExtensionsRetryTests` flake observed.

## Status

STATUS: DONE
Commit: (see `git log -1`)
Tests: `Capacitor.Cli.Tests.Unit` full suite — 2710 passed / 1 skipped (gated live E2E) / 0 failed.
AOT: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release` and
`dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release` — both clean, no
IL2xxx/IL3xxx warnings.

## Concerns

1. **Launch-time race (rare, benign):** `StartAcpForwardingAsync` is fire-and-forget from
   `HandleLaunchAgent` (deliberately — the bind is `IsReady`-gated via `ConnectionRetry` and can
   block across a reconnect outage; awaiting it inline would stall the SignalR hub's message loop
   for every other agent's commands). If the ACP process exits astonishingly fast — before the bind
   even completes — `FinalizeAgentRunAsync` could run before `agent.AcpForwarder` is ever set,
   skipping the final-drain entirely (degrades to no drain attempt, not a crash). The forwarder
   would still eventually start moments later and hit a now-terminal/unregistered binding, which
   the existing terminal-drop handling in `AcpTranscriptForwarder` absorbs. Not observed in practice
   (ACP sessions have real startup latency) and out of this task's explicit scope (§2.3 only asks for
   ordering *when both events occur*, not a full launch/exit race barrier).
2. **`RegisterAcpBinding`/`UnregisterAcpBinding` are non-virtual** on `ServerConnection`, so the new
   tests verify their effect indirectly via `ReBindAcpSessionsAsync` (mirroring task 3's own test
   approach) rather than a direct call-capture.
3. Did not touch task 1/2/3 internals per the brief's hard rule — only the handoff record, the
   factory's return statement, and `AgentOrchestrator`.
