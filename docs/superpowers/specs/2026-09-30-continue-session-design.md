# Continue a session: carry its work items and plans over

Issue: #1235.

## Problem

When a fresh agent is told "recap session X and continue working" — typically after X's agent died
(a daemon force-restart, a crash, a closed terminal) — the new session C does not inherit X's
links. It works through X's plan but never attaches to it, so the app shows no plan on C; it is not
attached to X's work items either. The recap and plans skills describe the manual steps, but a skill
is guidance an agent can skip.

A second gap blocks any tool that guards against taking over a live session: when X's agent was
private, or its daemon could not reach the server, nothing tells the server X is gone. X stays
`active` with a recent last event, indistinguishable from a session that is still running.

## Goals

- One MCP call attaches C to X's work items and X's unfinished plans, and reports what it attached.
- The call refuses to take over a session that is still running, and does not refuse one whose
  process is provably gone, whatever the server believes.
- `get_session_summary` shows X's work items next to `declared_plans`, so an agent reading a recap
  sees what it would take over.

## Non-goals

- **No continuation link** (`previous_session_id`) between X and C. It is written only on the
  SessionStart hook; adding one afterwards needs a new server event, and the link means "followed in
  the same terminal", not shared work. Work items and plans are the shared work, and this attaches
  exactly those. The issue's open question is answered with this reasoning.
- **No server-side SessionEnd for a dead local process.** The local exit record below could drive
  one, but the dead session may belong to a different profile or server than the current one. Filed
  as a follow-up.
- No server change. Everything composes existing endpoints.

## Design

### 1. Local exit records (`AgentSessions`)

`AgentSessions` (`src/Capacitor.Cli/AgentSessions.cs`) keeps `agent-sessions/<pid>` notes holding
`<session id>\n<process start token>`, written by every hook through
`WatcherManager.EnsureWatcherRunning` — daemon-hosted private agents included. `Reap()`, run on every
hook's spool drain, deletes the notes whose process is gone, which erases the only local evidence
that X's process died before the continuing session can read it.

Change:

- `Reap()` moves a dead note to `agent-sessions/exited/<session id>` instead of deleting it. The
  record's content is the reap time (ISO-8601 UTC). A note whose file is unreadable or malformed is
  deleted as today.
- A `SessionId`-keyed file means the latest exit wins, which is what the check needs.
- `Reap()` also deletes exit records older than 30 days, matching the spools' retention.
- New query `AgentSessions.Liveness(SessionId) → Running | Exited | Unknown`:
  - `Running` — a live claimant's note names the session (`IsClaimed`).
  - `Exited` — no live claimant and an exit record exists.
  - `Unknown` — neither.
  `Running` is checked first: a session can be exited in one process and resumed in another
  (`claude --resume`), and a live claim is the stronger evidence.
- `Claimants()` must skip the `exited` subdirectory (it enumerates files, so it already does; a test
  pins it).

### 2. `continue_session` in `kcap-workitems`

Lives in `McpWorkItemsServer`, not `kcap-sessions`: that server is auto-approved for reviewers as a
read-only whole, and `KcapMcpRegistryReviewFlowTests` pins its tool set. Annotation: `Additive`.

Inputs:

- `session_id` (required) — X, the session being continued.
- `force` (optional bool, default false) — take over even when X looks live.

C is always the current session, resolved through `McpSessionId` with no override.

Steps:

1. **Refusals** (tool error, nothing written):
   - C cannot be resolved.
   - X equals C after canonicalisation.
   - `GET /api/sessions/{X}/summary` is 404 — "not found or not visible".
