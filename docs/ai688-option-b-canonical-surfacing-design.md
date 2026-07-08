# AI-688 Option B — Live ACP transcript surfacing (design)

**Issue:** AI-688 (ACP support — Cursor hosted-agent prototype), child of AI-682.
**Scope of this doc:** make a launched Cursor hosted agent's live turn render as a canonical transcript in the
Kapacitor UI, by feeding the daemon's ACP `session/update` stream into the **already-built** server ingestion
pipeline. Builds on merged AI-684 (ACP foundation) / AI-685 (server ACP canonical mapper) / AI-686 (permission &
elicitation bridge) and on AI-688 **gap 1** (model selection, landed: `eb0d221`).

> **Revision r1 (2026-07-07):** incorporates a Codex spec review — 3 blocking + 2 non-blocking findings, all
> addressed. See §6 for the per-finding resolution log; the affected sections are §2.1 (prompt-turn
> serialization), §2.3 (bind ordering + terminal ownership), §2.2 (defensive tool mapping + thinking caveat).

## 1. Problem & key finding

A `cursor` hosted agent already launches (AI-684 routing) and runs a real turn on a chosen model (gap 1). But the
agent's output (assistant messages, reasoning, tool calls) never reaches the server, so the UI shows a live session
with **no transcript**. `AcpHostedAgentRuntime.Updates` (a `ChannelReader<AcpSessionUpdate>` of `session/update`
notifications) is produced but **consumed by nothing**.

**Key finding:** the server + UI half is **already built, tested, and unused**:
- `AcpSessionMapper.Map` maps every `AcpEventKind` (`AssistantText`, `AssistantThinking`, `UserMessage`,
  `ToolCall`, `ToolResult`, `SessionStarted`, `SessionEnded`) → canonical `Kurrent.Agent.Schema` events.
- `CapacitorHub.AcpSessionStarted(agentId, vendor, acpSessionId, cwd, model, metadata)` binds the canonical
  session ↔ agent (via `AcpSessionRegistry`, **requires an already-registered agent**) and sets `Agent.SessionId`.
- `CapacitorHub.AcpSessionEvents(agentId, acpSessionId, AcpEventEnvelope[]) → AcpBatchAck` maps + persists each
  envelope with per-session serialization + seq dedup/gap-detection/terminal-drop.
- The generic `EventContentDispatcher.razor` renders those events for every vendor.

**→ Option B is a daemon-only change for message / tool / lifecycle surfacing — no server or UI change required
there.** **One exception (Codex finding 4):** `ChatTurnBuilder` *filters `AssistantThinkingGenerated` out of the
Chat-tab transcript*, so reasoning will persist + render in the event-detail view but will NOT appear in the Chat
tab without a small separate server-side `ChatTurnBuilder` tweak — deferred to AI-689 (§3). Everything else renders
as-is once the daemon feeds the pipeline.

## 2. Design overview (daemon / kcap-cli)

Four responsibilities, all in the daemon:

1. **Drain** the runtime's ACP updates.
2. **Aggregate** streaming chunks into whole messages/thoughts, per serialized prompt turn.
3. **Translate** to `AcpEventEnvelope`s + **synthesize** the lifecycle envelopes `session/update` doesn't carry
   (`UserMessage`, `SessionStarted`, and — subject to §2.3 — `SessionEnded`), assigning a monotonic `Seq`.
4. **Forward** via two new `ServerConnection` calls (`AcpSessionStarted`, `AcpSessionEvents`) with seq/retry,
   **driven by the orchestrator, never by the runtime during `StartAsync`**.

### 2.1 Aggregation + prompt-turn serialization (key decision; Codex findings 2 + 1)

`agent_message_chunk` / `agent_thought_chunk` stream in pieces; `AcpSessionMapper` is strictly 1 envelope → 1 event
and `ChatTurnBuilder` treats a second `AssistantTextGenerated` in a turn as a *new* response — so chunks must
coalesce into **one** `AssistantText` (and one `AssistantThinking`) envelope per contiguous run.

