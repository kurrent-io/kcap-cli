# AI-689 — ACP hardening & docs: gap analysis + proposed decomposition

Parent epic **AI-682**. Follows the now-merged AI-684 (foundation), AI-686 (permission/elicitation
bridge), AI-687 (fs/terminal capability), AI-688 (Cursor prototype + transcript forwarding). This
work is entirely in **kcap-cli** (the daemon). Branch `ai-689-acp-hardening-docs` off `origin/main`
(`f724432`, includes both merges).

The issue is broad (5pts, six scope items). A full inventory of the merged ACP code shows AI-688 did
most of the *hardening* heavy-lifting, so the real remaining work is narrower and centers on
**diagnostics, protocol-version negotiation, operator docs, observability, and a couple of privacy
fixes** — not another pass over cancellation/reconnect/cleanup.

## What's already done (do NOT redo)

- **Item 1 hardening — mostly complete (AI-688).** Per-agent `CancellationTokenSource`; ordered
  `DisposeAsync` teardown; bounded channels (`_updates`/`_transcript` 2000 DropOldest, `_pendingTurns`
  50 DropWrite) with drop-count Warnings; fault isolation on all loops; bounded re-bind retry
  (3×250ms) with unregister-on-give-up, gated before `IsReady`; forwarder seq/ack + gap-resend +
  terminal-drop + hot-loop guard + bounded send-retry; child-process SIGTERM→kill + stderr-drain
  deadlock avoidance; malformed-frame resilience (skip-and-continue, never kills the read loop);
  inbound server-request answered exactly once (`-32601`/`-32603`/fallback). Handshake failures
  rethrow a clear wrapped exception and the factory disposes a half-started child.
- **Item 5 privacy — partially done.** Wire envelope is structured with **no** `Raw`/`rawInput`/
  `rawOutput` field; `AcpConnection` logs frame *shape* only (never params/content); AI-687
  fs/terminal decline; fail-closed permission mapping; session-id-from-params fail-safe.

## Genuine remaining work (the real AI-689)

| # | Gap | Evidence |
|---|---|---|
| 2a | **Protocol-version negotiation is entirely absent.** We send `protocolVersion:1` but discard the `initialize` response and never validate the agent's version. | `AcpHostedAgentRuntime.cs:271` discards result; `:264` sends v1 |
| 2b | **Missing-binary is silent.** `cursor-agent` not found → vendor silently omitted, no operator message pointing at `KCAP_CURSOR_PATH`. | `AcpHostedAgentRuntimeFactory.cs:40`; `DaemonRunner.cs:286-290` |
| 2c | **Auth / no-Team-subscription failures** surface only as cursor-agent stderr at Debug or a generic handshake error — no actionable messaging. (Real-world message quality unverified — the AI-684 probe never hit an unauthenticated agent.) | `AcpChildProcess.cs:54`; `AcpHostedAgentRuntime.cs:284` |
| 3a | **No Info-level operational logging** for ACP launch / session-started / capability-negotiated / interaction outcome. Operator at default level sees nothing unless something fails. | ACP files: 0 Info, ~22 Debug, 6 Warning |
| 3b | **Two Debug payload-leak vectors** (unredacted content): the Unknown-kind full-raw-update dump, and cursor-agent stderr. | `AcpEventTranslator.cs:106-108`; `AcpChildProcess.cs:54` |
| 4 | **Essentially no operator docs** for the hosted-Cursor/ACP path: no `cursor-agent acp` / version / Team-tier requirement, no limitations (no local-attach input/keys/resize/terminal output), no troubleshooting. Only 2 env vars documented; the recording-hooks Cursor path is easily confused with the hosted path. | `README.md:493, 591-598, 687-696`; `docs/` are design-only |
| 5b | **No documented ACP data-handling posture** (transcript forwards verbatim prompt/tool-args/results, same as every agent — needs to be stated, not silently true). | `AcpHostedAgentRuntime.cs:565,615-632` |

## Scope-fork decisions (need your call)

These three are genuine forks where the evidence points toward keeping AI-689 lean; I recommend
deferring each and documenting why, but they're yours to set.

- **D1 — Metrics infrastructure.** There is **no** metrics/telemetry stack anywhere in the repo
  (no `System.Diagnostics.Metrics`, no `Meter`, no exporter — only ad-hoc `Interlocked` counters).
  The acceptance criterion is "logs/metrics **enough to diagnose** launch/protocol failures," which
  **Info-level structured logging satisfies**. Building a metrics stack (with AOT care) has no
  existing consumer/exporter. **Recommend: Info-logging only now; defer a metrics stack** (separate
  issue if/when there's a consumer).
- **D2 — Raw-debug stream (Item 6).** None exists today. The acceptance criterion is "privacy-
  sensitive raw capture is **absent by default** or opt-in with limits" — **absent already
  satisfies it.** **Recommend: don't build raw capture; document the shape-only posture + close the
  two Debug leak vectors (3b).** (An opt-in gated full-frame trace could be a later diagnostic
  nicety, not needed for dogfooding.)
- **D3 — ACP process reconnect (Item 1 residual).** Relaunching/resuming a dead `cursor-agent`
  mid-session is a **feature**, not hardening — today a dead child cleanly finalizes the session
  (acceptable for dogfooding). `AcpTranscriptForwarder.cs:51` already flags "deeper reconnect
  resilience remains AI-689." **Recommend: out of scope for AI-689; spin a follow-up** if dogfooding
  shows it's needed.

## Proposed lean scope + slicing

If D1/D2/D3 are all "defer" (my recommendation), AI-689 becomes a well-bounded, dogfooding-ready
change, best delivered as **two PRs**:

- **PR 1 — code (diagnostics + negotiation + observability):** validate `initialize.protocolVersion`
  with a clear mismatch error (2a); actionable missing-binary + auth/subscription diagnostics (2b/2c);
  Info-level lifecycle logging via source-gen `[LoggerMessage]`, AOT-safe (3a); close the two Debug
  leak vectors (3b). Tests for each; NativeAOT gate clean.
- **PR 2 — docs + security posture:** operator setup for the hosted-Cursor/ACP path, `cursor-agent
  acp` + version + Team-tier requirement, env vars, limitations, troubleshooting (4); a short
  documented ACP data-handling posture (5b). Pure docs — cheap, unblocks dogfooding, mergeable first.

Acceptance-criteria coverage under this scope: documented setup/failure modes ✓ (PR 2 + 2a-c);
logs enough to diagnose ✓ (3a/3b + 2a-c); raw capture absent-by-default ✓ (D2 + 3b); Cursor ready for
routine dogfooding ✓. Deferred with rationale: metrics stack (D1), raw-debug stream (D2), process
reconnect (D3).