2. **Liveness**, unless `force`. Run `AgentSessions.Reap()` first so the answer does not depend on
   whether a hook has drained yet, then:
   - `Running` → refuse: "X is still running on this machine". Pass `force: true` only if the
     user confirms.
   - `Exited` → proceed.
   - `Unknown` → use the summary's `status` and `last_event_at`: proceed when `Ended`, or when the
     last event is older than 1 hour (the server's own `SessionStaleness.Threshold`). Otherwise
     refuse: "X still looks active (last event …) and did not run on this machine; if its agent is
     gone, ask the user and retry with `force: true`."
3. **Read** in parallel: `GET /api/work-items/session/{X}` and `GET /api/sessions/{X}/plans` (the
   latter follows X's continuation chain server-side).
4. **Work items**: for each, `POST /api/work-items/declare {session_id: C, work_item_id}`.
5. **Plans**: skip finished plans, using the same `finished` rule `get_session_summary` projects
   (server `finished` if present, else `total_known && completed == total && is_complete`). For each
   remaining plan pick the first `in_progress` task by ordinal, else the first `pending`; none →
   skip with reason `no_open_task`. Then
   `POST /api/plans/{plan_id}/tasks/{task_id} {session_id: C, status: <the task's current status>}`.
   Re-sending the current status changes nothing but the attachment, and the server makes the plan
   C's current plan. The plan with `is_current: true` on X is adopted last, so it ends up current
   on C.
6. **Result** (not a tool error when individual writes fail):

   ```json
   {
     "continued_from": "<X>",
     "liveness": "exited | stale | ended | forced",
     "work_items": [{ "work_item_id": "…", "label": "…", "attached": true }],
     "plans": [{ "plan_id": "…", "task_id": "…", "title": "…", "status": "in_progress", "attached": true }],
     "skipped_plans": [{ "plan_id": "…", "reason": "finished | no_open_task" }],
     "current_plan_id": "…"
   }
   ```

   A failed write carries `"attached": false, "error": "<status or message>"` on its entry. It is a
   tool error only when every attempted write failed. `current_plan_id` is the last plan attached
   successfully, omitted when none.

A 401 on any call surfaces the existing not-logged-in message, as the other tools do.

### 3. `get_session_summary` lists work items

`McpSessionsServer` adds a third best-effort GET, alongside the recap and plans reads:
`GET /api/work-items/session/{X}`, with the same 10 s timeout and drop-on-failure as the plans
read. It projects `work_items: [{work_item_id, label, is_primary}]`, omitted when empty or failed.
The tool description says that to continue X's work, call `continue_session` in `kcap-workitems`.
The tool stays read-only.

### 4. Skills and docs

- `kcap/skills/recap/SKILL.md` "Continuing another session's work": read the summary, call
  `continue_session(session_id: X)`, tell the user what it attached, then verify the working tree
  before resuming (the plans skill's "Verify before continuing" still applies). On a liveness
  refusal, ask the user; never pass `force` on your own.
- `kcap/skills/plans/SKILL.md` "Resuming a plan" stays for plans found through `list_repo_plans`
  with no session to continue, and points to `continue_session` when there is one.
- `kcap/skills/work-items/SKILL.md`: one line naming `continue_session`.
- Tool tables in `kcap/README.md` and `src/Capacitor.Cli.Core/Resources/help-mcp.txt`.

PR #1236 rewrites the same recap and plans sections for the manual procedure. This change lands on
top of it: merge #1236 first, or fold its text in here.

## Testing

`AgentSessionsTests`:

- A dead claimant's note becomes an exit record keyed by its session; a live one is untouched.
- `Liveness` returns `Running` for a live claim, `Exited` for an exit record with no live claim,
  `Running` when both exist, `Unknown` for neither.
- Exit records older than 30 days are reaped; `Claimants()` ignores the `exited` directory.

`McpWorkItemsServerTests` (WireMock):

- Attaches every work item and every unfinished plan; finished plans and plans with no open task are
  listed as skipped.
- Task choice: `in_progress` wins over an earlier `pending`; the current status is sent unchanged.
- X's current plan is adopted last.
- Refusals: self-continue, X not found, `Running`, `Unknown` + active + recent; `force` overrides
  the last two.
- `Exited` proceeds even when the server says active and recent.
- Partial failure is reported per entry and is not a tool error; all writes failing is.

`McpSessionsServerTests`: `work_items` projected when present, omitted when empty or when the read
fails.

`KcapMcpRegistryReviewFlowTests` needs no change; it would fail if the tool were added to
`kcap-sessions`.
