---
name: review-flows
description: >-
  This skill should be used when the user asks to "review this spec",
  "review this design", "review my PR", "code review", "get this reviewed",
  "re-review", "start a review flow", "review flow", "submit for review",
  or wants structured iterative review with findings and sign-off.
---

# Review Flows

Use `kcap mcp flows` MCP tools to run structured review loops for specs/designs and PR/code work. A review flow submits your work to a reviewer, collects findings, lets you address them, and keeps iterating until the reviewer returns `NO FINDINGS`.

## When to use

- Reviewing a spec or design document → use `kind: "spec-review"`
- Reviewing code changes or a pull request → use `kind: "code-review"`

## Core rules

1. **Start exactly one flow per user task.** Call `start_review_flow` once and hold the returned `flow_run_id`. Do NOT start a new flow for follow-up rounds — reuse the same ID.
2. **After receiving `FINDINGS:`**, address every finding, then call `submit_review_round` with the updated context and the same `flow_run_id`.
3. **Do NOT finish the user task while the flow has unresolved findings.** Keep iterating until the reviewer returns `NO FINDINGS`.
4. **Only call `close_review_flow` after `NO FINDINGS`.** Then report completion to the user.
5. **If reviewer output is unclear or requires user input**, pause and ask the user before proceeding.
6. **For code review, do NOT ask the reviewer to run tests.** CI covers test execution; reviewer feedback is on correctness, design, and adherence to conventions.

## Workflow

```
start_review_flow(kind, target_kind, target_ref, target_title, context)
  → reviewer returns FINDINGS: … | NO FINDINGS

if NO FINDINGS:
  close_review_flow(flow_run_id)
  report completion to user
  DONE

if FINDINGS:
  address each finding
  submit_review_round(flow_run_id, updated_context)
    → repeat until NO FINDINGS
  close_review_flow(flow_run_id)
  report completion to user
```

## Tool reference

| Tool | Required args | Optional args | When to call |
|---|---|---|---|
| `start_review_flow` | `kind` (`spec-review`\|`code-review`), `target_kind` (what is being reviewed: `spec`, `code`, `pr`, `branch`, `file`, etc.), `target_ref` (a path, branch name, or PR URL/number that identifies the target), `target_title` (short human-readable title, e.g. spec name or PR title), `context` (background context: what to focus on, constraints, definition of done) | `instructions`, `mode` | Once, at the start of a review task. |
| `submit_review_round` | `flow_run_id`, `context` | `instructions` | After addressing findings. Pass the same `flow_run_id` and the updated context. |
| `get_review_flow_status` | `flow_run_id` | — | Poll or check the current status of a flow (running, waiting, completed, failed). |
| `close_review_flow` | `flow_run_id` | — | Only after the reviewer returns `NO FINDINGS`. |

## Example (code review)

```
# Step 1 — start (all five required args must be provided)
start_review_flow(
  kind="code-review",
  target_kind="branch",
  target_ref="feature/add-null-check",
  target_title="Add null check on user input",
  context="Review the diff on this branch for correctness and adherence to project conventions."
)
# → returns flow_run_id, e.g. "flow_abc123"
# → reviewer returns FINDINGS: missing null check on line 42

# Step 2 — address findings, then re-submit
submit_review_round(
  flow_run_id="flow_abc123",
  context="Fixed null check on line 42. Updated diff attached."
)

# Step 3 — reviewer returns NO FINDINGS
close_review_flow(flow_run_id="flow_abc123")
# Report to user: review complete, all findings resolved
```