The turn boundary (`stopReason`) is known only to `SendPromptAsync` (the `session/prompt` response), not the raw
update channel — and today the runtime fires `session/prompt` as *background work for both launch and
`SendUserInputAsync`*, so **multiple prompt turns can overlap and a late `stopReason` could flush the wrong
buffer.** Decisions:

- **Aggregation lives in the runtime** (a runtime-owned forwarder/aggregator) — it is the one component that sees
  both the chunk stream and the turn boundary, and owns `sessionId`/model/cwd.
- **Prompt turns are serialized (single-flight FIFO queue) inside the runtime.** Public APIs (the initial launch
  prompt, `SendUserInputAsync`) *enqueue* a turn and return immediately (stay non-blocking, preserving AI-684's
  "don't await the turn in `StartAsync`" contract). A single worker processes turns in order: for each turn it
  emits the `UserMessage` envelope, sends `session/prompt`, awaits *that* response, and flushes *that* turn's
  buffered `AssistantText`/`AssistantThinking` on *its own* `stopReason`. Overlapping inputs therefore cannot
  cross-contaminate a buffer, and a `stopReason` always flushes the correct turn.
- The runtime exposes the aggregated, sequenced result as a `ChannelReader<AcpEventEnvelope>` (§2.4). Flush a
  buffered text run on: a kind transition, the turn's own `stopReason`, or session end.

### 2.2 Update-kind → envelope → canonical mapping (Codex finding 5 hardening)

| ACP `session/update` (`AcpSessionUpdate.Kind`) | Aggregated? | `AcpEventEnvelope` kind | Canonical event |
|---|---|---|---|
| `agent_message_chunk` | yes (per turn/run) | `AssistantText` | `AssistantTextGenerated` |
| `agent_thought_chunk` | yes (per turn/run) | `AssistantThinking` | `AssistantThinkingGenerated`¹ |
| `tool_call` | no (1:1) | `ToolCall` | `AssistantToolCallsGenerated` |
| `tool_call_update` | correlate by `ToolCallId` | `ToolResult`² | `ToolResultReceived` |
| `plan` | — | *(deferred — no canonical type)* | — → **AI-689** |
| `available_commands_update` | — | *(dropped — not transcript content)* | — |
| unknown `sessionUpdate` | — | dropped, but **`Raw` logged** (never silently) | — |

¹ persists + renders in event detail; NOT in the Chat tab until the `ChatTurnBuilder` tweak (§1, deferred).
² **Defensive rules** (the shapes are spec-derived, not probe-confirmed):
  - `AcpSessionUpdate.Reduce()` currently captures only id/title/kind/status — it must be **extended to pull the
    tool INPUT args from `Raw`** into `ToolCall`'s `ToolInputJson` (else `AssistantToolCallsGenerated` has no args),
    and any result payload for `ToolResult`.
  - A **status-only** `tool_call_update` (no extractable result content) updates in-memory correlation state but
    **does NOT emit an empty `ToolResultReceived`**. A `ToolResult` envelope is emitted **only on a
    terminal/completed update that carries extractable result content**.
  - Unknown/uncertain shapes are preserved + logged via `Raw`; the **live tool-using E2E is the gate** that
    confirms the real `tool_call`/`tool_call_update`/`agent_thought_chunk` shapes before the mapping asserts them.

**Synthesized daemon-side (not from `session/update`):**
- `SessionStarted` — `Seq = 0`, paired with the `AcpSessionStarted` bind (§2.3), carrying `acpSessionId`, `cwd`,
  resolved `model`, vendor `cursor`.
- `UserMessage` — emitted at the start of each serialized turn (initial prompt + each `SendUserInputAsync`).
- `SessionEnded` — see §2.3 (terminal ownership) for who emits it and when.

### 2.3 Forwarding, bind ordering & terminal ownership (Codex findings 1 + 3)

