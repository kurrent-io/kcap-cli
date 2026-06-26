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
start_review_flow(kind, context)
  → reviewer returns FINDINGS: … | NO FINDINGS

if FINDINGS:
  address each finding
  submit_review_round(flow_run_id, updated_context)
    → repeat until NO FINDINGS

close_review_flow(flow_run_id)
report completion to user
```

## Tool reference

| Tool | When to call |
|---|---|
| `start_review_flow` | Once, at the start of a review task. Pass `kind` (`spec-review` or `code-review`) and the content to review. |
| `submit_review_round` | After addressing findings. Pass the same `flow_run_id` and the updated content. |
| `close_review_flow` | Only after the reviewer returns `NO FINDINGS`. |

## Example (code review)

```
# Step 1 — start
start_review_flow(kind="code-review", context="<diff or file contents>")

# Step 2 — reviewer returns FINDINGS: missing null check on line 42
# Fix the code, then:
submit_review_round(flow_run_id="<id>", context="<updated diff>")

# Step 3 — reviewer returns NO FINDINGS
close_review_flow(flow_run_id="<id>")
# Report to user: review complete, all findings resolved
```
