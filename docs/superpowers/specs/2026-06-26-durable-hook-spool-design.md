# Durable session lifecycle hooks via a unified hook spool

- **Date:** 2026-06-26
- **Status:** Approved (design)
- **Scope:** `kcap-cli` only (no server changes required)

## Problem

A session that ended cleanly with `/exit` stayed **Active** in the Capacitor UI
with no `SessionEnded` recorded. Investigation of session
`9dc2775376454e4691ecc2d69973c152`:

- The transcript tail (`/exit`, "Bye!") reached the server, so the `SessionEnd`
  hook fired and had connectivity — its `InlineDrainAsync` POST to
  `/hooks/transcript` succeeded.
- But `SessionEnded` was never written and `get_session_summary` returned an
  empty `summary_text` (the "what's-done" generator only runs after a successful
  `session-end` POST). So the `POST /hooks/session-end` did **not** complete.
- Most likely cause: the server was briefly unavailable (a deploy window, ~15 s)
  exactly during the hook. Claude kills the `SessionEnd` hook at its configured
  timeout (15 s), so the in-hook retry (`PostWithRetryAsync`) was cut off before
  the server returned. Any retry that lives **inside the hook process** cannot
  outlast an outage longer than the hook's timeout budget.

The same fragility applies to **session-start**: a failed start POST currently
`return 1`s *before* the watcher is spawned (`ClaudeHookCommand.cs:254`), so a
deploy at session start loses the **entire** session — no server record *and* no
transcript capture.

The general failure mode: a brief server outage during a lifecycle hook
permanently drops a lifecycle event, because the hook process dies before the
server recovers.

## Goals

- A failed **session-end** POST is eventually delivered, so sessions don't get
  stuck Active across a brief server outage.
- A failed **session-start** POST is eventually delivered, and transcript
  capture is never lost even when the start POST fails.
- Recovery is automatic, requires no user action, and survives the hook process
  being killed by Claude's timeout.
- Negligible added latency — a no-op when the spool is empty, fast-fail when the
  server is down — and no risk of pushing a hook past its timeout.

## Non-goals

- **Long-tail delivery when the machine never runs `kcap` again.** The spooled
  event sits on disk until a future `kcap` hook fires or the user runs
  `kcap import`. A server-side quiet-session sweeper would be the backstop; it is
  out of scope (this is a CLI-side solution by decision).
- **Replaying real-time session-start side effects.** Context injection
  (lessons/version-nudge/plan content emitted to stdout) cannot be injected into
  an already-started session, so it is lost during an outage. Unavoidable.
- The separate watcher defects surfaced during diagnosis (parent-exit watchdog
  never starting on dead-at-startup parent PID; the watcher dying without a
  graceful shutdown) are tracked separately and are **not** addressed here.

## Approach

A vendor-neutral, on-disk **hook spool**. When a lifecycle POST fails
transiently (server unreachable / 5xx / timeout), the hook appends the payload
to the spool and exits cleanly. On *every* subsequent `kcap hook` invocation, a
**cross-session** drainer flushes all pending entries once the server is
confirmed reachable. The server's existing session-start/session-end
idempotency makes replay safe with no server changes.

```
lifecycle POST ──success──▶ done
       │
   transient fail
       ▼
  spool/<sid>.jsonl  ◀── append {route, body}
       │
  next hook (any session, any event) ──▶ DrainAllAsync ──server up──▶ replay → delete
```

Cross-session draining is essential because `session-end` is the **terminal**
hook for a session — the existing Cursor spool only replays a session's own
backlog on that session's *next* hook, which for a terminal event never comes
(until resume). The drainer must flush *any* session's stranded events the next
time *any* hook fires.

## Hook-budget discipline (the actual root cause)

Spooling on failure is only useful if the hook **reaches** the spool code before
Claude kills it. Today `ClaudeHookCommand` posts via `PostWithRetryAsync`
(`ClaudeHookCommand.cs:247`), whose default total budget is **30 s**
(`HttpClientExtensions.cs:79`, `SendWithRetryAsync`). Against a *hung* server
(as opposed to a clean connection refusal), that call blocks past the 15 s
`SessionEnd` hook timeout, so Claude SIGKILLs the hook **before** the
`!IsSuccessStatusCode` branch runs — nothing is ever spooled and the bug
persists. Budgeting only the *later* drain does not fix this.

