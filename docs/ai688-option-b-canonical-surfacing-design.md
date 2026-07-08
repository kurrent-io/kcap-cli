# AI-688 Option B — Live ACP transcript surfacing (design)

**Issue:** AI-688 (ACP support — Cursor hosted-agent prototype), child of AI-682.
**Scope of this doc:** make a launched Cursor hosted agent's live turn render as a canonical transcript in the
Kapacitor UI, by feeding the daemon's ACP `session/update` stream into the **already-built** server ingestion
pipeline. Builds on merged AI-684 (ACP foundation) / AI-685 (server ACP canonical mapper) / AI-686 (permission &
elicitation bridge) and on AI-688 **gap 1** (model selection, landed: `eb0d221`).

## 1. Problem & key finding

A `cursor` hosted agent already launches (AI-684 routing) and runs a real turn on a chosen model (gap 1). But the
agent's actual output — assistant messages, reasoning, tool calls — never reaches the server, so the UI shows a
live session with **no transcript**. `AcpHostedAgentRuntime.Updates` (a `ChannelReader<AcpSessionUpdate>` of
`session/update` notifications) is produced but **consumed by nothing** (`grep '\.Updates\b'` → zero prod hits).

**Key finding (investigation):** the *entire* server + UI half of this is **already built, tested, and unused**:
- `AcpSessionMapper.Map` (`src/Capacitor.Server/Sessions/Acp/AcpSessionMapper.cs`) maps every `AcpEventKind`
  (`AssistantText`, `AssistantThinking`, `UserMessage`, `ToolCall`, `ToolResult`, `SessionStarted`, `SessionEnded`)
  → canonical `Kurrent.Agent.Schema` events (`AssistantTextGenerated`, `AssistantThinkingGenerated`,
  `UserMessageReceived`, `AssistantToolCallsGenerated`, `ToolResultReceived`, `SessionStarted`, `SessionEnded`).
- `CapacitorHub.AcpSessionStarted(agentId, vendor, acpSessionId, cwd, model, metadata)` binds the canonical
  session ↔ agent (via `AcpSessionRegistry`) and flips `Agent.SessionId` → the UI's Chat tab lights up.
- `CapacitorHub.AcpSessionEvents(agentId, acpSessionId, AcpEventEnvelope[]) → AcpBatchAck` maps + persists each
  envelope via `SessionWriter.AppendCanonicalToSessionAsync` to `AgentSession-{canonical}`, with per-session
  serialization, seq dedup/gap-detection/terminal-drop.
- The generic `EventContentDispatcher.razor` already renders those canonical events for every vendor.

**Therefore Option B is a daemon-only change. No server or UI changes are required.** Once the daemon calls
`AcpSessionStarted` + `AcpSessionEvents`, the transcript renders automatically.

## 2. Design overview (daemon / kcap-cli)

Four responsibilities, all in the daemon:

1. **Drain** `AcpHostedAgentRuntime.Updates`.
2. **Aggregate** streaming chunks into whole messages/thoughts (turn-boundary aware).
3. **Translate** to `AcpEventEnvelope`s + **synthesize** the lifecycle envelopes `session/update` doesn't carry
   (`SessionStarted`, `UserMessage`, `SessionEnded`), assigning a monotonic `Seq`.
4. **Forward** via two new `ServerConnection` calls (`AcpSessionStarted`, `AcpSessionEvents`) with seq/retry.

### 2.1 Where aggregation lives (key decision)

`agent_message_chunk` / `agent_thought_chunk` stream in pieces; `AcpSessionMapper` is strictly 1 envelope → 1
event, and `ChatTurnBuilder` treats a second `AssistantTextGenerated` in a turn as a *new* response — so naively
forwarding each chunk shreds one reply into many bubbles. Chunks must be coalesced into **one** `AssistantText`
(and one `AssistantThinking`) envelope per contiguous run.

The turn boundary (`stopReason`) is known only to `AcpHostedAgentRuntime.SendPromptAsync` (the `session/prompt`
response), **not** to the raw `Updates` channel. **Decision: aggregation lives in the runtime (or a runtime-owned
`AcpTranscriptForwarder`), not in a bare orchestrator drain loop** — the runtime is the one component that sees
both the chunk stream and the turn boundary, plus it already owns `sessionId`/model/cwd. It exposes a
higher-level `ChannelReader<AcpEventEnvelope>` (already aggregated + sequenced); the orchestrator only forwards it.

