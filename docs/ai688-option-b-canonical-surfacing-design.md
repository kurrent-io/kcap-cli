# AI-688 Option B — Live ACP transcript surfacing (design)

**Issue:** AI-688 (ACP support — Cursor hosted-agent prototype), child of AI-682.
**Scope of this doc:** make a launched Cursor hosted agent's live turn render as a canonical transcript in the
Kapacitor UI, by feeding the daemon's ACP `session/update` stream into the **already-built** server ingestion
pipeline. Builds on merged AI-684 (ACP foundation) / AI-685 (server ACP canonical mapper) / AI-686 (permission &
elicitation bridge) and on AI-688 **gap 1** (model selection, landed: `eb0d221`).

> **Revision r1 (2026-07-07):** Codex spec review — 3 blocking + 2 non-blocking, all addressed (§6).
> **Revision r2 (2026-07-07):** Codex spec review round 2 — 2 blocking + 1 non-blocking, all addressed (§6):
> reconnect re-bind (§2.3), bounded final-drain (§2.3), concrete bind-handoff record (§2.4).
> **Revision r3 (2026-07-07):** Codex spec review round 3 — **NO BLOCKING FINDINGS**; 3 non-blocking clarifications
> folded in (§6): reconnect gating via `ConnectionRetry`/`IsReady` (§2.3), forwarder emits NO `session_ended`
> (`EndAgentSession` is the owner) (§2.3), daemon-local wire DTOs + `CapacitorJsonContext` (§2.4). **Spec LOCKED.**

## 1. Problem & key finding

A `cursor` hosted agent already launches (AI-684 routing) and runs a real turn on a chosen model (gap 1). But the
agent's output (assistant messages, reasoning, tool calls) never reaches the server, so the UI shows a live session
with **no transcript**. `AcpHostedAgentRuntime.Updates` (a `ChannelReader<AcpSessionUpdate>` of `session/update`
notifications) is produced but **consumed by nothing**.

**Key finding:** the server + UI half is **already built, tested, and unused**:
- `AcpSessionMapper.Map` maps every `AcpEventKind` (`AssistantText`, `AssistantThinking`, `UserMessage`,
  `ToolCall`, `ToolResult`, `SessionStarted`, `SessionEnded`) → canonical `Kurrent.Agent.Schema` events.
- `CapacitorHub.AcpSessionStarted(agentId, vendor, acpSessionId, cwd, model, metadata)` binds the canonical
  session ↔ agent (via `AcpSessionRegistry`, **requires an already-registered agent**, **idempotent on re-bind**)
  and sets `Agent.SessionId`.
- `CapacitorHub.AcpSessionEvents(agentId, acpSessionId, AcpEventEnvelope[]) → AcpBatchAck` maps + persists each
  envelope with per-session serialization + seq dedup/gap-detection/terminal-drop. **It does NOT create or recover
  a binding and throws when the session is unbound** — the bind (`AcpSessionStarted`) must always precede it,
  including after a reconnect (§2.3).
- The generic `EventContentDispatcher.razor` renders those events for every vendor.

**→ Option B is a daemon-only change for message / tool / lifecycle surfacing.** **One exception (finding 4):**
`ChatTurnBuilder` filters `AssistantThinkingGenerated` out of the Chat-tab transcript, so reasoning persists +
renders in the event-detail view but will NOT appear in the Chat tab without a small separate server-side
`ChatTurnBuilder` tweak — deferred to AI-689 (§3).

## 2. Design overview (daemon / kcap-cli)

Four responsibilities, all in the daemon:

1. **Drain** the runtime's ACP updates.
2. **Aggregate** streaming chunks into whole messages/thoughts, per serialized prompt turn.
3. **Translate** to `AcpEventEnvelope`s + **synthesize** the lifecycle envelopes `session/update` doesn't carry
   (`UserMessage`, `SessionStarted`, and — subject to §2.3 — `SessionEnded`), assigning a monotonic `Seq`.
4. **Forward** via two new `ServerConnection` calls (`AcpSessionStarted`, `AcpSessionEvents`) with seq/retry,
   **driven by the orchestrator, never by the runtime during `StartAsync`**.

