# AI-688 Option B — Task 4: orchestrator wiring (implementer brief)

The integration task: wire the task-1/2/3 pieces into the daemon's launch/teardown lifecycle so a live Cursor ACP
turn actually reaches the server. Builds on: task 1 (`AcpEventTranslator`, wire DTOs), task 2
(`AcpHostedAgentRuntime` exposes `IAcpTranscriptSource` — `AcpSessionId`/`Cwd`/`ResolvedModel`/`Envelopes`), task 3
(`ServerConnection.AcpSessionStartedAsync`/`SendAcpEventsAsync`/`RegisterAcpBinding`/`UnregisterAcpBinding`;
`AcpTranscriptForwarder`). This is the LAST implementation task (task 5 = live E2E only).

Work ONLY in `/Users/tony/Documents/kcap-cli-wt/ai-688-cursor-hosted-agent-prototype`, branch
`ai-688-cursor-hosted-agent-prototype`. Commit; **do NOT push**; no delegation. Spec:
`docs/ai688-option-b-canonical-surfacing-design.md` §2.3 (ordering/terminal) + §2.4 (handoff).

## Read first
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `HandleLaunchAgent` (the launch path:
  worktree → `runtimeFactory.StartAsync` → `RegisterAgentAsync` → the `EmitsTerminalOutput` special-case →
  `ReadAgentOutputAsync`), and the TEARDOWN path (`EndAgentSessionAsync` — note it is deliberately TIME-BUDGETED;
  and wherever the runtime is disposed when the ACP process exits / the read loop ends).
- `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` — `HostedRuntimeStart` (today: `Runtime`,
  `McpConfigPath`).
- `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs` — returns `HostedRuntimeStart`; the runtime
  it builds IS `IAcpTranscriptSource`.
- `src/Capacitor.Cli.Daemon/Services/AcpTranscriptForwarder.cs` + `ServerConnection.cs` (the task-3 API) +
  `AcpEventTranslator.BuildSessionStarted`.
- The SERVER `AcpSessionStarted` hub signature (read-only, ai-686 server worktree
  `src/Capacitor.Server/Sessions/CapacitorHub.cs`) — match arg order/names + know what `metadata` expects (pass an
  empty/minimal metadata for the prototype unless a field is required).

## A. Handoff on `HostedRuntimeStart` (§2.4)
Add a nullable ACP transcript source to `HostedRuntimeStart` (e.g. `IAcpTranscriptSource? Transcript = null`). In
`AcpHostedAgentRuntimeFactory.StartAsync`, set it to the runtime (which implements `IAcpTranscriptSource`). PTY
factory leaves it null. This is how the orchestrator gets `AcpSessionId`/`Cwd`/`ResolvedModel`/`Envelopes` without
downcasting the runtime.

## B. Launch wiring in `HandleLaunchAgent` (§2.3 bind ordering — the load-bearing part)
At the existing `EmitsTerminalOutput` special-case, **AFTER `RegisterAgentAsync` has completed**, when
`start.Transcript is { } transcript` (non-null → an ACP agent):
1. Call `connection.AcpSessionStartedAsync(agentId, cmd.Vendor, transcript.AcpSessionId, transcript.Cwd,
   transcript.ResolvedModel, <empty metadata>)` — the bind. MUST be after `RegisterAgentAsync` (the server rejects
   a bind for an unregistered agent) and before any events.