The fix is a **shared per-invocation deadline**:

- A single `Stopwatch` from the start of `Handle`, and a hook-budget ceiling per
  event (mirroring `kcap/hooks/hooks.json`: `SessionEnd` 15 s, `SessionStart` /
  `Notification` / `Stop` / `SubagentStart` / `SubagentStop` 5 s) minus a
  **safety margin (~1.5 s)** reserved so a failed POST still has time to spool
  and exit.
- Every network step — the pre-event drain, the session-end inline transcript
  drain, and the lifecycle POST — is clamped to the **remaining** budget. If
  earlier steps overrun, later steps get a small timeout and fail fast; either
  way the spool-on-failure path is always reached before the kill.
- The lifecycle POST itself becomes a **single bounded attempt** via the
  existing `PostOnceAsync` (`HttpClientExtensions.cs:160`) with
  `timeout = remaining`, replacing `PostWithRetryAsync`. A single attempt that
  fails fast and spools is more robust here than retries that risk the hook
  timeout — durable replay, not in-hook retry, provides the reliability.

This guarantees spool-before-kill against connection refusal, 5xx, **and** a
slow/hung server.

## Components

### 1. `HookSpool` (generalize `CursorHookSpool`)

`CursorHookSpool` is already close to vendor-neutral. Generalize it:

- **Location:** rename to `HookSpool`, vendor-neutral namespace
  (`Capacitor.Cli.Commands`). Spool dir → `PathHelpers.ConfigPath("spool")`
  (`~/.config/kcap/spool/`), replacing Cursor's `~/.cursor/kcap-pending/`.
- **Entry format:** keep the per-session JSONL layout
  (`{spoolDir}/<dashless-sid>.jsonl`, FIFO, 1 MB/session cap, 30-day reap), but
  store an explicit **`route`** per line (e.g. `session-end`,
  `session-start`, `session-end/cursor`) so the drainer needs **no** vendor
  event→route map. Line shape: `{"route": "<segment>", "body": "<raw payload>"}`.
  Lines lacking `route` (old Cursor format) are skipped and reaped — acceptable
  since spool entries are transient failed POSTs.
- **New method `DrainAllAsync(poster, budget, ct)`** — cross-session drain:
  enumerate all `*.jsonl`, FIFO-drain each via the existing per-file logic,
  invoke `poster(route, body)` per entry, mark-delivered (rewrite remaining /
  delete emptied file) on success, and **stop on the first failed POST or budget
  expiry**. `poster` is a `Func<string /*route*/, string /*body*/, Task<bool>>`.
- **Route-specific replay side effects live in the poster, not the spool.** The
  poster is a vendor-owned closure that performs the POST *and* handles the
  response. The Claude poster therefore parses a replayed `session-end`
  response and spawns the what's-done generator when `generate_whats_done` is
  set (`ClaudeHookCommand.cs:260`) — otherwise a session un-stuck by replay
  would still have no summary, which was part of the original symptom. Keeping
  this in the closure means `DrainAllAsync` stays generic (success/fail only)
  while no live-path side effect is lost on replay.
- Retain `Append`, `ReapOlderThan`, the 1 MB cap eviction, and the
  `SafeSessionId` (`^[0-9a-fA-F]{32}$`) guard.

### 2. Cursor migration

- `CursorHookCommand` switches to `HookSpool`, storing `route`
  (`mapping.RouteSegment`) instead of the event name, and uses
  `DrainAllAsync` (gaining cross-session recovery — its terminal `sessionEnd`
  is currently only replayed on resume).