### 2.1 Aggregation + prompt-turn serialization (findings 2 + 1, r1)

`agent_message_chunk` / `agent_thought_chunk` stream in pieces; `AcpSessionMapper` is strictly 1 envelope → 1 event
and `ChatTurnBuilder` treats a second `AssistantTextGenerated` in a turn as a *new* response — so chunks must
coalesce into **one** `AssistantText` (and one `AssistantThinking`) envelope per contiguous run.

The turn boundary (`stopReason`) is known only to `SendPromptAsync`, not the raw update channel — and the runtime
fires `session/prompt` as *background work for both launch and `SendUserInputAsync`*, so overlapping turns could
flush the wrong buffer. Decisions:

- **Aggregation is runtime-owned** — the runtime sees both the chunk stream and the turn boundary and owns
  `sessionId`/model/cwd.
- **Prompt turns are serialized (single-flight FIFO queue) inside the runtime.** Public APIs (initial launch
  prompt, `SendUserInputAsync`) *enqueue* a turn and return immediately (non-blocking, preserving AI-684's "don't
  await the turn in `StartAsync`"). A single worker processes turns in order: emit the `UserMessage` envelope, send
  `session/prompt`, await *that* response, flush *that* turn's buffer on its own `stopReason`. Overlapping inputs
  can't cross-contaminate; a `stopReason` always flushes the correct turn.
- **The worker/queue is cancellable** (see §2.3 bounded final-drain): a turn that never returns a `stopReason` must
  never pin shutdown.
- The runtime exposes the aggregated, sequenced result as a `ChannelReader<AcpEventEnvelope>` (§2.4). Flush a run
  on: a kind transition, the turn's own `stopReason`, or session end.

### 2.2 Update-kind → envelope → canonical mapping (finding 5 hardening)

| ACP `session/update` (`AcpSessionUpdate.Kind`) | Aggregated? | `AcpEventEnvelope` kind | Canonical event |
|---|---|---|---|
| `agent_message_chunk` | yes (per turn/run) | `AssistantText` | `AssistantTextGenerated` |
| `agent_thought_chunk` | yes (per turn/run) | `AssistantThinking` | `AssistantThinkingGenerated`¹ |
| `tool_call` | no (1:1) | `ToolCall` | `AssistantToolCallsGenerated` |
| `tool_call_update` | correlate by `ToolCallId` | `ToolResult`² | `ToolResultReceived` |
| `plan` | — | *(deferred — no canonical type)* | — → **AI-689** |
| `available_commands_update` | — | *(dropped — not transcript content)* | — |
| unknown `sessionUpdate` | — | dropped, but **`Raw` logged** | — |

¹ persists + renders in event detail; NOT in the Chat tab until the `ChatTurnBuilder` tweak (§1/§3, deferred).
² **Defensive rules** (shapes are spec-derived, not probe-confirmed): extend `AcpSessionUpdate.Reduce()` to pull
  the tool INPUT args from `Raw` into `ToolCall`'s `ToolInputJson` and any result payload for `ToolResult` (today it
  captures only id/title/kind/status; `AcpEventEnvelope` already has `ToolInputJson`/`ToolResult` fields ready); a
  **status-only** `tool_call_update` updates correlation state but emits **no** empty `ToolResultReceived`; a
  `ToolResult` envelope is emitted **only on a terminal/completed update with extractable result content**; unknown
  shapes are preserved + logged via `Raw`; the **live tool-using E2E gates** enabling the tool assertions.

**Synthesized daemon-side:** `SessionStarted` (`Seq 0`, paired with the `AcpSessionStarted` bind); `UserMessage`
(per serialized turn). **`SessionEnded` is NOT synthesized by the forwarder** — under the current server contract
`CapacitorHub.EndAgentSession` is the ACP `SessionEnded` owner (§2.3).

### 2.3 Forwarding, bind ordering, reconnect & terminal ownership (findings 1 + 3, r1; findings 1 + 2, r2)

