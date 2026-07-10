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
  - **A dedup key**, one of: **(a)** a **stable per-message id** carried identically on each envelope
    across original and replay (preferred — enables per-envelope dedup that preserves incremental
    streaming; C0 must also confirm how the **locally-synthesized `UserMessage`** — which we create,
    not the agent — appears in the replay so we can match it, e.g. by the prompt text we sent, r3-review
    B1); or **(b)** stable **complete-turn count + order** enabling occurrence-safe **turn-ordinal**
    dedup, which REQUIRES the whole-turn staging model in C3 (and its streaming tradeoff). A pure
    content fingerprint is rejected (r2-review B1 — no occurrence identity).
  - **Boundary-turn completeness** (r3-review B4): the turn interrupted mid-prompt must reappear
    **complete** in the replay (not truncated), else C4's discard-and-replay is unsound.
  - **An end-of-replay barrier** (r3-review B4): whether the `session/load` *response* reliably means
    all replay notifications have already arrived. C6 may only open the gate once replay is known
    complete; if the response is not a barrier, C0 must find the actual signal (e.g. a terminal
    notification) or the gate cannot safely reopen.
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
  - **(a) per-envelope stable-id dedup (preferred, streaming-preserving).** Every emitted envelope
    carries C0's stable id; the runtime keeps the set of already-forwarded ids; each replayed envelope
    is emitted only if its id is new. Incremental streaming is preserved. Requires the C0
    synthesized-`UserMessage` matching story.
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
     reconnect. `BeginReconnect` must be idempotent (read- and write-side may both fire).
  4. On give-up, the gate opens into the terminal path (worker completes, `_updates` completes,
     orchestrator finalizes) — same disposition as an intentional stop.
  This gate is the coupling point between C1/C2/C4; tests must cover "prompt queued during reconnect runs
  only after the replay barrier", "boundary partial discarded not flushed on the crash fault", and
  "write-side send failure triggers reconnect".

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
  reconnect runs **only after** resume (C6 gate); give-up after N attempts finalizes cleanly. Live
  `KCAP_ACP_LIVE` end-to-end resume.

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
