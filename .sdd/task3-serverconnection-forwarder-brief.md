# AI-688 Option B — Task 3: ServerConnection hub methods + AcpTranscriptForwarder (implementer brief)

The forwarding layer: two gated `ServerConnection` hub-invoke methods + a stateful `AcpTranscriptForwarder`
(seq assignment, unacked buffer, gap-retry, terminal-drop, send-retry) + the reconnect re-bind mechanism in
`ServerConnection`. Builds on task 1 (`AcpEventEnvelope`/`AcpBatchAck` wire DTOs) + task 2 (`IAcpTranscriptSource`
exposing `ChannelReader<AcpEventEnvelope> Envelopes`). **NOT in scope:** the initial bind *call*, building the
`SessionStarted` envelope, the final-drain, and any `AgentOrchestrator` wiring — those are task 4. Do not touch
`AgentOrchestrator`.

Work ONLY in `/Users/tony/Documents/kcap-cli-wt/ai-688-cursor-hosted-agent-prototype`, branch
`ai-688-cursor-hosted-agent-prototype`. Commit; **do NOT push**; no delegation. Spec:
`docs/ai688-option-b-canonical-surfacing-design.md` §2.3.

## READ FIRST — match the server contract exactly
The forwarder's ack-handling MUST match the server's `AcpSessionEvents` semantics. Read (read-only) in the ai-686
server worktree `/Users/tony/Documents/kcap-server-wt/ai-686-acp-permission-bridge`:
- `src/Capacitor.Server/Sessions/CapacitorHub.cs` — `AcpSessionStarted` (bind; args + idempotency) and
  `AcpSessionEvents` (the per-session seq/dedup/gap/terminal logic; what it returns in `AcpBatchAck`).
- `src/Capacitor.Server/Agents/AcpSessionRegistry.cs` — the authoritative seq/gap/terminal state.
- `src/Capacitor.Server.Core/Acp/AcpEventEnvelope.cs` — `AcpBatchAck(AcceptedSeq, PersistedSeq, ExpectedNextSeq?)`.
Confirm precisely: when is `ExpectedNextSeq` non-null (gap)? what does the server do with a duplicate/old seq
(dedup)? what does it return once the binding is terminal (per spec r2/r3: `AcceptedSeq` does NOT advance AND
`ExpectedNextSeq == null`)? Encode the forwarder to those exact rules; note any mismatch vs the spec in your report.

## Also read (daemon side) — mirror the existing gating pattern
`src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` — how existing hub calls are gated (`ConnectionRetry` +
`IsReady`), the non-blocking-invoke pattern of `RequestAcpInteractionAsync`, and `RegisterDaemon` (the reconnect
re-registration path that runs BEFORE `IsReady` returns). The new calls must use the SAME gating.

## A. `ServerConnection` hub methods
- `Task AcpSessionStartedAsync(string agentId, string vendor, string acpSessionId, string cwd, string? model, <metadata?>)`
  → `_hub.InvokeAsync("AcpSessionStarted", …)`, gated like existing calls. Idempotent server-side (same-agent
  re-bind), so safe to call again on reconnect.
- `Task<AcpBatchAck> SendAcpEventsAsync(string agentId, string acpSessionId, AcpEventEnvelope[] envelopes)`
  → `_hub.InvokeAsync<AcpBatchAck>("AcpSessionEvents", …)`, same gating. (Match the server hub's exact parameter
  order/names.)

## B. Reconnect re-bind mechanism (in ServerConnection; §2.3 r2/r3)
- Add an in-memory registry of ACTIVE ACP bindings: `RegisterAcpBinding(agentId, bindInfo)` /
  `UnregisterAcpBinding(agentId)` (task 4 calls these on launch/end). `bindInfo` = the args needed to re-invoke
  `AcpSessionStarted` (vendor/acpSessionId/cwd/model).
- In `RegisterDaemon` (the reconnect re-registration path that runs before `IsReady` returns), AFTER the existing
  agent re-registration, **re-invoke `AcpSessionStartedAsync` for every registered active binding** — so a
  post-reconnect `SendAcpEventsAsync` (which is `IsReady`-gated) can never reach the server before the binding is
  re-established. Idempotent, so a redundant re-bind is harmless.