**Bind ordering (r1 finding 1) — the runtime NEVER calls the hub during `StartAsync`.** `AcpSessionStarted`
requires an *already-registered* agent, but `StartAsync` completes `session/new` *before* registration. Sequence:
1. `AcpHostedAgentRuntime.StartAsync` completes initialize + `session/new` + model-selection and **exposes** the
   ACP metadata (`acpSessionId`/`cwd`/resolved `model`) + the transcript reader (§2.4) — calling no hub method.
2. `AgentOrchestrator.HandleLaunchAgent` registers the agent (`RegisterAgentAsync`) as today.
3. **Only then** the orchestrator starts the forwarder, which calls
   `ServerConnection.AcpSessionStartedAsync(agentId, vendor, acpSessionId, cwd, model, metadata)`, then streams
   `AcpSessionEvents` (starting with `SessionStarted@Seq0`). `AcpSessionStarted` is never invoked before the agent
   is registered, and always before any `AcpSessionEvents`.

**Reconnect / re-bind (r2 finding 1) — `AcpSessionStarted` is NOT "call once".** SignalR daemon↔server reconnects
happen routinely; `AcpSessionEvents` neither creates nor recovers a binding and **throws when the session is
unbound**. So the daemon's existing agent **re-register path** (on reconnect) MUST also **re-invoke
`AcpSessionStarted` idempotently** (safe per `AcpSessionRegistry`'s same-agent re-bind) **before** sending or
replaying any further `AcpSessionEvents`, then **resume from the unacked buffer / ack cursor** (the seq state and
unacked buffer survive the reconnect in-memory). Wire this into the same re-register hook the daemon already uses
for agents on reconnect. Deeper resilience (backoff tuning, long-outage replay depth) is AI-689; the re-bind itself
is in scope here because without it the transcript breaks on the first reconnect.

**Enforcement (r3 finding 1):** the new `AcpSessionStartedAsync` / `SendAcpEventsAsync` calls MUST go through the
same `ConnectionRetry` + `IsReady` gating as existing `ServerConnection` hub calls. `RegisterDaemon` runs agent
re-registration (which now includes the ACP re-bind) **before** `IsReady` returns, and `IsReady` is the retry gate —
so post-reconnect event batches cannot beat the re-bind (they block on `IsReady` until the re-bind has run).

**Forwarding + seq/retry.** `ServerConnection.SendAcpEventsAsync(agentId, acpSessionId, AcpEventEnvelope[]) →
AcpBatchAck` invokes the hub; envelopes carry a monotonic `Seq` (from 0), `ContractVersion = 1`, `TimestampIso`.
Per-session seq counter + a buffer of sent-but-unacked envelopes; on an `AcpBatchAck` reporting a gap, resend from
`ExpectedNextSeq`. Seq is in-memory only. Mirror the non-blocking-invoke pattern (`RequestAcpInteractionAsync`).

**Terminal ownership (r1 finding 3; r2 finding 2 bounded drain) — single owner, bounded flush-before-finalize.**
The existing `AgentOrchestrator.EndAgentSessionAsync` (on ACP process exit) already marks the ACP binding terminal
and is **deliberately time-budgeted so cleanup is never pinned by a server outage**; after it, further
`AcpSessionEvents` are terminal-dropped (acked WITHOUT advancing `AcceptedSeq`, `ExpectedNextSeq == null`). To avoid
a synthesized `SessionEnded` racing the finalizer AND to preserve that outage budget:
- On process exit the orchestrator (a) **cancels/completes** the prompt-turn queue (a turn awaiting a `stopReason`
  that will never come must not block), (b) **flushes** the final buffered message/thought and drains remaining
  transcript envelopes to `AcpSessionEvents` **under a finite drain budget**, in a `finally`, and (c) **always
  proceeds** to `EndAgentSessionAsync`/cleanup when the budget elapses — logging best-effort transcript loss rather
  than pinning shutdown. The final flush is *attempted* before finalization but never at the cost of the cleanup
  guarantee.
- **`SessionEnded` owner is the server (r3 finding 2):** under the current contract `CapacitorHub.EndAgentSession`
  is already the ACP-vendor `SessionEnded` owner and marks the binding terminal on confirmed success. Therefore the
  AI-688 forwarder **does NOT emit a `session_ended` envelope at all** — it drains the transcript envelopes (message/
  tool) under the budget, then always calls `EndAgentSessionAsync`, which produces the terminal `SessionEnded`.
- **Forwarder terminal-drop handling:** on a terminal-drop ack (`AcceptedSeq` < max-sent AND
  `ExpectedNextSeq == null`), the forwarder treats the session as terminal — stops retrying and clears the unacked
  buffer (no infinite resend against a terminal binding).

### 2.4 Consumption hook + bind-handoff (r1 finding; r2 finding 3)

The orchestrator needs `acpSessionId` / `cwd` / resolved `model` to call `AcpSessionStarted` AND the transcript
reader to forward — but today `HostedRuntimeStart` carries only `Runtime` + `McpConfigPath`. **Make the handoff
concrete (r2 finding 3):** the ACP factory returns an ACP-specific transcript source — e.g. an `IAcpTranscriptSource`
(or a nullable `AcpTranscript` record on `HostedRuntimeStart`) exposing `AcpSessionId`, `Cwd`, `ResolvedModel`, and
`ChannelReader<AcpEventEnvelope> Envelopes` — so the orchestrator binds + forwards without downcasting the runtime
or re-deriving state. `IHostedAgentRuntime`/PTY runtimes leave it null. At the existing `EmitsTerminalOutput`
special-case in `HandleLaunchAgent` (and **after** `RegisterAgentAsync`, §2.3), the orchestrator fires
`_ = ForwardAcpTranscriptAsync(agent, transcriptSource)` when the source is non-null — vendor-neutral, no PTY branch.

**Wire DTOs (r3 finding 3):** the daemon (`Capacitor.Cli.Daemon`) currently references only `Capacitor.Cli.Core`
and has **no** `AcpEventEnvelope` / `AcpBatchAck` definitions. Add **daemon-local wire records** mirroring the
server contract (field-for-field: `AcpEventEnvelope` with `Seq`/`Kind`/`ContractVersion`/`TimestampIso`/text/
`ToolInputJson`/`ToolResult`/etc.; `AcpBatchAck` with `AcceptedSeq`/`ExpectedNextSeq`) in `Capacitor.Cli.Core`, and
**register them in `CapacitorJsonContext`** (source-gen) for NativeAOT-safe SignalR serialization — the same
pattern the existing daemon↔server DTOs use.

## 3. Scope & deferrals

- **In scope:** assistant message, assistant thinking (persisted; event-detail render), tool call, tool result,
  session start/end, user message; prompt-turn serialization; seq/retry; **reconnect re-bind** + **bounded
  final-drain** (basics); the drain+aggregate+forward pipeline; unit tests + a live tool-using E2E.
- **Deferred to AI-689:** the `ChatTurnBuilder` change to show `AssistantThinkingGenerated` in the Chat tab;
  `plan` rendering; `available_commands`; rich tool-result payload fidelity; reconnect/resume (`session/load`)
  transcript replay + **deeper reconnect resilience (backoff tuning, long-outage handling)**; metrics/diagnostics.
- **AI-687 (separate):** whether Cursor issues client `fs`/`terminal` requests mid-turn — the tool-using E2E is the
  first chance to observe it; findings feed AI-687, no capability advertised here.
- **Untouched:** AI-686 permission/elicitation bridge (orthogonal, already wired); all server + UI code (except the
  deferred, optional `ChatTurnBuilder` thinking tweak, out of this issue's scope).

## 4. Risks / open questions

1. **`tool_call` / `tool_call_update` / `agent_thought_chunk` wire shapes are spec-derived, not observed.** The live
   tool-using E2E validates them; defensive mapping (§2.2) degrades a wrong guess to "no tool result", not an
   empty/incorrect event. Open: does `tool_call` carry input args; does the result arrive as a `tool_call_update`
   or a separate shape.
2. **Tool-call/result correlation** by `ToolCallId`; whether one `ToolResultReceived` cleanly represents Cursor's
   status-update model.
3. **Multi-turn seq continuity** across serialized turns; interplay with the hub's `AcpDeterministicId` dedup;
   seq/unacked-buffer continuity across a reconnect (§2.3).
4. **`SessionEnded` owner + `EndAgentSessionAsync` behavior** (§2.3) — settle against the actual finalizer during
   implementation (does it already write a canonical `SessionEnded`? what is its drain budget?).

## 5. Verification

- **Unit (FakeAcpAgent):** chunk aggregation; **prompt-turn serialization** (two overlapping inputs → each
  `stopReason` flushes its own buffer); kind→envelope translation incl. defensive tool rules (status-only update
  emits no empty `ToolResult`); seq monotonicity + gap-retry + **terminal-drop clears the buffer**; **reconnect
  re-bind** (simulated reconnect → `AcpSessionStarted` re-invoked before any resend, then resume from the ack
  cursor); **bounded final-drain** (a turn with no `stopReason` at exit → drain times out → `EndAgentSessionAsync`
  still proceeds, transcript-loss logged); lifecycle synthesis (SessionStarted seq 0 emitted by the orchestrator
  after registration + before events; UserMessage per turn; **forwarder emits NO `session_ended` — `EndAgentSession`
  owns it**); failure isolation (a translate/forward error never kills the live agent or the turn).
- **Live E2E (Team tier, `KCAP_ACP_LIVE=1` gated):** a real **tool-using** prompt ("run `echo hi` and tell me the
  output") through the real daemon → assert the canonical transcript persists (assistant text + tool_call +
  tool_result) AND observe whether Cursor triggers the AI-686 permission path. Combined proof for AI-688's
  acceptance; also the gate that confirms the tool/thought wire shapes before the assertions are enabled.

## 6. Codex spec review — resolution log

**Round 1 (r1):**
1. [BLOCKING] Bind ordering vs agent registration → §2.3 (runtime never calls the hub; orchestrator drives it after
   `RegisterAgentAsync`).
2. [BLOCKING] Overlapping prompt turns / wrong-buffer flush → §2.1 (single-flight FIFO worker).
3. [BLOCKING] `SessionEnded` vs finalizer → §2.3 (single terminal owner; flush before finalize; terminal-drop clears buffer).
4. [NON-BLOCKING] "already built" overstated thinking-in-Chat → §1/§3 (narrowed; Chat-tab tweak deferred AI-689).
5. [NON-BLOCKING] Tool mapping must be defensive → §2.2 (args/result from `Raw`; no empty `ToolResult`; live-E2E gate).

**Round 2 (r2):**
1. [BLOCKING] Reconnect re-bind → §2.3 (`AcpSessionStarted` is not "once"; re-invoke idempotently on the reconnect
   re-register path before resuming `AcpSessionEvents` from the ack cursor). Deeper resilience → AI-689.
2. [BLOCKING] Bounded final-drain → §2.3 (finite drain budget; cancel the prompt queue on exit; flush in `finally`;
   always proceed to `EndAgentSessionAsync` after the budget, preserving the existing outage-cleanup guarantee).
3. [NON-BLOCKING] Concrete bind-handoff API → §2.4 (`IAcpTranscriptSource`/`AcpTranscript` record carrying
   `AcpSessionId`/`Cwd`/`ResolvedModel`/`Envelopes`, so the orchestrator binds without downcasting/re-deriving).

**Round 3 (r3) — NO BLOCKING FINDINGS:**
1. [NON-BLOCKING] Reconnect enforcement → §2.3 (new hub calls use the same `ConnectionRetry`/`IsReady` gating;
   `RegisterDaemon` re-binds before `IsReady` returns, so post-reconnect batches can't beat the re-bind).
2. [NON-BLOCKING] `SessionEnded` owner made concrete → §2.2/§2.3 (server `EndAgentSession` is the owner; the
   forwarder emits no `session_ended` — drains transcript, then always calls `EndAgentSessionAsync`).
3. [NON-BLOCKING] Daemon wire DTOs → §2.4 (add daemon-local `AcpEventEnvelope`/`AcpBatchAck` mirror records in
   `Capacitor.Cli.Core` + register in `CapacitorJsonContext` for NativeAOT SignalR).