**Bind ordering (finding 1) — the runtime NEVER calls the hub during `StartAsync`.** `AcpSessionStarted` requires an
*already-registered* agent, but `StartAsync` completes `session/new` *before* the orchestrator registers the agent.
So the sequence is strictly:
1. `AcpHostedAgentRuntime.StartAsync` completes initialize + `session/new` + model-selection and **exposes**
   `acpSessionId` / `cwd` / resolved `model` (metadata) + the `TranscriptEnvelopes` reader — but calls no hub method.
2. `AgentOrchestrator.HandleLaunchAgent` registers the agent (`RegisterAgentAsync`), exactly as today.
3. **Only then** the orchestrator starts the forwarder, which (a) calls
   `ServerConnection.AcpSessionStartedAsync(agentId, vendor, acpSessionId, cwd, model, metadata)` **once**, then
   (b) streams `AcpSessionEvents` (starting with the `SessionStarted@Seq0` envelope). `AcpSessionStarted` is thus
   never invoked before the agent is registered, and always before any `AcpSessionEvents`.

**Forwarding + seq/retry.** `ServerConnection.SendAcpEventsAsync(agentId, acpSessionId, AcpEventEnvelope[]) →
AcpBatchAck` invokes the hub; envelopes carry a monotonic `Seq` (from 0), `ContractVersion = 1`, `TimestampIso`.
Maintain a per-session seq counter + a buffer of sent-but-unacked envelopes; on an `AcpBatchAck` reporting a gap,
resend from `ExpectedNextSeq`. Seq is in-memory only (an ACP session dies with the daemon). Mirror the existing
non-blocking-invoke pattern (`RequestAcpInteractionAsync`).

**Terminal ownership (finding 3) — single owner, flush-before-finalize.** The existing
`AgentOrchestrator.EndAgentSessionAsync` (on ACP process exit) already marks the server-side ACP binding terminal;
after that, further `AcpSessionEvents` are terminal-dropped (acked WITHOUT advancing `AcceptedSeq`, `ExpectedNextSeq
== null`). To avoid a synthesized `SessionEnded` racing the finalizer and being silently dropped:
- On process exit the orchestrator (a) stops the prompt-turn queue, (b) **flushes** the final buffered
  message/thought and drains remaining `TranscriptEnvelopes` to `AcpSessionEvents`, and (c) **then** calls
  `EndAgentSessionAsync`. The final transcript flush always precedes finalization.
- **Exactly one owner emits the canonical `SessionEnded`.** Implementation MUST first determine whether
  `EndAgentSessionAsync` already writes a canonical `SessionEnded` server-side: if it does, the forwarder does NOT
  emit its own; if it does not, the forwarder emits `SessionEnded` as the *last drained envelope before*
  `EndAgentSessionAsync`. Never both, never after finalization.
- **Forwarder terminal-drop handling:** if an `AcpBatchAck` indicates terminal drop (`AcceptedSeq` < max-sent AND
  `ExpectedNextSeq == null`), the forwarder treats the session as terminal — **stops retrying and clears the
  unacked buffer** (no infinite resend against a terminal binding).

### 2.4 Consumption hook

Add `ChannelReader<AcpEventEnvelope>? TranscriptEnvelopes => null` to `IHostedAgentRuntime` (default null; only
`AcpHostedAgentRuntime` overrides). At the existing `EmitsTerminalOutput` special-case in
`AgentOrchestrator.HandleLaunchAgent` (and **after** `RegisterAgentAsync`, per §2.3), the orchestrator fires
`_ = ForwardAcpTranscriptAsync(agent, acpRuntime)` when the reader is non-null — vendor-neutral, mirrors the
existing pattern, adds no PTY branch.

## 3. Scope & deferrals

- **In scope:** assistant message, assistant thinking (persisted; event-detail render), tool call, tool result,
  session start/end, user message; prompt-turn serialization; seq/retry; the drain+aggregate+forward pipeline;
  unit tests + a live tool-using E2E.
- **Deferred to AI-689:** the `ChatTurnBuilder` change to show `AssistantThinkingGenerated` in the Chat tab;
  `plan` rendering (no canonical event type); `available_commands`; rich tool-result payload fidelity;
  reconnect/resume (`session/load`) transcript replay; metrics/diagnostics.
