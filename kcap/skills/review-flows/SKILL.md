---
name: review-flows
description: >-
  This skill should be used ONLY when the user explicitly asks to run a
  structured review *flow* — e.g. "start a review flow", "submit this for
  review", "re-review after I address findings", or wants an iterative review
  loop run by a separate reviewer that continues until sign-off. Do NOT use
  this skill (and do NOT call the flows MCP tools) for an ordinary review
  request such as "review my PR", "review this diff/spec/design", or "code
  review" where the user just wants you to review it yourself — perform that
  review directly instead.
---

# Review Flows

Use the `kcap mcp flows` MCP tools (`start_review_flow`, `submit_review_round`, …) to run a structured review **flow**: your work is submitted to a **separate, hosted reviewer** agent, which returns findings; you address them and keep iterating until the reviewer returns `NO FINDINGS`. This is a deliberate, heavier workflow — use it only when the user explicitly opts into it.

## When NOT to use this skill / these tools

These tools do **not** perform a review — they hand the work off to a separate hosted reviewer. If the user simply asked *you* to review something in a normal session — e.g. "review my PR", "review this diff", "code review this", "look over this spec" — just perform the review yourself and report your findings directly. Do **NOT** call `start_review_flow` / `submit_review_round` for an ordinary review request; that would spin up a hosted reviewer the user did not ask for.

Only start a flow when the user explicitly asks for a review *flow* — e.g. "start a review flow", "submit this for review", "get an independent review", or "re-review after I address the findings".

## Choosing the flow kind

Once the user has explicitly opted into a flow (see above), pick the `kind`:

- Spec or design document → `kind: "spec-review"`
- Code changes or a pull request → `kind: "code-review"`

## Prerequisites

A flow hands your work to a **hosted reviewer** the daemon launches for you (its vendor and model come from the flow definition — you don't pick or configure the reviewer). For `start_review_flow` to succeed, two things must be true:

1. You are logged in (`kcap login`).
2. A `kcap` daemon is running with **this** repo checked out — that daemon hosts the reviewer.

If `start_review_flow` errors, do **not** retry blindly — the two cases differ:

- **No daemon** — tell the user to start one with the target repo checked out (`kcap daemon start -d`), then start the flow again.
- **Ambiguous match** ("multiple daemons/checkouts match this repo") — retrying won't help: the tool exposes no `daemon_name`/`repo_path`, so it can't disambiguate when several checkouts of the repo are registered (e.g. multiple git worktrees). Surface this to the user rather than looping (tracked in AI-1112).

## If the flows MCP tools are not loaded

If `start_review_flow` / `submit_review_round` are not among the tools available in this session, do NOT try to obtain them:

- Do NOT run `kcap mcp flows` from a shell, do NOT handshake it over stdio/JSON-RPC, and do NOT edit any MCP configuration.
- The absence is deliberate: hosted review-flow reviewers run with all MCP servers stripped, so a reviewer cannot start a nested flow.
- If you were asked to review a spec, design, or code and these tools are absent, you are most likely the hosted reviewer inside an existing flow. This skill does not apply to you — skip the workflow below entirely. Perform the requested review directly and end with a final message that starts with `FINDINGS:` (followed by your findings) or `NO FINDINGS`. Your final message is captured automatically; no tool call is needed to deliver it.

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
| `start_review_flow` | `kind` (`spec-review`\|`code-review`), `target_kind` (what is being reviewed: `spec`, `code`, `pr`, `branch`, `file`, etc.), `target_ref` (a path, branch name, or PR URL/number that identifies the target), `target_title` (short human-readable title, e.g. spec name or PR title), `context` (background context: what to focus on, constraints, definition of done) | `instructions`, `mode` (`context-only` — required for code-review unless the reviewer runs in your exact repo checkout) | Once, at the start of a review task. |
| `submit_review_round` | `flow_run_id`, `context` | `instructions` | After addressing findings. Pass the same `flow_run_id` and the updated context. |
| `get_review_flow_status` | `flow_run_id` | — | Poll or check the current status of a flow (running, waiting, completed, failed). |
| `close_review_flow` | `flow_run_id` | — | Only after the reviewer returns `NO FINDINGS`. |

## Example (code review)

```
# Step 1 — start (all five required args must be provided; mode=context-only is required for code-review)
start_review_flow(
  kind="code-review",
  target_kind="branch",
  target_ref="feature/add-null-check",
  target_title="Add null check on user input",
  context="Review the diff on this branch for correctness and adherence to project conventions.",
  mode="context-only"
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
