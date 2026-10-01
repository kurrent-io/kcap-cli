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

- One call — a CLI flag or an MCP tool, both on every tier — attaches C to X's work items and X's
  unfinished plans, and reports what it attached.
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
- A note is dead only when its holder is provably gone: no process has the pid
  (`ProcessHelpers.IsProcessAlive`, which counts EPERM, and access denied on Windows, as alive), or a live one has a readable start
  token that differs from the note's (the pid was reused). A live process whose token cannot be
  read or compared leaves the note in place and writes no record: an exit record lets another
  session take this one over, so it must never rest on a guess.
- A `SessionId`-keyed file means the latest exit wins, which is what the check needs. The file
  name is the lower-cased id: a hook may write the id in a different case than the canonical one a
  takeover looks up.
- `Claim(pid, session)` deletes that session's exit record: a hook firing for it proves it is live.
- `Reap()` also deletes exit records older than 30 days, matching the spools' retention.
- New query `AgentSessions.Liveness(SessionId) → Running | Exited | Unknown`:
  - `Running` — a note names the session (compared case-insensitively) and its holder is not
    provably gone, by the same rule `Reap()` uses. A note whose token cannot be read or compared
    counts: it may be a live claimant.
  - `Exited` — no such note and an exit record exists.
  - `Unknown` — neither.
  `Running` is checked first: a session can be exited in one process and resumed in another
  (`claude --resume`), and a possibly-live claimant must not be taken over on old death evidence.
- `Claimants()` must skip the `exited` subdirectory (it enumerates files, so it already does; a test
  pins it).

### 2. The takeover core (`SessionTakeover`)

One service in `Capacitor.Cli`, called by both surfaces below, so the CLI and the MCP tool cannot
drift. Continuing a session is a generic operation: it must work on every tier, so it depends on no
tier-gated feature and never lives in a tier-gated server.

Inputs: X (the session being continued), `force` (default false). C is the current session,
resolved through `HarnessRequesterContext` — the same resolution the MCP servers use, and available
to shell commands (`CLAUDE_CODE_SESSION_ID`, `KCAP_SESSION_ID`, `CODEX_THREAD_ID`).

Steps:

1. **Refusals** (error, nothing written):
   - C cannot be resolved. The MCP tool takes an optional `current_session_id` that wins over the
     harness session, for a harness that does not export its own id.
   - X equals C after canonicalisation.
   - `GET /api/sessions/{X}/summary` is 404 — "not found or not visible".