Flush rules for the buffered text run:
- flush on a **kind transition** (a message chunk followed by a thought chunk / tool_call / etc.);
- flush the final run on **turn completion** (the `session/prompt` response's `stopReason` arrives);
- flush on **session end** (process exit) as a safety net.

### 2.2 Update-kind → envelope → canonical mapping

| ACP `session/update` (`AcpSessionUpdate.Kind`) | Aggregated? | `AcpEventEnvelope` kind | Canonical event |
|---|---|---|---|
| `agent_message_chunk` | yes (per turn/run) | `AssistantText` | `AssistantTextGenerated` |
| `agent_thought_chunk` | yes (per turn/run) | `AssistantThinking` | `AssistantThinkingGenerated` |
| `tool_call` | no (1:1) | `ToolCall` | `AssistantToolCallsGenerated` |
| `tool_call_update` | no (correlate by `ToolCallId`) | `ToolResult` (on completion/status) | `ToolResultReceived` |
| `plan` | — | *(deferred — no canonical type)* | — → **AI-689** |
| `available_commands_update` | — | *(dropped — not transcript content)* | — |
| unknown | — | dropped (logged) | — |

**Synthesized daemon-side (not from `session/update`):**
- `SessionStarted` — emitted at `Seq = 0` right after `session/new`, carrying `acpSessionId`, `cwd`, resolved
  `model`, vendor `cursor`. Paired with the `AcpSessionStarted` hub bind (§2.3).
- `UserMessage` — synthesized from the initial prompt (and any later `SendUserInputAsync`) so the user's turn
  appears in the transcript.
- `SessionEnded` — synthesized on ACP process exit / stop.

### 2.3 Forwarding & ordering

- New `ServerConnection.AcpSessionStartedAsync(agentId, vendor, acpSessionId, cwd, model, metadata)` →
  `_hub.InvokeAsync("AcpSessionStarted", …)`. Called **once**, after `session/new`, **before** any
  `AcpSessionEvents` (the hub binds the session first; ordering is load-bearing).
- New `ServerConnection.SendAcpEventsAsync(agentId, acpSessionId, AcpEventEnvelope[]) → AcpBatchAck` →
  `_hub.InvokeAsync<AcpBatchAck>("AcpSessionEvents", …)`. Envelopes carry a monotonic `Seq` (starting 0),
  `ContractVersion = 1`, `TimestampIso`. Mirror the existing non-blocking-invoke pattern used by
  `RequestAcpInteractionAsync`.
- **Seq + retry:** maintain a monotonic per-session `Seq`; buffer sent-but-unacked envelopes; on an `AcpBatchAck`
  indicating a gap, resend from `ExpectedNextSeq` (the hub's gap-detection expects a well-behaved retrying client).
  Seq is in-memory only — an ACP session does not survive a daemon restart (the agent dies with it), so no
  cross-restart seq persistence is needed.
- Batching: opportunistic — drain available envelopes and send as a batch; a lone envelope is a batch of one.

### 2.4 Consumption hook

Add `ChannelReader<AcpEventEnvelope>? TranscriptEnvelopes => null` to `IHostedAgentRuntime` (default null;
only `AcpHostedAgentRuntime` overrides). `AgentOrchestrator.HandleLaunchAgent`, at the existing
`EmitsTerminalOutput` special-case (`AgentOrchestrator.cs:471-478`), fires `_ = ForwardAcpTranscriptAsync(agent)`
when the runtime exposes a non-null reader — vendor-neutral, mirrors the existing pattern, adds no PTY branch.

## 3. Scope & deferrals

- **In scope:** assistant message, assistant thinking, tool call, tool result, session start/end, user message;
  seq/retry; the drain+aggregate+forward pipeline; unit tests + a live tool-using E2E.
- **Deferred to AI-689:** `plan` rendering (no canonical event type today), `available_commands` surfacing, rich
  tool-result payload fidelity, reconnect/resume (`session/load`) transcript replay, metrics/diagnostics.
- **AI-687 (separate):** whether Cursor issues client `fs`/`terminal` requests mid-turn — the tool-using E2E here
  is the first chance to observe it; findings feed AI-687, no capability is advertised by this work.
- **Untouched:** AI-686 permission/elicitation bridge (orthogonal, already wired end-to-end); all server + UI code.

## 4. Risks / open questions (for review)

1. **`tool_call` / `tool_call_update` / `agent_thought_chunk` wire shapes are spec-derived, not observed** (the
   only live turn so far was a no-tool prompt). The live tool-using E2E validates them; if real Cursor deviates,
   `AcpSessionUpdate.Reduce()` + the translation adjust. In particular: does `tool_call` carry the tool **input
   args** (needed for `AssistantToolCallsGenerated.ToolInputJson`)? Does the tool **result** arrive as a
   `tool_call_update` or a separate shape? `AcpSessionUpdate` may need extra fields pulled from `Raw`.
2. **Aggregation location** — runtime-owned (recommended §2.1) vs. an orchestrator drain loop with a runtime-emitted
   turn-complete marker. Recommending runtime-owned; open to challenge.
3. **Tool-call/result correlation** across `tool_call` → `tool_call_update` by `ToolCallId`, and whether a single
   canonical `ToolResultReceived` cleanly represents Cursor's status-update model.
4. **Multi-turn / user input mid-session** — `UserMessage` synthesis for `SendUserInputAsync`, and seq continuity
   across multiple prompt turns in one session.
5. **Failure isolation** — a translation/forward error must not kill the live agent; the forward path is
   best-effort + retry, and a persistent forward failure degrades to "no transcript" (session still live), never a
   launch/turn failure.
6. **`stopReason` coupling** — wiring the `session/prompt` response boundary into the aggregator without
   reintroducing the AI-684 "don't await the turn in StartAsync" hang.

## 5. Verification

- **Unit (FakeAcpAgent):** chunk aggregation (many chunks → one envelope; flush on kind-transition/turn-end/session-end);
  kind→envelope translation; seq monotonicity + gap-retry against a fake `AcpBatchAck`; lifecycle synthesis
  (SessionStarted seq 0 before events, UserMessage from prompt, SessionEnded on exit); failure isolation.
- **Live E2E (Team tier, `KCAP_ACP_LIVE=1` gated):** a real **tool-using** prompt (e.g. "run `echo hi` and tell me
  the output") launched through the real daemon → assert the canonical transcript persists (assistant text +
  tool_call + tool_result) AND observe whether Cursor triggers the AI-686 permission path (asking to run the tool).
  This is the combined proof for AI-688's acceptance ("appears as a live session in the UI" + "exercise a
  permission/elicitation path if exposed").
