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
- Zero added latency on the hot path and no risk of pushing a hook past its
  timeout.

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
- Retain `Append`, `ReapOlderThan`, the 1 MB cap eviction, and the
  `SafeSessionId` (`^[0-9a-fA-F]{32}$`) guard.

### 2. Cursor migration

- `CursorHookCommand` switches to `HookSpool`, storing `route`
  (`mapping.RouteSegment`) instead of the event name, and uses
  `DrainAllAsync` (gaining cross-session recovery — its terminal `sessionEnd`
  is currently only replayed on resume).
- Best-effort one-time migration: on first run, move existing
  `~/.cursor/kcap-pending/*.jsonl` into `~/.config/kcap/spool/`. If skipped,
  old entries simply reap after 30 days.

### 3. `ClaudeHookCommand` — session-start

Restructure so a failed start never loses the session:

- **Always** `EnsureWatcherRunning` (idempotent; does not need the response) so
  transcript capture continues regardless of POST outcome.
- **On success:** emit the SessionStart context envelope + post plan content (as
  today).
- **On transient failure:** `spool.Append(sid, "session-start", body)` (body is
  already repo-enriched and carries the original payload). Replay later creates
  the server record; the watcher's buffered transcript reconciles against it.
- `session-start` is `async: true` in the plugin, so Claude does not block on
  the exit code; return 0.

### 4. `ClaudeHookCommand` — session-end

- **Stamp `ended_at`** (ISO-8601, computed once) into the session-end body
  before the POST, mirroring the watcher parent-exit path
  (`WatchCommand.PostSessionEndOnParentExitAsync`), so a late replay records the
  true end time rather than the flush time.
- **On transient failure:** `spool.Append(sid, "session-end", body)`, write a
  stderr breadcrumb (`recoverable via: kcap import --session <id>`), and
  **return 0** (durably captured) instead of 1.
- **4xx (permanent):** do not spool; preserve current behavior.

### 5. Drain step (every invocation)

Run at the **end of `Handle`**, after the current event's own POST, **gated on
that POST having succeeded** (server provably reachable now):

- If the current POST failed, skip the drain entirely → ~0 cost when the server
  is down.
- Budget: **2 s**, further clamped to the hook's remaining headroom
  (`hookTimeout − elapsed − 1 s safety`) so it never risks a hook timeout. The
  per-event hook timeouts (`kcap/hooks/hooks.json`): `SessionEnd` 15 s;
  `Notification`/`Stop` 5 s; `SessionStart`/`SubagentStart`/`SubagentStop` 5 s
  (`async`). `PermissionRequest` early-returns before this point and is unaffected.
- Cheap no-op when the spool dir is empty (single existence/enumeration check).
- Relies on `DrainAllAsync` stop-on-first-failure for the mid-drain
  server-drop case.

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
- **Spool scope = session-start + session-end** only (the canonical
  lifecycle events), matching Cursor's `SpoolOnFailure` selection. `stop`,
  `notification`, and `subagent-*` are not spooled — they only ensure the
  watcher runs and carry nothing lifecycle-critical to replay.
- **Ordering:** transcript lines may reach the server (via the watcher's
  SignalR stream) before a spooled `session-start` is replayed. This already
  happens today on resume and is tolerated by the server's lazy session
  handling; the spooled start reconciles when it lands.

## Failure classification

Spool on **transient** failures only:

- `HttpRequestException` (connection refused / DNS / reset)
- request timeout (`TaskCanceledException` not originating from our own budget)
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
- session-end on 4xx → not spooled
- session-start on failure → spooled **and** watcher spawned
- next hook with server up → backlog drained (cross-session), files deleted
- drain skipped when current POST fails (server down)
- drain budget never exceeds the hook's remaining headroom

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
  route, cross-session drain, dir migration)
- `src/Capacitor.Cli/Commands/CursorHookEventMap.cs` (route now carried in the
  spool entry; event→route resolution at append time)
- `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` (session-start restructure,
  session-end spool + `ended_at`, end-of-`Handle` drain step)
- `src/Capacitor.Cli.Core/PathHelpers.cs` (shared `spool` path — via existing
  `ConfigPath`, no change expected)
- Tests under `test/Capacitor.Cli.Tests.Unit/`

No new CLI command or flag → `README.md` unaffected (to be re-verified during
implementation).