2. `connection.RegisterAcpBinding(agentId, <bindInfo built from the same args>)` — so a reconnect re-binds it
   (task 3's `ReBindAcpSessionsAsync`).
3. Build the `SessionStarted` envelope via `AcpEventTranslator.BuildSessionStarted(seq: 0, NowIso(), cwd, model,
   rawSessionId: acpSessionId, ...)` (match the builder's params).
4. Construct `new AcpTranscriptForwarder(send: (batch, ct) => connection.SendAcpEventsAsync(agentId,
   transcript.AcpSessionId, batch), initialEnvelope: sessionStarted, envelopes: transcript.Envelopes, logger)` and
   start it fire-and-forget under the orchestrator's shutdown token: `_ = ForwardAcpTranscriptAsync(agent,
   forwarder, ct)` (a small wrapper that awaits `forwarder.RunAsync` in a try/catch — a forwarder fault must be
   logged, NEVER crash the daemon or the agent). Keep a handle to the forwarder + its task on the `AgentInstance`
   (or a side dictionary keyed by agentId) so teardown (C) can coordinate.
Mirror the existing `EmitsTerminalOutput` special-case placement; add no PTY branch (guarded by
`start.Transcript is not null`).

## C. Teardown: bounded final-drain, then finalize (§2.3 terminal ownership + finding 2/3; also the guard's primary prevention)
When the ACP agent ends (process exit / read loop ends / stop), BEFORE calling `EndAgentSessionAsync`:
1. Dispose/stop the runtime so task 2 completes the transcript channel (its `DisposeAsync` cancels the turn worker
   + courtesy-flushes the open run + completes `_transcript`).
2. **Bounded final-drain:** await the forwarder's `RunAsync` task to finish draining, but under a FINITE budget
   (e.g. `await Task.WhenAny(forwarderTask, Task.Delay(FinalDrainBudget))`); if the budget elapses first, cancel the
   forwarder and log best-effort transcript loss. This preserves `EndAgentSessionAsync`'s existing outage-cleanup
   guarantee — the drain must NEVER pin shutdown. (This ordering — forwarder stops before the binding goes terminal
   — is also what keeps the task-3 hot-loop guard's edge from triggering in the normal flow.)
3. THEN `EndAgentSessionAsync(...)` as today (it is the sole canonical `SessionEnded` owner — the forwarder never
   emits `session_ended`).
4. `connection.UnregisterAcpBinding(agentId)` (so a later reconnect doesn't try to re-bind a dead session).
Make this robust to the crash path (no clean exit): the final-drain is best-effort and time-bounded; a missing
flush degrades to "no trailing transcript", never a hang or a crash.

## TDD (test/Capacitor.Cli.Tests.Unit/)
Use the orchestrator's existing test seam (`AgentOrchestratorVendorTests` / `BuildOrchestrator` with a fake
`ServerConnection` capture + a fake/spy runtime factory that returns a `HostedRuntimeStart` with a stub
`IAcpTranscriptSource`). Genuine red→green. Cover:
- **Bind ordering:** a `cursor` launch calls `AcpSessionStarted` AFTER `RegisterAgent`/`AgentRegistered` and before
  any `SendAcpEvents` (assert call order on the capture); `RegisterAcpBinding` is called.
- **Forwarding:** envelopes the stub transcript source emits are forwarded via `SendAcpEventsAsync` (SessionStarted@0
  first).
- **Teardown ordering:** on agent end, the final-drain runs (forwarder gets a chance to drain) BEFORE
  `EndAgentSession`, then `UnregisterAcpBinding`; and if the forwarder never drains, `EndAgentSession` still happens
  within the budget (no hang).
- **PTY unaffected:** a `claude`/`codex` launch (Transcript null) never calls `AcpSessionStarted`/`SendAcpEvents`.
- **Failure isolation:** a forwarder that throws does not crash the launch or the orchestrator.

## Definition of done
- `dotnet build` clean (daemon+core); AOT gate empty
  (`dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`).
- `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` — report pass/total (note the
  known pre-existing flaky `HttpClientExtensionsRetryTests` if it appears).
- Red→green for bind-ordering + teardown-ordering.

## Report contract
Write detail to `.sdd/task4-orchestrator-wiring-report.md`. Return only STATUS, commit sha, one-line test summary,
AOT result, and concerns (esp. anything about the teardown/crash path or the `AgentInstance` handle you added).
Commit as `AI-688 Option B task 4: orchestrator wiring — bind, forward, bounded final-drain`.

## HARD RULES
- Read/Edit/Bash yourself only; NO Agent/Task delegation; NO git push. Read the server worktree read-only.
- Worktree-only. `await` every TUnit assertion. AOT/trim-safe.
- Preserve the existing PTY launch/teardown path + `EndAgentSessionAsync`'s time-budget. Do NOT re-implement task
  1/2/3 code — only wire it. Do NOT make the forwarder emit `session_ended` (server owns it).
