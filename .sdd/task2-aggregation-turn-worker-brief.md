# AI-688 Option B — Task 2: aggregation + serialized prompt-turn worker (implementer brief)

Restructure `AcpHostedAgentRuntime` so it produces an ordered, aggregated `AcpEventEnvelope` transcript stream from
its ACP `session/update` notifications + serialized prompt turns. Builds on task 1 (`AcpEventTranslator`, the wire
DTOs, `AcpSessionUpdate.Reduce()` — all landed at `ffe80a6`). **NOT in scope:** seq assignment, `ServerConnection`/
forwarding, `AcpSessionStarted`, `SessionStarted` emission, orchestrator wiring (tasks 3–4). Do not touch those.

Work ONLY in `/Users/tony/Documents/kcap-cli-wt/ai-688-cursor-hosted-agent-prototype`, branch
`ai-688-cursor-hosted-agent-prototype`. Commit; **do NOT push**; no delegation. Spec:
`docs/ai688-option-b-canonical-surfacing-design.md` §2.1 (this task) + §2.4 (the `IAcpTranscriptSource` handoff).

## What exists today (read first)
`AcpHostedAgentRuntime.StartAsync` does initialize → session/new → (gap 1) model-select → then fires the initial
`session/prompt` as background work via `FireAndTrackPromptAsync`/`SendPromptAsync` **without awaiting the turn**
(AI-684 Fix E), and returns once the session is established. `SendUserInputAsync` also fires prompts. `session/update`
notifications flow through `HandleNotification` → `Reduce` → the existing `Updates` (`ChannelReader<AcpSessionUpdate>`).
**Problem this task fixes:** prompts fire as *concurrent* background work (overlapping turns), and nothing aggregates
the chunk stream — so a late `stopReason` could flush the wrong buffer and `AcpSessionMapper`/`ChatTurnBuilder` would
shred a streamed reply into many bubbles.

## Design (§2.1) — build these

### 1. Serialized prompt-turn worker (single-flight FIFO)
- Add an internal FIFO queue of pending prompt turns + a SINGLE long-running worker task. Public entry points
  (the initial launch prompt from `StartAsync`, and `SendUserInputAsync`) **enqueue** a turn and **return
  immediately** — `StartAsync` must keep its non-blocking contract (session established → return; never await a
  turn). Replace the current ad-hoc `FireAndTrackPromptAsync` firing with enqueue-onto-the-queue.
- The worker processes turns strictly in order. Per turn: (a) produce a **`UserMessage`** envelope (via
  `AcpEventTranslator.BuildUserMessage`) into the transcript channel; (b) send `session/prompt` and **await its
  response** (the `stopReason`) — reuse the existing `SendPromptAsync` send/await; (c) on that response, perform the
  **turn-end flush** (below). Because turns are serialized, exactly one turn's updates are ever in flight, so the
  aggregation buffer unambiguously belongs to the active turn.
- **Cancellable:** the worker + queue honor the runtime's `CancellationToken`/dispose. A turn whose `stopReason`
  never arrives must NOT pin shutdown — on cancel/dispose, cancel the in-flight await, stop the worker, and complete
  the transcript channel. (The bounded-drain-before-finalize policy itself is task 4; task 2 must simply be
  promptly cancellable and not deadlock.)

### 2. Chunk aggregation
- A single aggregation path owns a **current text run** (kind = message or thinking + accumulated text). Consume the
  reduced `AcpSessionUpdate`s (from the existing `Updates` source / `HandleNotification`). Rules:
  - `AgentMessageChunk` / `AgentThoughtChunk`: if a run of the SAME kind is open, append its `Text`; else flush any
    open run (below) and start a new run of this kind.
  - Any OTHER kind (`ToolCall`, `ToolCallUpdate`, `Plan`, `AvailableCommands`, `Unknown`): **flush** the open run
    first (kind-transition), then translate the update 1:1 via `AcpEventTranslator.Translate` and, if non-null,
    write that envelope to the channel (so tool_call / tool_result land as their own envelopes, in order).
  - **Turn-end flush** (worker, on `stopReason`) and **session-end flush** (on the runtime stopping): flush the open
    run. Flushing a run = translate via `AcpEventTranslator.Translate(update-of-that-kind, aggregatedText: buffer)`
    (build a representative update or call an overload) → write ONE `AssistantText`/`AssistantThinking` envelope,
    then clear the buffer.
