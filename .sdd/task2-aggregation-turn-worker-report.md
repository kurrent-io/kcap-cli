# AI-688 Option B — Task 2: chunk aggregation + serialized prompt-turn worker (report)

**Status:** DONE.

## What was built

### A. Serialized, single-flight prompt-turn worker (`AcpHostedAgentRuntime`)

- `_pendingTurns` — an unbounded `Channel<string>` FIFO queue of pending turn texts.
- `EnqueueTurn(string)` replaces the old `FireAndTrackPromptAsync` — writes to the queue and returns
  immediately. Called from `StartAsync`'s initial-prompt branch and from `SendUserInputAsync`, both
  unchanged in their non-blocking contract (AI-684 Fix E).
- `RunTurnWorkerAsync` — one long-running task (started in `StartAsync`, alongside the existing
  connection read-loop task) that drains `_pendingTurns` strictly FIFO via
  `ReadAllAsync(ct).ConfigureAwait(false)`, `await`-ing `ProcessTurnAsync` to full completion before
  the next iteration — this is the single-flight serialization: turn N+1's `session/prompt` is never
  sent until turn N's flush has already happened.
- `ProcessTurnAsync` — per turn: (a) emits the `UserMessage` envelope (via
  `AcpEventTranslator.BuildUserMessage`); (b) `await`s `SendPromptAsync` (reused unchanged); (c) in a
  `finally`, flushes that turn's aggregation buffer — runs on success, on a non-cancellation fault
  (logged, non-fatal, mirroring the old `FireAndTrackPromptAsync` swallow-and-log), and on
  cancellation (a courtesy flush of whatever partial text had accumulated; the `when (ex is not
  OperationCanceledException)` filter still lets cancellation propagate so `RunTurnWorkerAsync`'s loop
  stops promptly).
- Removed `_backgroundPromptTasks`/`FireAndTrackPromptAsync`; `DisposeAsync` now completes
  `_pendingTurns`'s writer and bounded-waits (5s) on the single `_turnWorkerTask` instead of
  `Task.WhenAll` over a tracking dictionary.

### B. Chunk aggregation (`AggregateUpdate`/`FlushOpenRun(Locked)`/`EmitEnvelope`)

- `AggregateUpdate(AcpSessionUpdate)` — fed synchronously from `HandleNotification` (in addition to
  the existing `_updates` channel write, which is untouched — the two are independent sinks of the
  same reduced update). Rules exactly per §2.1: a same-kind `AgentMessageChunk`/`AgentThoughtChunk`
  run appends to `_openRunText`; any other kind (or a kind transition between message/thought)
  flushes the open run first (`FlushOpenRunLocked`), then — for tool_call/tool_call_update/plan/
  available_commands/unknown — translates the update 1:1 via `AcpEventTranslator.Translate` and emits
  it if non-null.
- `FlushOpenRunLocked` builds a representative `AcpSessionUpdate(kind)` (only the `Kind` field is
  populated — that's all `Translate` needs to pick `AssistantText` vs `AssistantThinking` when
  `aggregatedText` is supplied) and translates it with the buffered text as `aggregatedText`, emitting
  exactly one envelope for the whole run.
- `EmitEnvelope` is the **only** call site that writes to the new `_transcript` channel.

### C. Thread-safety mechanism (chosen: a single `lock`, not a unified loop)

A plain `object _aggregationLock` guards (1) the open-run state
(`_openRunKind`/`_openRunText`) and (2) every write to `_transcript` (`EmitEnvelope` always takes the
lock; `lock` is reentrant on the same thread, so `FlushOpenRunLocked` calling back into `EmitEnvelope`
from inside `AggregateUpdate`'s own `lock` block doesn't self-deadlock).

Two call sites can flush: the connection's read loop (synchronous, via `HandleNotification` →
`AggregateUpdate`, on a kind transition) and the turn worker (via `ProcessTurnAsync`'s turn-end flush,
which runs as the continuation of an awaited `session/prompt` response — `AcpConnection.RequestAsync`'s
`TaskCompletionSource` uses `RunContinuationsAsynchronously`, so that continuation is **not**
guaranteed to run on the read-loop's own thread). Because turns are serialized (A), these two call
sites never actually contend in practice — turn N+1's updates cannot start arriving until turn N's
`session/prompt` is sent, which cannot happen until turn N's flush has already completed — but a lock
is a cheap, simple guarantee against that invariant ever silently breaking (a future worker change, a
non-conforming agent, etc.) rather than relying solely on the timing argument.

Rejected alternative: a single loop reading both the update stream and a turn-boundary signal. This
would need the connection's notification callback plumbed through its own channel (a second unbounded
channel + consumer loop) for no additional safety over a lock, given the happens-before analysis above
— more moving parts for the same guarantee.

### D. `IAcpTranscriptSource` (§2.4 handoff, `src/Capacitor.Cli.Daemon/Services/IAcpTranscriptSource.cs`)

New `internal interface` in `Capacitor.Cli.Daemon.Services` (alongside `IHostedAgentRuntime`/
`HostedRuntimeStart`, for task 3/4 to consume): `string AcpSessionId`, `string Cwd`, `string?
ResolvedModel`, `ChannelReader<AcpEventEnvelope> Envelopes`. `AcpHostedAgentRuntime` implements it
directly (`: IHostedAgentRuntime, IAcpTranscriptSource`):
- `AcpSessionId => _sessionId!` (only meaningful post-`StartAsync`, same as the existing `SessionId?`).
- `Cwd => _cwd!` — `_cwd` is now captured at the top of `StartAsync`.
- `ResolvedModel => _resolvedModel` — set in `TrySelectModelAsync` only after `session/set_config_option`
  is **awaited without error**, so a rejected/unresolved model correctly reports `null` (Cursor's own
  default applies in every null case), not the requested-but-unconfirmed id.