## C. `AcpTranscriptForwarder` (the state machine)
A component (new file, `src/Capacitor.Cli.Daemon/Services/` or `/Acp/`) that pumps envelopes to the server with
seq/ack bookkeeping. Constructor takes: a send delegate `Func<AcpEventEnvelope[], CancellationToken, Task<AcpBatchAck>>`
(bound to `conn.SendAcpEventsAsync` for this agent/session — do NOT have the forwarder call `AcpSessionStarted`; the
bind is task 4's), an **initial envelope** (the `SessionStarted`, pre-built by task 4 — the forwarder assigns it
`Seq = 0`), the transcript `ChannelReader<AcpEventEnvelope>` (task 2's `IAcpTranscriptSource.Envelopes`), and a logger.

`RunAsync(ct)` behavior:
- **Seq assignment:** the initial envelope gets `Seq = 0`; each envelope dequeued from the transcript channel gets
  the next monotonic seq (1, 2, …). Envelopes arrive with a placeholder `Seq = 0` (task 2) — stamp the real seq via
  `env with { Seq = next }`.
- **Unacked buffer:** keep every sent-but-unacked envelope keyed by seq (ordered). Batch opportunistically (drain
  what's available from the channel, send as one `AcpEventEnvelope[]`).
- **Ack handling** (per the server contract you read):
  - normal (`AcceptedSeq` advanced, `ExpectedNextSeq == null`): drop acked envelopes (≤ `AcceptedSeq`) from the buffer.
  - gap (`ExpectedNextSeq != null`): resend from `ExpectedNextSeq` using the buffer.
  - terminal-drop (`AcceptedSeq` < highest-sent AND `ExpectedNextSeq == null`): stop the loop + clear the buffer;
    no further sends/retries (the binding is terminal server-side).
- **Send-retry:** a send that throws (connection dropped / not ready) is retried from the current unacked cursor
  after the connection is ready again (lean on the `ConnectionRetry`/`IsReady` gating in `SendAcpEventsAsync`, plus
  a bounded backoff). The server dedups by seq, so resending an already-persisted batch is safe.
- **Completion:** when the transcript channel completes (session ending) and the buffer is drained/acked (or
  terminal), `RunAsync` returns. Cancellation via `ct` returns promptly (no hang).

## TDD (test/Capacitor.Cli.Tests.Unit/)
Genuine red→green. Forwarder (fake send delegate returning scripted `AcpBatchAck`s + a fake transcript channel):
- monotonic seq: initial=0, then 1,2,3… in channel order;
- normal acks drop the buffer; the buffer only holds unacked;
- **gap** (`ExpectedNextSeq` set) → resend from that seq;
- **terminal-drop** (`AcceptedSeq` < max, `ExpectedNextSeq` null) → loop stops, buffer cleared, no more sends;
- **send-throw then recover** → the same unacked batch is retried (no seq skipped/duplicated on the wire order);
- channel-complete → `RunAsync` returns; cancellation → returns promptly.
ServerConnection (fake hub / the suite's existing ServerConnection test seam): `AcpSessionStartedAsync` /
`SendAcpEventsAsync` invoke the right hub method names with the right payload + are `IsReady`-gated; a simulated
reconnect through `RegisterDaemon` re-invokes `AcpSessionStarted` for each registered active binding **before**
`IsReady` flips (assert ordering).

## Definition of done
- `dotnet build` clean (daemon+core); AOT gate empty
  (`dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`).
- `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` — report pass/total (note the
  known pre-existing flaky `HttpClientExtensionsRetryTests` if it appears).
- Red→green for the forwarder ack state machine (gap + terminal-drop + send-retry) and the reconnect re-bind ordering.

## Report contract
Write detail to `.sdd/task3-serverconnection-forwarder-report.md`. Return only STATUS, commit sha, one-line test
summary, AOT result, the exact server `AcpBatchAck` semantics you encoded to (esp. gap vs terminal-drop), and
concerns. Commit as `AI-688 Option B task 3: ServerConnection ACP hub methods + AcpTranscriptForwarder + reconnect re-bind`.

## HARD RULES
- Read/Edit/Bash yourself only; NO Agent/Task delegation; NO git push. Read the server worktree read-only; never edit it.
- Worktree-only. `await` every TUnit assertion. AOT/trim-safe (source-gen JSON, no reflection).
- Do NOT: build/emit `SessionStarted` (task 4 passes it in), call `AcpSessionStarted` from the forwarder, do the
  final-drain, or touch `AgentOrchestrator` (task 4). Do NOT re-order or re-implement task 1/2 code.
