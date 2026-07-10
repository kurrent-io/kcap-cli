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
  unauthenticated / non-Team account fails, surface an actionable message ("cursor-agent is not
  logged in or lacks a Team subscription — run `cursor-agent login`"). Detection uses the failing
  RPC's error text plus the presence of `authMethods`. **Caveat:** the exact unauthenticated failure
  shape is unverified (the AI-684 probe never hit it); A4 is best-effort — it must never *mask* the
  underlying error, only annotate it. A short live probe against a logged-out `cursor-agent` should
  confirm the shape during implementation.

## 3. Workstream B — observability (PR 2)

- **B1 Info-level lifecycle logging** via source-gen `[LoggerMessage]` (the AOT-safe pattern already
  used in `ServerConnection`/`AgentOrchestrator`; the `Acp/` files currently use raw `ILogger`).
  Events, all payload-free (ids/metadata only): launch requested, handshake ok (protocolVersion +
  loadSession + model), session started, session loaded/resumed, blocking request issued+resolved
  (kind + decision, never content), reconnect attempt/success/give-up, session ended.
- **B2 metrics stack.** Introduce a single `Meter` (`"Capacitor.Cli.Daemon.Acp"`) with counters:
  `acp.launches`, `acp.sessions_started`, `acp.sessions_loaded`, `acp.blocking_requests` (tag: kind),
  `acp.reconnects` (tag: outcome), `acp.failures` (tag: stage). `System.Diagnostics.Metrics` is
  NativeAOT-safe (no reflection); **no exporter is wired** — counters are observable via
  `dotnet-counters`/any future OTel exporter. Documented as such (acceptance is "metrics enough to
  diagnose", not "shipped dashboards").
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

The heart of Full scope. Today a mid-session `cursor-agent` death ends the read loop, faults pending
requests, and finalizes the session. Replace that with a bounded resume attempt.

- **C1 death detection.** In `AcpHostedAgentRuntime`, distinguish an *unexpected* child exit /
  read-loop end (process died while the session was active and no intentional stop/dispose was
  requested) from normal teardown. Only the unexpected case triggers reconnect.
- **C2 resume.** If `loadSession` was advertised (A2): relaunch `cursor-agent acp` (same cwd/env via
  the factory), `initialize` (re-run A1 protocol check + A2 capture), then
  `session/load {sessionId, cwd, mcpServers}` with the SAME sessionId. On success the session is live
  again and accepts prompts. Bounded attempts (default 3) with backoff; on give-up, finalize cleanly
  (today's behavior) with a Warning + `acp.reconnects{outcome=failed}`.
- **C3 replay dedup (subtle — from the probe).** `session/load` **replays prior history** as
  `session/update` notifications. Those map to envelopes the forwarder has ALREADY sent. We must not
  double-forward. Approach: the runtime tracks the highest transcript sequence already emitted before
  the death; during a load-triggered replay it **suppresses re-emission up to that watermark** (drop
  replayed updates whose reconstructed envelope index ≤ watermark), resuming live emission only for
  post-watermark content. The forwarder's existing seq/ack machinery is the backstop (monotonic seq;
  the server already ignores/acks by seq), but suppression at the source avoids relying on it for
  correctness. Overlapping-turn single-flight (AI-688) still holds.
- **C4 in-flight turn.** A turn interrupted by the death cannot be assumed complete. After resume,
  the open aggregation run is flushed/closed at the death boundary (no fabricated completion); a
  partial `AssistantText`/`Thinking` is emitted as-is. New prompts continue normally.
- **C5 interaction vs SignalR re-bind.** C-reconnect (agent process) is orthogonal to the existing
  `ReBindAcpSessionsAsync` (SignalR server binding). Both can fire; ordering: a process resume keeps
  the same agentId/sessionId, so the existing binding stays valid — no re-bind needed for a pure
  process resume. Document the interaction so the two mechanisms aren't conflated.

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

1. **PR 4 (docs + security)** first — pure prose, zero code risk, unblocks dogfooding immediately.
2. **PR 1 (diagnostics + negotiation)** — small, self-contained; also lands the typed `InitializeResult`
   that PR 3 depends on.
3. **PR 2 (observability)** — logging + metrics + leak fixes + raw-frame flag.
4. **PR 3 (reconnect)** — largest; depends on PR 1's `InitializeResult`/capability capture. Includes a
   short live probe to confirm the death→relaunch→`session/load` path end-to-end (gated
   `KCAP_ACP_LIVE`), mirroring AI-688's live test.

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
- **R2 (C3):** replay-dedup watermark correctness is the riskiest piece — the forwarder seq/ack is the
  backstop, but source suppression must be covered by a deterministic "died mid-transcript then
  replays" test.
- **R3 (B2):** metrics have no exporter yet — acceptable per acceptance ("enough to diagnose",
  observable via `dotnet-counters`); a real exporter is out of scope.
- **R4:** reconnect could mask a genuinely-crashing agent by relaunching repeatedly — the bounded
  attempt cap + backoff + `acp.reconnects{failed}` metric + give-up-finalize prevent an infinite loop.