- **Cursor keeps its full `SpoolOnFailure` set unchanged** — `sessionStart`,
  `sessionEnd`, `beforeSubmitPrompt`, `afterAgentThought`
  (`CursorHookEventMap.cs:12`). The spool component is event-agnostic (it stores
  whatever `route` it's handed); each vendor's command decides what to spool.
  This is **not** a narrowing of Cursor durability — see Idempotency & ordering.
- **Migration must transform, not just move.** Old Cursor spool lines use
  `{"hook_event_name", "body"}` and the new drainer keys on `route`, so a bare
  file move would silently discard the backlog (the drainer skips lines without
  `route`). The one-time, best-effort migration reads each
  `~/.cursor/kcap-pending/*.jsonl` line, resolves `route` from the event name
  via `CursorHookEventMap`, rewrites it in the new format under
  `~/.config/kcap/spool/`, and removes the old file. Lines whose event no
  longer maps are dropped. Failures are swallowed; un-migrated leftovers reap
  after 30 days.

### 3. `ClaudeHookCommand` — session-start

Restructure so a failed start never loses the session:

- **Always** `EnsureWatcherRunning` (idempotent; does not need the response) so
  transcript capture continues regardless of POST outcome.
- **On success:** emit the SessionStart context envelope + post plan content (as
  today).
- **On transient failure** (bounded POST per the budget-discipline section):
  `spool.Append(sid, "session-start", body)` (body is already repo-enriched and
  carries the original payload). Replay later creates the server record; the
  watcher's buffered transcript reconciles against it.
- `session-start` is `async: true` in the plugin, so Claude does not block on
  the exit code; return 0.

### 4. `ClaudeHookCommand` — session-end

- **Stamp `ended_at`** (ISO-8601, computed once) into the session-end body
  before the POST, mirroring the watcher parent-exit path
  (`WatchCommand.PostSessionEndOnParentExitAsync`), so a late replay records the
  true end time rather than the flush time.
- **Bounded POST** per the budget-discipline section (single `PostOnceAsync`
  clamped to the remaining hook budget), replacing `PostWithRetryAsync`.
- **On transient failure** (refusal / 5xx / 408 / 429 / timeout):
  `spool.Append(sid, "session-end", body)`, write a stderr breadcrumb
  (`recoverable via: kcap import --session <id>`), and **return 0** (durably
  captured) instead of 1.
- **4xx (permanent, except 408/429):** do not spool; preserve current behavior.

### 5. Drain step (every invocation)

Run **before** the current event's own POST — matching the existing Cursor
ordering (`CursorHookCommand.cs:115`) and the test that pins it
(`CursorHookCommandTests.cs:90`, `spool_drain_runs_before_current_event_under_budget`).

Draining-before is required for **correctness**, not just parity: if the same
session has a stranded `session-start` in the spool and is now firing
`session-end`, draining *after* would post `SessionEnded` before the replayed
`SessionStarted` — an inversion. Draining first preserves lifecycle order.

- **No success-gate needed.** `DrainAllAsync` stops on the first failed POST, so
  when the server is down the drain fails fast on the first entry and bails
  (~one bounded request, per the budget-discipline section) before the fresh
  event is attempted. This is cheaper and simpler than gating on a later POST.
- Budget: **≤ 2 s**, clamped to the shared per-invocation remaining budget so it
  always leaves headroom for the current event's own work and bounded POST. The
  per-event ceilings are listed in the budget-discipline section.
  `PermissionRequest` early-returns before this point and is unaffected.
- Cheap no-op when the spool dir is empty (single existence/enumeration check).

Because every hook drains and delivery is incremental + idempotent, a backlog
flushes across successive hooks rather than needing one big flush — so a small
budget is sufficient. A deploy strands only a handful of concurrent sessions,
which the frequent `stop`/`session-start` hooks flush within seconds of the
server returning, before any later `session-end` would need to.

## Idempotency & ordering

- **Idempotent replay** is already guaranteed server-side: both `kcap import`
  (`ImportCommand.cs:2259`) and the watcher parent-exit path re-POST
  session-end, and resume re-fires session-start; the server dedupes on
  deterministic event ids. No server changes required.
- **Spool scope is per-vendor, set by each hook command — not by the spool.**
  - *Claude (new):* `session-start` + `session-end` (the canonical lifecycle
    events). `stop`, `notification`, and `subagent-*` are not spooled — they
    only ensure the watcher runs and carry nothing lifecycle-critical to replay.
  - *Cursor (unchanged):* its existing four `SpoolOnFailure` events —
    `sessionStart`, `sessionEnd`, `beforeSubmitPrompt`, `afterAgentThought`. The
    unification preserves this; it is **not** narrowed to the two lifecycle
    events.
- **Lifecycle ordering on replay** is preserved by draining before the current
  event (Component 5): a stranded `session-start` is replayed before the current
  `session-end` for the same session.
- **Transcript-vs-start ordering:** transcript lines may still reach the server
  (via the watcher's SignalR stream) before a spooled `session-start` is
  replayed. This already happens today on resume and is tolerated by the
  server's lazy session handling; the spooled start reconciles when it lands.

## Failure classification

Spool on **transient** failures only:

- `HttpRequestException` (connection refused / DNS / reset)
- bounded-POST timeout against a slow/hung server — the `PostOnceAsync` deadline
  firing surfaces as `OperationCanceledException`; this is the case
  `PostWithRetryAsync` could not handle within the hook budget
- HTTP `5xx`, `408`, `429`

Do **not** spool on `2xx` (success) or other `4xx` (permanent — bad request,
auth). This avoids spooling payloads that will never succeed.

## Error handling / safety

Entirely **fail-open**, matching the existing hook contract: any IO / JSON /
network error in spool append, drain, or migration is swallowed and never
crashes a hook or blocks Claude. Tight, headroom-clamped budgets respect the
5 s / 15 s hook timeouts. AOT-clean — `JsonNode`/`JsonObject` only, no
reflection-based serialization or `JsonArray` collection expressions.

## Testing

TUnit unit tests (Microsoft Testing Platform), WireMock.Net for HTTP:

`HookSpool`:
- append then `DrainAllAsync` delivers in FIFO order across multiple session
  files
- stop-on-first-failure leaves undelivered entries in place
- budget expiry leaves the remainder for next time
- 1 MB cap evicts oldest entries
- `ReapOlderThan` deletes stale files
- fail-open on unreadable/locked files
- old-format lines (no `route`) are skipped, not crashed on

`ClaudeHookCommand`:
- session-end on 5xx / unreachable → spooled, returns 0, `ended_at` stamped
- **session-end against a slow/hung server (WireMock fixed delay > bounded
  timeout) → POST aborts within the hook budget and the entry is spooled** (the
  root-cause test; would hang with `PostWithRetryAsync`)
- session-end on 4xx → not spooled
- session-start on failure → spooled **and** watcher spawned
- next hook with server up → backlog drained (cross-session), files deleted
- **drain runs before the current event** (replayed `session-start` posts before
  the current `session-end`; mirrors the Cursor ordering test)
- **replayed `session-end` whose response has `generate_whats_done` spawns the
  what's-done generator** (side effect not lost on replay)
- drain budget never exceeds the hook's remaining headroom; drain fails fast
  when the server is down

`CursorHookCommand` (regression guard):
- still spools all four `SpoolOnFailure` events after the switch to `HookSpool`
- migration transforms old `{hook_event_name,body}` lines into `{route,body}`
  (backlog survives, not discarded)

## Known gaps / future work

- Long-tail (machine never runs `kcap` again): manual `kcap import --session
  <id>` remains the escape hatch; a server-side quiet-session sweeper is the
  eventual backstop (separate, server-repo work).
- Session-start context injection is lost during an outage (unavoidable).
- Watcher defects from the diagnosis (watchdog-never-started, abrupt watcher
  death) are separate work.

## Affected files

- `src/Capacitor.Cli/Commands/CursorHookSpool.cs` → generalized/renamed
  `HookSpool.cs` (+ `route` field, `DrainAllAsync`)
- `src/Capacitor.Cli/Commands/CursorHookCommand.cs` (use `HookSpool`, store
  route, cross-session drain, transforming dir migration; keep all four
  `SpoolOnFailure` events)
- `src/Capacitor.Cli/Commands/CursorHookEventMap.cs` (route now carried in the
  spool entry; event→route resolution at append + migration time)
- `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` (shared per-invocation
  deadline; drain step **before** the current event; bounded lifecycle POST via
  `PostOnceAsync`; session-start restructure; session-end spool + `ended_at`;
  Claude poster handles `generate_whats_done` on replay)
- `src/Capacitor.Cli.Core/HttpClientExtensions.cs` (reuse existing
  `PostOnceAsync` for the bounded lifecycle POST; no change expected)
- `src/Capacitor.Cli.Core/PathHelpers.cs` (shared `spool` path — via existing
  `ConfigPath`, no change expected)
- Tests under `test/Capacitor.Cli.Tests.Unit/`

No new CLI command or flag → `README.md` unaffected (to be re-verified during
implementation).
