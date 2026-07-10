# AI-689 — ACP production hardening & docs: design

Parent epic **AI-682**. Follows merged AI-684 (foundation), AI-686 (permission/elicitation bridge),
AI-687 (fs/terminal capability), AI-688 (Cursor prototype + transcript forwarding). Entirely in
**kcap-cli** (the daemon). Branch `ai-689-acp-hardening-docs` off `origin/main` (`f724432`).

**Scope = Full** (owner decision): the lean set (diagnostics + protocol/capability negotiation +
observability logging + leak-vector fixes + docs + security posture) **plus** a metrics stack, an
opt-in raw-frame debug stream, and **ACP process reconnect/resume**. See
[gap analysis](ai-689-gap-analysis.md) for what AI-688 already delivered (most of Item-1 hardening) —
this design does not redo that.

## 1. Evidence (two probes)

### 1.1 fs/terminal (AI-687, recap)
Cursor runs file/shell ops itself as a local child process; requests no client `fs/*`/`terminal/*`.
We advertise none and decline unhandled agent→client methods `-32601`.

### 1.2 Reconnect + `initialize` response (AI-689, new — probe archived in scratchpad)
The `initialize` response — **currently discarded** at `AcpHostedAgentRuntime.cs:271` — contains:
```jsonc
{ "protocolVersion": 1,
  "agentCapabilities": {
    "loadSession": true,                       // ← protocol-native session resume IS supported
    "promptCapabilities": { "image": true, "audio": false, "embeddedContext": false },
    "sessionCapabilities": { "list": {} },
    "mcpCapabilities": { "http": true, "sse": true } },
  "authMethods": [ { "id": "cursor_login", "name": "Cursor Login",
                     "description": "…Run 'agent login' first if not logged in." } ] }
```
**`session/load` works across a process restart.** After hard-killing the first `cursor-agent`, a
fresh process resumed the SAME `sessionId` via `session/load {sessionId, cwd, mcpServers}`:
- it returned a normal result (modes/models/configOptions),
- it **replayed prior history** as `session/update` notifications (`user_message_chunk`,
  `agent_thought_chunk`, `agent_message_chunk`),
- and a subsequent `session/prompt` on the loaded session completed (`stopReason: end_turn`).

**Design consequences:** reconnect is protocol-native (relaunch → `initialize` → `session/load`, same
sessionId; no `--resume` CLI flag). The replay means the forwarder must not re-emit already-sent
envelopes (§4.3). The `initialize` response gives us protocolVersion, `loadSession`, and `authMethods`
for negotiation + diagnostics (§2, §4).

## 2. Workstream A — diagnostics + protocol/capability negotiation (PR 1)

Stop discarding the `initialize` result; parse it into a typed `InitializeResult`
(source-gen JSON, registered in `CapacitorJsonContext`) and:

