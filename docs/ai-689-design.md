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

- **C0 — replay-identity probe (FIRST step of PR3, before any code).** The reconnect design hinges on
  one unknown the AI-689 probe did not resolve: *what exactly does `session/load` replay, and can a
  replayed turn be matched to an already-forwarded one?* Probe: establish a multi-turn session, kill
  the process, `session/load`, and capture the replayed `session/update` stream — specifically
  whether assistant/user messages carry a **stable message id** across the original vs the replay,
  whether **turn order** is preserved, and whether chunk coalescing differs. The result picks the C3
  dedup key (stable id preferred; content fingerprint fallback). No reconnect code lands until C0 is
  answered.

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

- **C3 — content-identity dedup (replaces the seq/index watermark).** Dedup lives in the **runtime's
  emission layer**, keyed on **content identity**, never on our assigned seq (B1) or a positional
  index (B2). The runtime maintains a set of identities of the COMPLETE turn envelopes it has already
  emitted (stable ACP message id if C0 finds one; else a content fingerprint over role + text/tool
  payload). During the `session/load` replay, updates are aggregated into complete turns exactly as in
  live operation; each reconstructed complete turn is emitted **only if its identity is not already in
  the set**. This is order- and boundary-independent: already-forwarded turns are dropped regardless of
  how the replay re-chunks them; genuinely new content (incl. the completed boundary turn, see C4)
  passes through. The forwarder seq/ack remains the transport backstop but is explicitly NOT relied on
  for replay correctness. AI-688 single-flight still holds.

- **C4 — boundary turn: do NOT flush a partial.** Revised from r1: at the death boundary the in-flight
  aggregation run is **discarded, not flushed** (no partial `AssistantText`/`Thinking` emitted, no
  fabricated completion). Because the partial was never emitted, its identity is not in the C3 set, so
  when `session/load` replays that turn *complete*, C3 emits it exactly once. This removes the
  dup-prefix / drop-suffix hazard entirely. (If reconnect ultimately fails, the interrupted turn is
  simply absent — acceptable, and better than a forwarded partial that never gets corrected.)

- **C5 — interaction vs SignalR re-bind.** C-reconnect (agent process) is orthogonal to the existing
  `ReBindAcpSessionsAsync` (SignalR server binding). A pure process resume keeps the same
  agentId/sessionId, so the existing SignalR binding stays valid — no re-bind needed. Documented so the
  two mechanisms aren't conflated.

**Scope note:** C1's runtime restructure (swappable process/connection, deferred finalize, crash-vs-stop
gating) is the largest single change in AI-689 and touches AI-688's teardown ordering directly. It gets
its own PR (PR3), its own C0 probe, and a deterministic "died mid-transcript → replay → dedup" test
suite. If C0 reveals `session/load` replay is not reliably matchable, reconnect is re-scoped to a
follow-up issue rather than shipped unsound (flagged for owner decision at that point).

## 5. Workstream D — docs + security posture (PR 4, mergeable first)

- **D1 operator docs** (`README.md` + a focused `docs/` page): the hosted-Cursor/ACP path (explicitly
  distinct from the Cursor *recording-hooks* path, a known confusion point); `cursor-agent acp` +
  minimum version + **Team-tier subscription** + `cursor-agent login`; env vars (`KCAP_CURSOR_PATH`,
  `KCAP_CURSOR_MODEL`, `KCAP_ACP_DEBUG_FRAMES`, `KCAP_URL`); **limitations** (no local-attach raw
  input/special-keys/resize, no terminal-output surfacing, reconnect is best-effort resume);
  **troubleshooting** (missing binary → `KCAP_CURSOR_PATH`; auth → `cursor-agent login`/Team;
  model-resolution fallback; protocol-version mismatch).
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
- **C:** fake agent that "dies" mid-session → runtime relaunches + `session/load`s the same
  sessionId; **replayed updates ≤ watermark are suppressed** (assert no duplicate envelopes reach the
  forwarder); post-watermark content forwards; give-up after N attempts finalizes cleanly. Live
  `KCAP_ACP_LIVE` end-to-end resume.

## 8. Risks / open questions

- **R1 (A4):** unauthenticated `cursor-agent` failure shape unverified — confirm via a logged-out
  live probe during PR 1; A4 annotates, never masks.
- **R2 (C3/C4 — reworked after r1):** replay-dedup no longer uses a seq/index watermark (unsafe — seq
  is a forwarder concept, replay boundaries unproven). It now uses **content identity** at the runtime
  emission layer + **not flushing the death-boundary partial**. Residual risk: the dedup key itself —
  whether `session/load` replay carries a stable message id (preferred) or only content (fingerprint
  fallback, with collision risk for identical turns). **C0 probe resolves this before code**; a
  deterministic died-mid-transcript→replay→dedup test locks it.
- **R2b (C1 restructure):** making the runtime swap its process + defer finalize touches AI-688's
  teardown ordering (`ReadOutputAsync`→finalize, `AcpCts`, `_updates` completion, binding unregister).
  Highest-blast-radius change; isolated to PR3 with focused tests, and re-scopable if C0 fails.
- **R3 (B2):** metrics have no exporter yet — acceptable per acceptance ("enough to diagnose",
  observable via `dotnet-counters`); a real exporter is out of scope.
- **R4:** reconnect could mask a genuinely-crashing agent by relaunching repeatedly — the bounded
  attempt cap + backoff + `acp.reconnects{failed}` metric + give-up-finalize prevent an infinite loop.