2. **Liveness**, unless `force`. Run `AgentSessions.Reap()` first so the answer does not depend on
   whether a hook has drained yet, then:
   - `Running` → refuse: "X is still running on this machine". Continue only if the user confirms.
   - `Exited` → proceed.
   - `Unknown` → use the summary's `status` and `last_event_at`: proceed when `Ended`, or when the
     last event is older than 1 hour (the server's own `SessionStaleness.Threshold`). Otherwise
     refuse: "X still looks active (last event …) and did not run on this machine; if its agent is
     gone, ask the user and retry with force."
3. **Read** in parallel: `GET /api/work-items/session/{X}` and `GET /api/sessions/{X}/plans` (the
   latter follows X's continuation chain server-side). A 401 on either is `Unauthorized` before any
   write. A 2xx whose body is not a JSON array is a failed read (`malformed response`), never an
   empty list. The plans endpoint returns at most the 20 most recently touched plans, unpaged: a
   full page sets `plans_truncated: true`, and the report says only those were checked.
4. **Work items**: for each, `POST /api/work-items/declare {session_id: C, work_item_id}`. An MCP
   declare makes the item primary (unless the user pinned one), so the last declare wins: X's
   `is_primary` item is declared last, keeping it primary on C. On a tier
   without work items the read is a 403 with code `work_items_not_in_plan`: the part is reported as
   `not_in_plan` and the takeover goes on. Plans carry no tier gate.
5. **Plans**: skip finished plans, using the same `finished` rule `get_session_summary` projects
   (server `finished` if present, else `total_known && completed == total && is_complete`). For each
   remaining plan pick the first `in_progress` task by ordinal, else the first `pending`, skipping
   tasks whose `source` is `user`: an `mcp` write cannot override a user-set status, so the server
   would answer 409 even for an unchanged one. Tasks with `status_partial: true` are skipped too:
   their status was set by a session the caller cannot see, the server returns their note as null,
   and re-sending null would erase the stored note. No open task → skip with reason
   `no_open_task`; open tasks that are all user-set or partial → `not_adoptable`. Then
   `POST /api/plans/{plan_id}/tasks/{task_id} {session_id: C, status, note}` carrying the task's
   current status and note. The server compares both, so re-sending them unchanged records only the
   attachment; omitting the note would record a change that erases it. The server makes the plan
   C's current plan. The plan with `is_current: true` on X is adopted last, so it ends up current
   on C.
6. **Outcome**:

   ```json
   {
     "continued_from": "<X>",
     "liveness": "exited | stale | ended | forced",
     "work_items": { "status": "ok | not_in_plan | failed",
                     "items": [{ "work_item_id": "…", "label": "…", "attached": true }] },
     "plans": [{ "plan_id": "…", "task_id": "…", "title": "…", "status": "in_progress", "attached": true }],
     "skipped_plans": [{ "plan_id": "…", "reason": "finished | no_open_task | not_adoptable" }],
     "plans_error": "HTTP 500",
     "plans_truncated": true,
     "current_plan_id": "…"
   }
   ```

   `plans_error` appears only when the plans read fails, `plans_truncated` only on a full page. A
   failed write carries `"attached": false, "error": "<status or message>"` on its entry. The
   takeover fails when any attempted write failed or any read (work items or plans) failed, so a
   caller never mistakes a partial takeover for a whole one; `not_in_plan` is not a failure, and X
   having nothing to attach is still a success. `current_plan_id` is the last plan
   attached successfully, omitted when none. A 401 surfaces the existing not-logged-in message.

### 3. `kcap recap <X> --continue [--force]`

- `--continue` requires an explicit session id and excludes `--repo`; it combines with `--chain`,
  `--full` and `--per-turn`, which shape only the recap part.
- Runs the takeover first, then prints the recap as today, preceded by a `## Continued` block: what
  was attached, what was skipped and why, and which plan is now current.
- A refusal, an unresolved current session included, prints the reason to stderr, prints no
  recap, and exits 2, so an agent cannot read the recap and carry on without noticing the takeover
  did not happen. Any other takeover failure exits
  1 after the recap.
- `help-recap.txt` and the `kcap recap` section of `README.md` document both flags.

### 4. `continue_session` in a new `kcap-handoff` MCP server

- Not `kcap-sessions`: some reviewer runtimes auto-approve that server whole
  (`KcapMcpRegistry.ReviewFlowAutoApprovableServers`), so a write tool there would run unprompted for
  reviewers. Not `kcap-workitems` or `kcap-plans`: continuing must not depend on either feature.
- `kcap-handoff` (`kcap mcp handoff`, `NeedsProjectCwd: false`) serves one tool, `continue_session`
  (`session_id` required, `force` and `current_session_id` optional), which returns the outcome JSON; a refusal is a tool
  error carrying the reason. Annotation `Additive`.
- `AutoApprove: true`: it writes only attachments of the session the caller names (the harness session
  or `current_session_id`), as kcap-plans' `session_id` override already does unprompted. The liveness refusal is the guard, not a permission prompt.
- Not added to `ReviewFlowAutoApprovableServers`: a reviewer has no session to continue.
- Registration: `KcapMcpServers.All`, the `mcp` switch in `Program.cs`, `CommandServices`, and the
  bundled `kcap/.mcp.json`. The existing harness config writers derive from `All`.

### 5. `get_session_summary` lists work items

`McpSessionsServer` adds a third best-effort GET, alongside the recap and plans reads:
`GET /api/work-items/session/{X}`, with the same 10 s timeout and drop-on-failure as the plans read,
a 403 included. It projects `work_items: [{work_item_id, label, is_primary}]`, omitted when empty or
unavailable. The description says that to continue X's work, call `continue_session` in
`kcap-handoff` or run `kcap recap X --continue`. The tool stays read-only.

### 6. Skills and docs

- `kcap/skills/recap/SKILL.md` "Continuing another session's work": run `kcap recap X --continue`
  (or `continue_session`), tell the user what it attached, then verify the working tree before
  resuming (the plans skill's "Verify before continuing" still applies). On a liveness refusal, ask
  the user; never pass `--force` on your own.
- `kcap/skills/plans/SKILL.md` "Resuming a plan" stays for plans found through `list_repo_plans`
  with no session to continue, and points to the takeover when there is one.
- `kcap/skills/work-items/SKILL.md`: one line naming the takeover.
- `README.md` (recap flags, MCP server list), `kcap/README.md` tool tables, `help-mcp.txt`.

PR #1236 rewrites the same recap and plans sections for the manual procedure. This change lands on
top of it: merge #1236 first, or fold its text in here.

## Testing

`AgentSessionsTests`:

- A dead claimant's note becomes an exit record keyed by its session; a live one is untouched.
- A live pid under a different start token is reused and yields an exit record; a live pid whose
  token cannot be compared keeps its note and yields none.
- `Liveness` returns `Running` for a live claim, `Exited` for an exit record with no live claim,
  `Running` when both exist, `Unknown` for neither. A note whose token cannot be compared is
  `Running` even beside an exit record; a new claim deletes the exit record.
- A claim note or exit record in another case is found for the lower-case canonical id.
- Exit records older than 30 days are reaped; `Claimants()` ignores the `exited` directory.

`SessionTakeoverTests` (WireMock):

- Attaches every work item and every unfinished plan; finished plans and plans with no open task are
  listed as skipped.
- Task choice: `in_progress` wins over an earlier `pending`; the current status is sent unchanged.
- X's current plan is adopted last.
- Refusals: self-continue, X not found, `Running`, `Unknown` + active + recent; `force` overrides
  the last two.
- `Exited` proceeds even when the server says active and recent.
- A 403 `work_items_not_in_plan` reports work items as `not_in_plan` and still adopts the plans.
- A failed write is reported on its entry and fails the takeover; so does a failed or malformed
  read. A 401 on a follow-up read is `Unauthorized` with nothing written.
- A partial task is passed over; a plan whose open tasks are all partial or user-set is
  `not_adoptable`. A full page of plans sets `plans_truncated`.
- X's primary work item is declared last.

`RecapCommand` tests: `--continue` prints the `## Continued` block before the recap; a refusal exits
2 with no recap; `--continue` without a session id, or with `--repo`, is a usage error.

`McpHandoffServerTests`: `tools/list` advertises `continue_session` only; a refusal is a tool error;
the outcome is returned as JSON.

`McpSessionsServerTests`: `work_items` projected when present, omitted when empty or when the read
fails.

`KcapMcpRegistryReviewFlowTests` needs no change: `kcap-sessions` gains no tool.