- **A1 protocol-version check.** If `result.protocolVersion != 1`, fail the handshake with a clear,
  actionable `InvalidOperationException` ("cursor-agent negotiated ACP protocol vN; this build
  supports v1 — update kcap or cursor-agent"). Do not silently proceed.
- **A2 capability capture.** Record `agentCapabilities` (esp. `loadSession`) on the runtime; reconnect
  (§4) only attempts `session/load` when `loadSession == true`. Log the negotiated capabilities once
  at Info (§3).
- **A3 missing-binary diagnostic.** When `CliResolver` can't find `cursor-agent`, the vendor is
  omitted today with no explanation. Add a one-time operator-facing Warning on daemon start naming
  the vendor and pointing at `KCAP_CURSOR_PATH` + `agent` install. (Keep the omission behavior — just
  make it visible.)
- **A4 auth/subscription diagnostic.** When `session/new` (or a prompt) fails in the way an
  unauthenticated / non-Team account fails, surface an actionable *annotation* ("possible
  auth/subscription issue — try `cursor-agent login` / verify Team tier") that **preserves the
  original RPC error code + data verbatim** and never replaces it. Per r1: `authMethods` presence is
  **weak evidence** (it's advertised during normal init too), so it is a hint, not a trigger — do not
  drive the diagnosis off `authMethods` alone or off broad substring matching until the shape is
  confirmed. Also per r1: prompt failures are currently swallowed in the background turn worker
  (`AcpHostedAgentRuntime.cs` turn worker), so A4 must hook the actual failure site (handshake /
  `session/new` / the surfaced turn error), not assume the error reaches the launch path. A short
  logged-out live probe confirms the real failure shape before any string matching is added.

## 3. Workstream B — observability (PR 2)

- **B1 Info-level lifecycle logging** via source-gen `[LoggerMessage]` (the AOT-safe pattern already
  used in `ServerConnection`/`AgentOrchestrator`; the `Acp/` files currently use raw `ILogger`).
  Events, all payload-free (ids/metadata only): launch requested, handshake ok (protocolVersion +
  loadSession + model), session started, session loaded/resumed, blocking request issued+resolved
  (kind + decision, never content), reconnect attempt/success/give-up, session ended.
- **B2 metrics stack (OPTIONAL — per r1).** A single `Meter` (`"Capacitor.Cli.Daemon.Acp"`) with
  counters: `acp.launches`, `acp.sessions_started`, `acp.sessions_loaded`, `acp.blocking_requests`
  (tag: kind), `acp.reconnects` (tag: outcome), `acp.failures` (tag: stage). r1 confirmed
  `System.Diagnostics.Metrics` is not an AOT hazard, but flagged that the acceptance criterion
  ("logs/metrics *enough to diagnose*") is **already met by B1's Info logging** — there is no existing
  `Meter`/exporter in the repo. So B2 is **optional/nice-to-have, not required**: implement it LAST
  within PR2, and it is the first thing to drop if PR2 gets large. No exporter is wired (observable via
  `dotnet-counters`).
- **B3 close the two Debug payload-leak vectors.** The Unknown-kind full-raw-update dump
  (`AcpEventTranslator.cs:106-108`) and cursor-agent stderr (`AcpChildProcess.cs:54`) currently log
  content unconditionally at Debug. Gate both behind the B4 opt-in flag; when the flag is off, log
  only shape/metadata (kind, length), never content.
- **B4 opt-in raw-frame debug stream.** Env var `KCAP_ACP_DEBUG_FRAMES=1` (default off, absent).
  When set, log full inbound/outbound ACP frames + the B3 content at Debug under a dedicated
  category, prefixed with a "may contain sensitive payloads" warning emitted once. Absent-by-default
  satisfies the privacy acceptance criterion; the opt-in is bounded (frame length cap) and never
  writes to the transcript/server.

## 4. Workstream C — ACP process reconnect/resume (PR 3)

The heart of Full scope, and — per spec-review r1 (3 BLOCKING) — the part that needs a real design,
not a bullet list. Today a mid-session `cursor-agent` death ends the read loop, faults pending
requests, and drives finalization. The three findings below are load-bearing; the revised design
answers each.

> **r1 findings addressed here:** (B1) seq is a *forwarder* concept (runtime envelopes carry
> placeholder `Seq=0`; the forwarder assigns real seqs on dequeue, `IAcpTranscriptSource.cs:34`,
> `AcpTranscriptForwarder.cs:232`) — so a seq/index watermark cannot be the dedup key. (B2) a
> `session/load` replay is NOT proven to preserve envelope boundaries/order, and a partial death-
> boundary flush can dup-prefix or drop-suffix the boundary turn. (B3) `_connection`/`_process` are
> `readonly`, `_updates` completes on read-loop end, and `ReadOutputAsync` returning drives orchestrator
> finalization (`AgentOrchestrator.cs:743`), disposal/final-drain (`:1336`), `AcpCts` cancel, and
> binding unregister (`:827,:887`) — the runtime is built for a single process lifetime and cannot
> swap its process as originally described.

- **C0 — replay-identity probe (FIRST step of PR3, and a HARD GATE).** The reconnect design hinges on
  unknowns the AI-689 probe did not resolve. Probe: establish a multi-turn session (with a deliberately
  **repeated** identical prompt/answer, to test occurrence identity), then **kill the process DURING an
  active prompt turn** (not between turns — r3-review B4), `session/load`, and capture the replayed
  `session/update` stream. C0 must establish ALL of:
  - **A dedup key**, one of: **(a)** a **per-emitted-envelope, occurrence-safe key** (r4-review 2) —
    NOT a per-*message* id (one ACP message can fan out to several emitted envelopes — a tool_call +
    its result, coalesced text runs — so a per-message set would drop valid later envelopes). The key
    must be a composite that is stable across replay and distinct per emitted envelope, e.g. `agent
    message id + envelope kind + (tool-call id | run index)`. C0 must confirm such a composite is
    reconstructable identically on replay, AND — separately — a **concrete occurrence-safe key for the
    interrupted USER turn** (r5-review 1), named with its lifecycle, not hand-waved. The required
    primary is a **client-generated prompt-occurrence-id**: the runtime mints a per-session monotonic
    (or GUID) id, **persists it before the first byte of `session/prompt` is written**, and attaches it
    to the outgoing prompt — and **C0 must confirm ACP `session/load` echoes that client id identically
    on the replayed user turn**. (Content-matching by prompt text is rejected — duplicate-prompt-unsafe.
    A stable ACP-provided user-turn id also qualifies ONLY if it is durably associated *before* any
    crash window; an id first learned from the live echo is useless for a prompt whose echo never
    arrived.) **If C0 confirms neither an echoed client id nor a pre-association ACP turn id, the
    interrupted-turn PRESENT/ABSENT decision is undecidable by key → C8 falls back to documented
    at-most-once semantics (see C8), and C3(a) is DISALLOWED** → C3(b) or re-scope. Or **(b)** stable
    **complete-turn count + order** enabling
    occurrence-safe **turn-ordinal** dedup, which REQUIRES the whole-turn staging model in C3 (and its
    streaming tradeoff). A pure content fingerprint is rejected (r2-review B1 — no occurrence identity).
  - **Boundary-turn shape (drives C8's three-way routing — r3-review B4, r7-review 1):** kill mid-prompt
    and record which of three shapes the interrupted turn takes in the replay — **absent**, **user-only
    / no terminal assistant state (incomplete)**, or **complete (terminal assistant state / `StopReason`
    present)**. This mapping is exactly what C8 routes on (ABSENT→requeue, PRESENT-COMPLETE→no requeue,
    PRESENT-INCOMPLETE→surface-as-interrupted), so it must be observed, not assumed. Also confirm a
    *fully* completed turn reappears complete (not truncated), else C3/C4 dedup is unsound.
  - **Acceptance-boundary invariant for ABSENT (r8-review 1):** C8's `ABSENT ⇒ safe to requeue` rests on
    a claim that must be PROVEN, not inferred from absence: *once ACP has accepted/started a prompt, its
    occurrence becomes replay-visible.* So C0 must also kill **after crossing the acceptance/execution
    boundary** — after the first assistant/tool `session/update` evidence for that occurrence — and
    verify the occurrence then appears in the replay as PRESENT-INCOMPLETE/COMPLETE, **never ABSENT**.
    If a "started-but-not-yet-replay-visible" window exists (an accepted prompt can still be ABSENT),
    then ABSENT is ambiguous and C8's auto-requeue would duplicate already-started, possibly
    side-effecting work → in that case reconnect re-scopes, or ABSENT-after-possible-acceptance routes
    to the interrupted/at-most-once path rather than auto-requeue.
  - **A protocol-backed closed-world end-of-replay barrier** (r3-review B4, r5-review 3, r6-review 1):
    C0 must establish a signal ACP itself **guarantees** is terminal for replay — the `session/load`
    response contract, an explicit EOF, or a terminal notification — after which **no further replay
    `session/update` can arrive**. A **bounded quiescence / quiet-period heuristic is NOT acceptable**
    to drive C8's PRESENT/ABSENT (r6-review 1): a late replayed user turn arriving after the quiet
    period would fire *after* the same occurrence id was already requeued, recreating the duplicate.
    C8's check and the C6 gate reopen run ONLY after this protocol-backed barrier. **If ACP provides no
    protocol-backed barrier, PRESENT/ABSENT is undecidable → C8 uses the at-most-once fallback and/or
    reconnect re-scopes** — never a heuristic barrier. *(Encouraging: ACP v1 documents `session/load` as
    responding only after all conversation entries have streamed — i.e. its response is a protocol-
    backed closed-world barrier — so this is likely satisfiable; C0 confirms it against the real
    `cursor-agent`.)*
  **If C0 fails any of these, reconnect is re-scoped to a follow-up issue and NOT shipped** — A/B/D
  stand alone. No reconnect code lands until C0 answers all three.

- **C1 — reconnect owner + crash-vs-stop.** Introduce an explicit reconnect owner inside the runtime
  (not the orchestrator). Add an `_intentionalStop` flag set by the dispose/stop paths; the read-loop
  ending or `ReadOutputAsync` returning while `!_intentionalStop` (and `loadSession` capable) means
  *crash → reconnect*, NOT finalize. This requires restructuring the runtime so it does not signal
  terminal to the orchestrator on an unexpected exit: `_updates` must **not** complete, `AcpCts` must
  **not** cancel, and the orchestrator's finalize trigger (`ReadOutputAsync` return) must be gated on
  the runtime's reconnect *outcome* — the runtime reports "done" only after reconnect attempts are
  exhausted or an intentional stop. `_connection`/`_process` become swappable (guarded), no longer
  `readonly`.

- **C2 — resume.** If `loadSession` was advertised (A2): relaunch `cursor-agent acp` (same cwd/env via
  the factory), `initialize` (re-run A1 protocol check + A2 capture), then
  `session/load {sessionId, cwd, mcpServers}` with the SAME sessionId; swap the new process/connection
  into the runtime and re-arm the read loop, keeping the outer channels + forwarder binding alive.
  Bounded attempts (default 3) with backoff; on give-up, finalize cleanly (today's behavior) with a
  Warning + `acp.reconnects{outcome=failed}`.

- **C3 — occurrence-safe dedup, at the right granularity (addresses r3-review B1).** A "turn" is NOT
  one envelope: the runtime emits a synthesized `UserMessage`, then `ToolCall`/`ToolResult` and
  completed thought/text runs *incrementally throughout the turn*, and the forwarder dequeues+sends
  each as it is emitted (`AcpHostedAgentRuntime.cs:421-430,647-666`; `AcpEventTranslator.cs:137-146`).
  So on a mid-turn death, part of the boundary turn is **already forwarded** — C4 discarding only the
  open run cannot retract those, and a turn-ordinal `>N` scheme would re-forward them on replay. Two
  admissible designs, chosen by C0:
  - **(a) per-envelope composite-key dedup (preferred, streaming-preserving).** Every emitted envelope
    carries C0's **per-envelope composite key** (`message id + kind + tool-call-id|run-index`, r4-review
    2 — never a bare per-message id, which would drop the 2nd+ envelope of one message); the runtime
    keeps the set of already-forwarded keys; each replayed envelope is emitted only if its key is new.
    Incremental streaming is preserved. The synthesized `UserMessage` uses the C0-validated synthetic-
    user key (if C0 couldn't establish one, C3(a) is disallowed — see C0).
  - **(b) whole-turn atomic staging (only if no stable id).** The runtime **buffers all envelopes of a
    turn** and publishes them to the forwarder **atomically only when `session/prompt` completes**;
    turn-ordinal `>N` dedup is then sound because turns are all-or-nothing at the forwarder boundary.
    On a mid-turn crash the entire staged (incomplete) turn is discarded (C4). **Tradeoff:** this
    removes within-turn incremental streaming to the server (the turn appears at completion), a
    regression from AI-688's live tail — so (b) is a fallback, and if its tradeoff is unacceptable,
    reconnect re-scopes rather than ship it.
  Both are occurrence-safe (identical content at different ids/ordinals stays distinct — r2-review B1).
  The forwarder seq/ack remains transport backstop, NOT relied on for replay correctness. AI-688
  single-flight still holds. (If C0 found neither key, the workstream re-scopes — see C0.)

- **C4 — boundary turn: discard, don't flush (granularity per C3, must override the `finally` flush).**
  On a mid-turn crash the boundary turn must be dropped from our side so the replay can re-emit it
  exactly once: under C3(a) discard the open aggregation run (the already-forwarded boundary envelopes
  are dropped on replay by id-dedup); under C3(b) discard the entire **staged** boundary turn (nothing
  of it was forwarded). **Critical (r2-review B2):** this is NOT automatic — when the read loop ends it
  faults pending requests (`AcpConnection.cs:163`) and `ProcessTurnAsync`'s `finally { FlushOpenRun(); }`
  (`AcpHostedAgentRuntime.cs:397`) would flush/publish exactly what we intend to discard. The reconnect
  state (C6) must be set **before** that `finally` runs, and the flush/publish path must check it and
  **discard instead** while reconnecting. Not wiring this makes C4 a no-op. (If reconnect ultimately
  fails, the interrupted turn is simply absent — acceptable, better than an uncorrectable partial.)

- **C5 — interaction vs SignalR re-bind.** C-reconnect (agent process) is orthogonal to the existing
  `ReBindAcpSessionsAsync` (SignalR server binding). A pure process resume keeps the same
  agentId/sessionId, so the existing SignalR binding stays valid — no re-bind needed. Documented so the
  two mechanisms aren't conflated.

- **C6 — reconnect gate + a guaranteed happens-before (addresses r2-review B2, r3-review B2).** A
  single-process assumption is baked into the turn worker: `ProcessTurnAsync` catches a prompt fault,
  runs its `finally`, and keeps draining `_pendingTurns` (`AcpHostedAgentRuntime.cs:397,:421`);
  `SendPromptAsync` uses the swappable `_connection` (`:493`). Reconnect needs an explicit gate:
  1. A `Reconnecting` state; the turn worker **awaits the gate before dequeuing/sending each turn**, and
     `SendUserInputAsync`/key/resize await it too — no queued prompt runs against a dead or half-swapped
     connection; the process/connection swap happens only while the gate is closed; the gate reopens
     **only after C0's end-of-replay barrier** (not merely on `session/load` returning).
  2. **The happens-before is NOT free (r3-review B2).** `AcpConnection.RunAsync` faults pending requests
     in its OWN `finally` (`AcpConnection.cs:147-165`) before the outer runtime observes `RunAsync`
     ended, so the prompt continuation (→ `ProcessTurnAsync.finally` flush) and the runtime continuation
     race — the flush can win. The fix is an explicit **pre-fault hook**: `AcpConnection` invokes a
     `BeginReconnect` callback **before** it faults pending requests, and that callback atomically sets
     `Reconnecting` AND starts the reconnect owner. Only then does it fault pending requests. This makes
     "`Reconnecting` set before any `finally` runs" a guarantee, not a hope.
  3. **Write-side faults too (r3-review B2):** a `SendPromptAsync`/write failure must funnel through the
     same `BeginReconnect` — otherwise a send failure closes nothing and no read-loop exit ever starts
     reconnect. **`BeginReconnect` must be synchronous, non-blocking, idempotent, and a no-op once
     intentional stop is marked** (r4-review 3): it only flips `Reconnecting` + schedules the reconnect
     owner and returns immediately; ALL blocking work (relaunch, handshake, `session/load`, disposal)
     runs in the owner, never in the callback and never under the reconnect lock (C7). This is what lets
     `AcpConnection` call it inline on the pre-fault path without risking a block.
  4. On give-up, the gate opens into the terminal path (worker completes, `_updates` completes,
     orchestrator finalizes) — same disposition as an intentional stop.
  5. **Reopen ordering on successful resume (r6-review 2) — strict sequence, gate stays CLOSED
     throughout:** (a) drain replay to C0's protocol-backed barrier; (b) C9 stop/finalized re-check;
     (c) C8 PRESENT/ABSENT decision; (d) if ABSENT, durably enqueue the requeue (same occurrence id) at
     the **head** of the post-replay queue, **before any live update** can be delivered; (e) C9 re-check
     again; (f) **only then reopen the gate** for live delivery. This guarantees a requeued user turn is
     never trailed by its own assistant/tool updates and no live update overtakes the barrier.
  This gate is the coupling point between C1/C2/C4/C8/C9; tests must cover "prompt queued during
  reconnect runs only after the replay barrier", "boundary partial discarded not flushed on the crash
  fault", "write-side send failure triggers reconnect", and "on ABSENT, the requeued user turn is
  delivered before any post-resume live update".

- **C7 — intentional stop serialized against an in-progress relaunch (addresses r3-review B3).** Stop
  today calls `RequestGracefulStopAsync`/`WaitForExitAsync`/`TerminateAsync` against the *current*
  connection/process (`AcpHostedAgentRuntime.cs:509-523`; `AgentOrchestrator.cs:937-963,1569-1576`). A
  stop arriving mid-reconnect would target the dead child while the reconnect owner then installs
  another — delaying finalization or leaking that child. Setting `_intentionalStop` (C1) is not enough;
  stop must, under one lock governing reconnect: (i) **cancel** the reconnect owner's backoff/handshake
  (a dedicated reconnect `CancellationTokenSource` stop cancels), (ii) **prevent any further launch or
  swap** (checked after the cancellation point, before installing a candidate), (iii) **dispose any
  in-flight candidate child** already spawned by the current attempt, and (iv) **release the parked
  terminal signal** so finalization proceeds. Reconnect and stop must not both mutate
  `_connection`/`_process` concurrently.

  **Lock-scope acceptance criteria (r4-review 3 — deadlock avoidance).** The reconnect lock guards ONLY
  fast, non-blocking state: the `Reconnecting`/`_intentionalStop` flags, cancelling the reconnect CTS,
  the swap-prevention check, and transferring ownership of a candidate child. It is NEVER held across
  connection I/O, a request await, a process wait (`WaitForExitAsync`), or a potentially-blocking
  disposal (`TerminateAsync`) — those run OUTSIDE the lock. Otherwise the r4-review-3 deadlock occurs:
  stop holds the lock awaiting graceful-stop/exit while `AcpConnection`'s pre-fault `BeginReconnect`
  blocks on the same lock before it can fault pending requests, so the request stop is waiting on never
  faults. Because `BeginReconnect` is synchronous/non-blocking and a no-op after stop (C6.3) and all
  blocking teardown runs outside the lock, the pre-fault hook and an in-progress stop cannot block each
  other.

- **C8 — prompt commit: requeue-vs-replay for the interrupted turn (addresses r4-review 1, r5-review 1).**
  ACP has no per-prompt ack, so a crash during an active `session/prompt` leaves the prompt's fate
  unknown from the wire. **Primary rule (requires the C0 client-generated prompt-occurrence-id):** after
  `session/load` + the C0 **closed-world** end-of-replay barrier, decide **three ways** (r7-review 1 —
  "present" ≠ "complete", because ACP `session/load` does NOT auto-resume an interrupted turn; a new
  `session/prompt` starts a *new* turn):
  - **ABSENT** (occurrence id not in the replay): the agent never got it → **requeue exactly once**,
    carrying the *same* occurrence id (so a crash during the requeue is itself decidable). **This is
    safe ONLY under the C0-proven invariant that an accepted/started prompt always becomes
    replay-visible** (r8-review 1) — i.e. ABSENT genuinely means "never accepted." If C0 cannot prove
    that (a started-but-not-replay-visible window exists), an ABSENT that could follow acceptance is
    routed to the PRESENT-INCOMPLETE surface-as-interrupted path instead, never auto-requeued (blind
    requeue would duplicate already-started, possibly side-effecting work).
  - **PRESENT-COMPLETE** (occurrence id present AND its turn reached a terminal assistant state /
    `StopReason` in the replay): fully handled → **do NOT requeue** (replay carries it; C3 forwards any
    new part).
  - **PRESENT-INCOMPLETE** (occurrence id present but the turn has **no terminal assistant state** in
    the replay): the prompt is *stranded* — `session/load` will not finish it, and blind-requeuing the
    same content would duplicate the user message in the agent's context. So: **do NOT auto-requeue and
    do NOT silently drop** — surface it to the user as an interrupted turn to re-send (the at-most-once
    floor for the incomplete case), logged with the occurrence id. (If the product needs automatic
    continuation of an interrupted turn, that requires an ACP resume primitive ACP v1 does not provide →
    re-scope; it must not be faked by re-prompting.)
  Keys on the pre-write occurrence id (not prompt text), and runs strictly after the closed-world
  barrier — so it is correct even for **repeated identical prompts** and has no mid-replay window.
  "Terminal assistant state" detection uses the same replayed signal ACP represents turn completion with
  (the `session/prompt` `StopReason` / a terminal assistant turn); **C0 must characterize which of the
  three shapes a mid-prompt kill actually produces in the replay** so the routing is grounded, not
  assumed.
- **C8 fallback (when C0 yields no usable key — r5-review 1).** If C0 confirms neither an echoed client
  id nor a pre-association ACP turn id, the interrupted turn is **undecidable**, so C8 adopts documented
  **at-most-once** semantics: **do NOT requeue** an interrupted prompt of unknown fate (never silently
  re-run it — a duplicate could re-execute tools with side effects), log it, and surface to the user
  that the last prompt may not have been delivered. Under the C3(b) staging path the same rule applies,
  keyed on whether the staged user turn appears in the replayed complete-turn sequence. This fallback is
  a correctness floor, not the goal; if at-most-once is unacceptable for the product, reconnect
  re-scopes until ACP gives a round-tripped prompt id.

- **C9 — reconnect-owner unwind contract vs stop/finalize (addresses r5-review 2).** C6.3's "no-op
  after stop" only guards *new* `BeginReconnect` calls; it does NOT cover an owner already scheduled and
  mid-flight. The owner runs all its blocking work under the reconnect `CancellationToken` (C7) and
  **re-checks stop/finalized state at every checkpoint**: before relaunch, after each await, before
  `session/load`, before the connection/process swap, before reopening the gate, and before a C8
  requeue. **Stop/finalize always wins:** on observing it (token cancelled or `_updates` completed), the
  owner unwinds in a `finally` — dispose any candidate process/connection it created, resolve
  `Reconnecting` and drive the gate to the terminal path (never leave it stuck closed), and perform **no
  swap, no requeue, and no envelope emission** after intentional stop or `_updates` completion. This is
  the explicit contract binding the reconnect owner to AI-688's `AcpCts`/`_updates`/orchestrator-finalize
  teardown so a late owner cannot install a process after stop, emit into a completed `_updates`, requeue
  after a user stop, or leak a child. Acceptance cases: stop while the owner is queued; stop during
  relaunch / `initialize` / `session/load`; a graceful-exit/`TerminateAsync` overlapping a pre-fault
  reconnect; and reconnect attempts exhausting *during* teardown — each must finalize once, leak nothing,
  and emit/requeue nothing post-stop.

**Scope note:** C1's runtime restructure (swappable process/connection, deferred finalize, crash-vs-stop
gating) is the largest single change in AI-689 and touches AI-688's teardown ordering directly. It gets
its own PR (PR3), its own C0 probe, and a deterministic "died mid-transcript → replay → dedup" test
suite. If C0 reveals `session/load` replay is not reliably matchable, reconnect is re-scoped to a
follow-up issue rather than shipped unsound (flagged for owner decision at that point).

## 5. Workstream D — docs + security posture (PR 4, mergeable first)

- **D1 operator docs** (`README.md` + a focused `docs/` page) — **current reality only** (per §6 /
  r2-review NB6; future knobs ship with their PR, NOT here): the hosted-Cursor/ACP path (explicitly
  distinct from the Cursor *recording-hooks* path, a known confusion point); `cursor-agent acp` +
  minimum version + **Team-tier subscription** + `cursor-agent login`; the env vars that exist today
  (`KCAP_CURSOR_PATH`, `KCAP_CURSOR_MODEL`, `KCAP_URL`); current **limitations** (no local-attach raw
  input/special-keys/resize, no terminal-output surfacing); **troubleshooting** (missing binary →
  `KCAP_CURSOR_PATH`; auth → `cursor-agent login`/Team; model-resolution fallback). **Deferred to their
  implementing PRs:** `KCAP_ACP_DEBUG_FRAMES` (with PR2) and reconnect/best-effort-resume limitations
  (with PR3).
- **D2 security posture** (docs): the ACP transcript forwards prompt text, tool args, and tool
  results **verbatim** — the same canonical-transcript posture as every other agent, with no
  ACP-specific redaction; default logging is shape-only; the opt-in raw-frame stream may contain
  sensitive payloads and is off by default; fs/terminal are declined (AI-687).

## 6. PR sequencing

Per r1: docs describe only what's shipped — future knobs (`KCAP_ACP_DEBUG_FRAMES`, reconnect
limitations) are documented **with their implementing PR**, not up front.

1. **PR 4 (docs + security)** first — pure prose, zero code risk, unblocks dogfooding. Documents only
   *current* reality: hosted-Cursor/ACP setup vs recording-hooks, `cursor-agent acp` + version + Team
   tier + `login`, the env vars that exist today (`KCAP_CURSOR_PATH`, `KCAP_CURSOR_MODEL`, `KCAP_URL`),
   current limitations (no local-attach input/keys/resize, no terminal-output), and the verbatim-
   transcript security posture. **Does NOT** pre-document `KCAP_ACP_DEBUG_FRAMES` or reconnect.
2. **PR 1 (diagnostics + negotiation)** — small, self-contained; lands the typed `InitializeResult`
   that PR 3 depends on. Includes the A4 logged-out live probe.
3. **PR 2 (observability)** — Info logging + leak-vector fixes + opt-in raw-frame flag; **B2 metrics
   optional/last**. Ships the `KCAP_ACP_DEBUG_FRAMES` doc **with** this PR.
4. **PR 3 (reconnect)** — largest; depends on PR 1's `InitializeResult`/capability capture. Starts with
   the **C0 replay-identity probe**, then the C1 runtime restructure + C3 content-dedup, then a gated
   `KCAP_ACP_LIVE` end-to-end resume test and a deterministic died-mid-transcript→replay→dedup unit
   suite. Ships the reconnect-limitations doc **with** this PR. Re-scopes to a follow-up if C0 shows
   replay is not reliably matchable.

Each PR: unit tests (fake-agent harness already supports scripted server-requests, initialize
capture, prompt scripts); NativeAOT publish gate warning-free; kcap-cli conventions (no Linear ids in
comments; concise; `JsonElementExtensions`; README sync for new env vars).

## 7. Test plan (highlights)

- **A1:** initialize response with `protocolVersion:2` → handshake throws the clear mismatch error;
  `:1` → proceeds. **A2:** `loadSession` captured + gates reconnect. **A3:** missing binary → the
  operator Warning fires once. **A4:** simulated auth-style RPC error → annotated actionable message,
  original error preserved.
- **B1:** lifecycle events logged at Info with ids only (assert no payload). **B2:** counters
  increment on launch/session/blocking/reconnect/failure. **B3/B4:** flag off → content-free logs;
  flag on → full frames under the dedicated category.
- **C:** fake agent that "dies" mid-session → runtime relaunches + `session/load`s the same sessionId;
  replayed complete turns at **ordinal ≤ N (or matching an already-emitted stable id)** are dropped
  while ordinal > N forwards (assert no duplicate envelopes reach the forwarder, AND that a
  legitimately-repeated identical turn is NOT dropped — the occurrence-safety case from r2-review B1);
  the boundary partial is **discarded, not flushed** on the crash fault (C4/C6); a prompt queued during
  reconnect runs **only after** the replay barrier (C6 gate); the interrupted prompt is **requeued iff
  its user turn is absent from the replay** (C8 — assert both branches: present→not requeued,
  the C8 three-way keyed on the occurrence id at the three crash points: **absent** (before write) →
  head-requeue once with the same id; **present-complete** (terminal assistant state in replay) → no
  requeue; **present-incomplete** (user turn but no terminal state) → surfaced as interrupted, NOT
  auto-requeued and NOT dropped);
  **stop/finalize during an in-progress reconnect** (owner queued, or mid relaunch/initialize/load)
  finalizes exactly once, leaks no candidate child, and emits/requeues nothing post-stop (C7 lock-scope
  + C9 unwind); give-up after N attempts finalizes cleanly. Live `KCAP_ACP_LIVE` end-to-end resume.

## 8. Risks / open questions

- **R1 (A4):** unauthenticated `cursor-agent` failure shape unverified — confirm via a logged-out
  live probe during PR 1; A4 annotates, never masks.
- **R2 (C3/C4 — reworked across r1→r3):** replay-dedup uses neither a seq/index watermark (r1 — seq is
  a forwarder concept) nor a content fingerprint (r2 — no occurrence identity). It is either
  **per-envelope stable-id dedup** (C3a, preferred, streaming-preserving) or **whole-turn atomic
  staging + ordinal** (C3b, fallback, with a within-turn-streaming regression), chosen by the C0 hard
  gate. Residual risk is entirely front-loaded into C0: if it finds neither a stable id (incl. the
  synthesized-`UserMessage` match) nor stable turn count+order, AND the C3b tradeoff is unacceptable,
  reconnect re-scopes rather than shipping unsound. A deterministic died-**mid-prompt**→replay→dedup
  test (occurrence-safety + gate-ordering + boundary-discard cases) locks the chosen path.
- **R2b (C1 restructure):** making the runtime swap its process + defer finalize touches AI-688's
  teardown ordering (`ReadOutputAsync`→finalize, `AcpCts`, `_updates` completion, binding unregister).
  Highest-blast-radius change; isolated to PR3 with focused tests, and re-scopable if C0 fails.
- **R3 (B2):** metrics have no exporter yet — acceptable per acceptance ("enough to diagnose",
  observable via `dotnet-counters`); a real exporter is out of scope.
- **R4:** reconnect could mask a genuinely-crashing agent by relaunching repeatedly — the bounded
  attempt cap + backoff + `acp.reconnects{failed}` metric + give-up-finalize prevent an infinite loop.