- **AI-687 (separate):** whether Cursor issues client `fs`/`terminal` requests mid-turn — the tool-using E2E here
  is the first chance to observe it; findings feed AI-687, no capability is advertised by this work.
- **Untouched:** AI-686 permission/elicitation bridge (orthogonal, already wired end-to-end); all server + UI code
  (except the deferred, optional `ChatTurnBuilder` thinking tweak, which is out of this issue's scope).

## 4. Risks / open questions

1. **`tool_call` / `tool_call_update` / `agent_thought_chunk` wire shapes are spec-derived, not observed.** The
   live tool-using E2E validates them; defensive mapping (§2.2) means a wrong guess degrades to "no tool result"
   rather than an empty/incorrect event. Open: does `tool_call` carry input args; does the result arrive as a
   `tool_call_update` or a separate shape.
2. **Tool-call/result correlation** across `tool_call` → `tool_call_update` by `ToolCallId`; whether one canonical
   `ToolResultReceived` cleanly represents Cursor's status-update model.
3. **Multi-turn seq continuity** across serialized turns in one session; interplay with the hub's
   `AcpDeterministicId` dedup.
4. **`SessionEnded` owner** (§2.3) — must be settled against the actual `EndAgentSessionAsync` behavior during
   implementation (does it already write a canonical `SessionEnded`?).

## 5. Verification

- **Unit (FakeAcpAgent):** chunk aggregation (many chunks → one envelope; flush on kind-transition/turn-end/session-end);
  **prompt-turn serialization** (two overlapping inputs → each `stopReason` flushes its own buffer, no cross-contamination);
  kind→envelope translation incl. the defensive tool rules (status-only update emits no empty `ToolResult`);
  seq monotonicity + gap-retry + **terminal-drop clears the buffer** against a fake `AcpBatchAck`;
  lifecycle synthesis (SessionStarted seq 0 emitted only after agent registration + before events, via the
  orchestrator; UserMessage per turn; SessionEnded ownership + flush-before-finalize); failure isolation.
- **Live E2E (Team tier, `KCAP_ACP_LIVE=1` gated):** a real **tool-using** prompt (e.g. "run `echo hi` and tell me
  the output") launched through the real daemon → assert the canonical transcript persists (assistant text +
  tool_call + tool_result) AND observe whether Cursor triggers the AI-686 permission path. Combined proof for
  AI-688's acceptance ("appears as a live session in the UI" + "exercise a permission/elicitation path if exposed").
  This run is also the gate that confirms the tool/thought wire shapes before the assertions are enabled.

## 6. Codex spec review — resolution log (r1)

1. **[BLOCKING] Bind ordering vs agent registration** → §2.3: the runtime never calls the hub during `StartAsync`;
   the orchestrator, *after* `RegisterAgentAsync`, drives the forwarder which calls `AcpSessionStarted` then
   `AcpSessionEvents`.
2. **[BLOCKING] Overlapping prompt turns / wrong-buffer flush** → §2.1: prompt turns are serialized in a
   single-flight FIFO worker; each turn emits its `UserMessage`, awaits its own `stopReason`, and flushes its own
   buffer; public launch/input APIs stay non-blocking.
3. **[BLOCKING] `SessionEnded` vs existing finalizer / terminal-drop** → §2.3: `EndAgentSessionAsync` stays the
   single terminal owner; final transcript flush is ordered *before* it; exactly one owner emits canonical
   `SessionEnded`; forwarder stops + clears the unacked buffer on a terminal-drop ack.
4. **[NON-BLOCKING] "already built" overstates thinking-in-Chat** → §1/§3: claim narrowed — thinking persists +
   renders in event detail but is filtered from the Chat tab by `ChatTurnBuilder`; the Chat-tab tweak is deferred
   to AI-689.
5. **[NON-BLOCKING] Tool mapping must be more defensive** → §2.2: capture tool args/result from `Raw`; emit
   `ToolResult` only on a terminal update with extractable content (no empty results); preserve/log unknown `Raw`;
   the live E2E gates enabling the tool assertions.
