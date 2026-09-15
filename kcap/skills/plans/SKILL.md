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
   and `path`. The file is read locally, hashed, and keyed against the
   repository root; declaring the same file again from a later session lands
   on the same plan. When a plan implements a spec, or a spec refines a design,
   pass the other file as `argues_from` so both attach to one plan.
2. **When a plan has discrete steps, declare them and update each transition.**
   Call `set_plan_tasks` with the whole ordered list (`title`, optional
   `task_id`, `status`, `note`); re-send the whole list when the steps change,
   carrying the `task_id`s you were given. Call `update_plan_task` every time a
   task starts, finishes or is skipped, by `task_id` or 1-based `ordinal`, with
   `status` `pending`, `in_progress`, `completed` or `skipped` and a `note`
   when the reason matters. After compaction, `get_plan` returns the list.
3. **Keep whatever ledger your own workflow asks for as well.** These tools
   replace the harness's task list, not your notes or any file another
   workflow tells you to maintain.

## Tool reference

| Tool | Required args | Purpose |
|---|---|---|
| `declare_plan_document` | `kind`, `path` (+ `argues_from`, `work_item_id`, `session_id`) | Declare the document; returns `plan_id`, `document_key`, `created`. |
| `set_plan_tasks` | `tasks` (+ `plan_id`, `session_id`) | Replace the declared task list; returns tasks with ids and ordinals. |
| `update_plan_task` | `status` and one of `task_id` / `ordinal` (+ `note`, `plan_id`) | Record one transition; the result names the plan. |
| `get_plan` | — (+ `plan_id`, `session_id`) | Documents, tasks with status and source, progress. |

Without `plan_id` every tool acts on the session's current plan — the one it
most recently wrote to; a session on no plan gets one created by
`set_plan_tasks` and an empty result from `get_plan`. If a tool cannot resolve
the session, pass `session_id` explicitly.

## Requirements

Requires `kcap login`. The `kcap-plans` MCP server is registered for every
supported harness by `kcap setup` and `kcap plugin install`.
