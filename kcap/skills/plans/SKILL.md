---
name: plans
description: >-
  This skill should be used whenever you write or read a plan, spec or design
  document, when you start executing a plan, and whenever a task list, todo
  list or checklist comes up — yours or the user's. It says how to record the
  plan and its tasks in Kurrent Capacitor through the `kcap mcp plans` MCP
  tools, so progress shows in the session view and survives context
  compaction.
---

# Plans — declaring the document and its tasks

Capacitor keeps its own record of the plan a session executes: the document,
the ordered task list, and each task's status. It is written only by you,
through the `kcap-plans` MCP tools; nothing infers it. Three rules:

1. **When you write or are handed a plan, spec or design document, declare it.**
   Call `declare_plan_document` with its `kind` (`plan`, `spec` or `design`)
   and `path`. The file is read locally, hashed, and keyed against the repository root
   **and the checkout it sits in**: declaring the same file again from the same
   checkout lands on the same plan, but from a different worktree or clone it
   starts a separate one. To continue a plan another session began, see
   "Resuming a plan" below — do not re-declare its document. When a plan
   implements a spec, or a spec refines a design, pass the other file as
   `argues_from` so both attach to one plan.
2. **When a plan has discrete steps, declare them and update each transition.**
   Call `set_plan_tasks` with the whole ordered list (`title`, optional
   `task_id`, `status`, `note`); re-send the whole list when the steps change,
   carrying the `task_id`s you were given. Call `update_plan_task` every time a
   task starts, finishes or is skipped, by `task_id` or 1-based `ordinal`, with
   `status` `pending`, `in_progress`, `completed` or `skipped` and a `note`
   when the reason matters. After compaction, `get_plan` returns the list.
   While a plan's `is_complete` is `false`, never call `set_plan_tasks`
   on it for any reason — the list would be rebuilt from a view that is
   missing tasks.
3. **Keep whatever ledger your own workflow asks for as well.** These tools
   replace the harness's task list, not your notes or any file another
   workflow tells you to maintain.

## Resuming a plan

To continue a plan that an earlier session left unfinished — yours or a teammate's. Finding and reading use the `kcap-sessions` tools; the only writes are the reconciling snapshot in step 3, when one is needed, and the adoption in step 5, always last.

1. **Find it.** `list_repo_plans` lists this repository's open plans. Read each row's `sessions` together: a session that is `active`, not `stale`, and whose `last_touched_at` is recent may still be executing the plan — **ask the user before adopting it**. An active session whose `last_touched_at` is old has most likely moved to other work; a session stays attached to every plan it ever touched, so that alone is no reason to hold back.
2. **Read it.** `get_declared_plans(plan_id: …)` for the documents and the full task list.
3. **Compare the document.** Read the plan file from *this* checkout at its repo-relative `path` and compare it with `content_hash` — the lowercase hex SHA-256 of the file's bytes, as `shasum -a 256` prints it. If it changed, the task list may be out of date. Reconciling it means sending a new snapshot with `set_plan_tasks(plan_id: …)`, and a snapshot **replaces** the list — a task it omits is deleted, a note it omits is cleared:
   - **`is_complete` is `true`:** reconcile now, **before** step 5, carrying every existing task's `task_id`, `status` and `note` over from step 2. Sent after step 5, the snapshot would carry the resumed task's earlier `pending` status and put it straight back.
   - **`is_complete` is `false`: never send a snapshot.** Your view is missing someone else's tasks or notes, and a list rebuilt from it would destroy them. Carry on with `update_plan_task`, which changes one task and nothing else, and tell the user the document changed and the list could not be reconciled from your view.
4. **Verify before continuing.** The ledger records what the earlier session *claimed*. A task left `in_progress` may be half-written: check the working tree and the history since the document's `commit_sha`, when it is set, before picking it up.
5. **Adopt it.** `update_plan_task(plan_id: …, task_id: …, status: "in_progress")` on the task you are resuming. That attaches this session to the plan and makes it the session's current plan, so later calls can omit `plan_id`. This is always the last write of the procedure.

**Do not call `declare_plan_document` for that plan's file while resuming from a different checkout** — a new worktree, another clone. It would start a second plan, point this session at it, and split the ledger: your progress would land on an empty plan while the original stops moving. Rule 1's "declare a document when you read it" does not apply here. From the same checkout path, declaring it again is harmless.

## Tool reference

| Tool | Required args | Purpose |
|---|---|---|
| `declare_plan_document` | `kind`, `path` (+ `argues_from`, `work_item_id`, `session_id`) | Declare the document; returns `plan_id`, `document_key`, `created`. |
| `set_plan_tasks` | `tasks` (+ `plan_id`, `session_id`) | Replace the declared task list; returns tasks with ids and ordinals. |
| `update_plan_task` | `status` and one of `task_id` / `ordinal` (+ `note`, `plan_id`) | Record one transition; the result names the plan. |
| `get_plan` | — (+ `plan_id`, `session_id`) | Documents, tasks with status and source, progress. |

To read a plan from *another* session, or to find unfinished plans in the repository, use `get_declared_plans` and `list_repo_plans` on the `kcap-sessions` server: they are read-only and do not prompt.

Without `plan_id` every tool acts on the session's current plan — the one it
most recently wrote to; a session on no plan gets one created by
`set_plan_tasks` and an empty result from `get_plan`. If a tool cannot resolve
the session, pass `session_id` explicitly.

## Requirements

Requires `kcap login`. The `kcap-plans` MCP server is registered for every
supported harness by `kcap setup` and `kcap plugin install`.