- **Thread-safety / ordering invariant:** all writes to the transcript channel happen from a single logical
  aggregation path so envelope ORDER is deterministic (UserMessage → the turn's message/thought/tool envelopes in
  arrival order → next turn's UserMessage …). If the worker (turn-end flush) and the update-consumer run on
  different tasks, guard the shared buffer + channel writes so a kind-transition flush and a turn-end flush cannot
  interleave a half-built run. Prefer a single loop that reads BOTH the update stream AND a turn-boundary signal, or
  a lock around buffer-mutate+flush — implementer's choice, but state which and why in the report.

### 3. Expose the transcript to the orchestrator (§2.4 — the handoff shape)
- Define `IAcpTranscriptSource` (in `Capacitor.Cli.Daemon`) with: `string AcpSessionId`, `string Cwd`,
  `string? ResolvedModel`, `ChannelReader<AcpEventEnvelope> Envelopes`. `AcpHostedAgentRuntime` implements/ exposes
  it: `AcpSessionId` = the session/new sessionId; `Cwd` = the StartAsync cwd; `ResolvedModel` = the model actually
  selected in gap 1's `TrySelectModelAsync` (capture it there — the resolved id, or null if selection was skipped);
  `Envelopes` = the new transcript `ChannelReader<AcpEventEnvelope>`. (Wiring this onto `HostedRuntimeStart` +
  consuming it is task 4 — just EXPOSE it here so task 4 can pick it up.)

### Seq / timestamp (do NOT own seq here)
Envelopes are written to the channel with `Seq` left at its default (**0 placeholder — task 3's forwarder assigns
the real monotonic Seq on dequeue**) and a REAL `TimestampIso` (use the daemon's `TimeProvider` if the runtime has
one, else `DateTimeOffset.UtcNow.ToString("O")` — prefer an injectable clock for test determinism). Channel FIFO
order is the contract task 3 relies on; do not reorder.

## TDD (test/Capacitor.Cli.Tests.Unit/, FakeAcpAgent)
Genuine red→green. Cover:
- **Aggregation:** N `agent_message_chunk`s in one turn → exactly ONE `AssistantText` envelope whose text is the
  concatenation; same for thought chunks; a message run followed by a thought chunk → two envelopes (kind-transition
  flush); a `tool_call` mid-run → the open message run flushes, then a `ToolCall` envelope, in order.
- **Serialization (the round-2 finding):** enqueue two inputs whose turns overlap on the fake agent → each turn's
  chunks aggregate into that turn's own envelope; no cross-contamination; ordering is UserMessage(1) … flush(1) …
  UserMessage(2) … flush(2).
- **UserMessage:** one `UserMessage` envelope per turn, emitted before that turn's assistant envelopes.
- **Turn-end + session-end flush:** the final run of a turn flushes on `stopReason`; an open run at session-stop
  flushes (or is intentionally dropped — state which) without hanging.
- **Cancellation:** a turn whose `stopReason` never arrives → dispose/cancel returns promptly, the transcript
  channel completes, no deadlock/hang.
- **Non-blocking `StartAsync`:** still returns once the session is established without awaiting the first turn.

## Definition of done
- `dotnet build` clean (daemon+core); AOT gate empty
  (`dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`).
- `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` — report pass/total (note the
  known pre-existing flaky `HttpClientExtensionsRetryTests` retry-timeout test if it appears).
- Red→green confirmed for the aggregation + serialization core.

## Report contract
Write detail to `.sdd/task2-aggregation-turn-worker-report.md`. Return only STATUS, commit sha, one-line test
summary, AOT result, the thread-safety mechanism you chose + why, and concerns. Commit as
`AI-688 Option B task 2: chunk aggregation + serialized prompt-turn worker + IAcpTranscriptSource`.

## HARD RULES
- Read/Edit/Bash yourself only; NO Agent/Task delegation; NO git push.
- Worktree-only. `await` every TUnit assertion.
- Preserve AI-684 Fix E (StartAsync non-blocking) + gap 1 (model selection) behavior. AOT/trim-safe (source-gen JSON,
  no reflection). Do NOT assign real Seq, do NOT add ServerConnection/orchestrator/SessionStarted code (tasks 3–4).