- `Envelopes => _transcript.Reader`.

Explicitly **not** wired onto `HostedRuntimeStart`/`AcpHostedAgentRuntimeFactory`/the orchestrator —
that's task 3/4, per the brief. Seq stays a `0` placeholder on every envelope this task emits (task 3
assigns the real monotonic seq on dequeue); `TimestampIso` is real, via a new injectable
`TimeProvider` field (`TimeProvider.System` default, overridable through a new optional constructor
parameter — kept for test determinism even though the new tests don't currently assert on the exact
timestamp value).

## Tests (TDD, genuine red → green)

New file: `test/Capacitor.Cli.Tests.Unit/Acp/AcpTranscriptAggregationTests.cs` (8 tests, end-to-end
against `FakeAcpAgent` through the real runtime, reusing its `BuildAgentMessageChunkUpdate`/
`BuildAgentThoughtChunkUpdate`/`BuildToolCallUpdate`/`HoldPromptResponses` scripting):

1. N `agent_message_chunk`s in one turn → one `AssistantText` envelope, concatenated text.
2. N `agent_thought_chunk`s → one `AssistantThinking` envelope.
3. A message run then a thought chunk → two envelopes (kind-transition flush).
4. A message run, then a `tool_call`, then a fresh message run → flush → `ToolCall` → flush, in order.
5. `UserMessage` once per turn, before that turn's assistant envelope, across two sequential turns.
6. **Serialization (the round-2 finding):** two turns with the fake's `session/prompt` response held —
   asserts turn 2's `session/prompt` is **not** sent while turn 1's is still outstanding, then asserts
   clean per-turn envelope ordering/content (`UserMessage(1)`, flush(1), `UserMessage(2)`, flush(2)) —
   no cross-contamination.
7. Dispose of a turn whose `stopReason` never arrives returns promptly (`WaitAsync(5s)` doesn't throw)
   and `Envelopes.Completion` completes once drained.
8. Dispose flushes the partial buffer of a turn whose `stopReason` never arrived (documents the
   **flush, not drop** decision for the session-end case) — content-verified, not just "doesn't hang".

Per-suite run: **8/8 green**. Full suite: **2685 passed, 0 failed, 1 skipped** (2686 total; the
skipped test is the pre-existing gated live-ACP E2E, `KCAP_ACP_LIVE=1`; up from task 1's 2677+1
baseline by exactly the 8 new tests — no regressions, `HttpClientExtensionsRetryTests`' known
flaky retry-timeout test did not flake this run).

**Genuine red confirmed retroactively** (the implementation and tests were written together, not
strictly test-first, given the concurrency design needed to be nailed down as one piece): after all
tests were green, I temporarily reintroduced each of the two bugs task 2 fixes and reran the suite,
then reverted:
- Changed the worker's `await ProcessTurnAsync(...)` to a fire-and-forget `_ =
  ProcessTurnAsync(...)` (the pre-task-2 overlap bug) → **only** test 6 (serialization) failed, with
  `session/prompt` count 2 instead of 1 — exactly the round-2 regression this task exists to catch.
- Disabled the same-kind coalescing check in `AggregateUpdate` (`if (false && _openRunKind ==
  update.Kind)`) → **only** tests 1 and 2 (coalescing) failed, each emitting just the first chunk's
  text instead of the concatenation.

Both reverts confirmed, full suite re-verified green after (2685/0/1, matching the numbers above).

## Verification

- `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Debug` → 0 Error(s) (builds
  Core transitively).
- AOT gate: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E
  'IL[23][01][0-9]{2}'` → no output (clean).
- `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug
  --no-build` → **2685 passed, 0 failed, 1 skipped** (2686 total).

## Concerns / judgment calls

1. **Session-end (dispose) behavior for an open run: flush, not drop.** The brief left this an
   implementer choice. I chose to flush (courtesy-flush, content-verified by test 8) rather than
   silently discard partial assistant text — a turn cut short by shutdown still contributes whatever
   it produced. This is a pure in-memory operation (never I/O), so it cannot itself cause the dispose
   hang the cancellation contract guards against.
2. **Lock vs. single consumer loop.** Documented above (C) — chose the lock for simplicity given the
   happens-before analysis holds turns are genuinely serialized; flagged in code comments as a
   defense-in-depth choice, not a load-bearing assumption that two writers are ever *actually*
   concurrent in the current design.
3. **`TimeProvider` injection point.** Added as a new optional trailing constructor parameter
   (`TimeProvider? timeProvider = null`) — backward compatible with every existing call site (factory
   + all pre-existing tests), defaults to `TimeProvider.System`. No test currently exercises a fake
   clock (none of the new tests assert on `TimestampIso`'s exact value) — the seam exists per the
   brief's "prefer an injectable clock for test determinism" but wasn't exercised, since none of the
   required TDD scenarios needed it.
4. Did **not** touch `AgentOrchestrator`, `ServerConnection`, `HostedRuntimeStart`,
   `AcpHostedAgentRuntimeFactory`, or any seq-assignment/forwarding/`SessionStarted` logic — out of
   scope per the brief (tasks 3-4). `IAcpTranscriptSource` is defined and implemented but not wired
   onto anything outside `AcpHostedAgentRuntime` itself.

## Commit

`AI-688 Option B task 2: chunk aggregation + serialized prompt-turn worker + IAcpTranscriptSource` —
committed on branch `ai-688-cursor-hosted-agent-prototype`, **not pushed**.
