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

Use the `kcap mcp flows` MCP tools (`start_review_flow`, `submit_review_round`, …) to run a structured review **flow**: your work is submitted to a **separate, hosted reviewer** agent, which returns a result (`findings` with the findings text, or `clean`); you address findings and keep iterating until the reviewer returns `clean`. This is a deliberate, heavier workflow — use it only when the user explicitly opts into it.

These four tools are aliases of the generic flow tools (`start_flow`, `send_to_participant`, `get_flow_status`, `close_flow`) — see the `agent-flows` skill for non-review flows.

## When NOT to use this skill / these tools

These tools do **not** perform a review — they hand the work off to a separate hosted reviewer. If the user simply asked *you* to review something in a normal session — e.g. "review my PR", "review this diff", "code review this", "look over this spec" — just perform the review yourself and report your findings directly. Do **NOT** call `start_review_flow` / `submit_review_round` for an ordinary review request; that would spin up a hosted reviewer the user did not ask for.

Only start a flow when the user explicitly asks for a review *flow* — e.g. "start a review flow", "submit this for review", "get an independent review", or "re-review after I address the findings".

## Choosing the flow kind

Once the user has explicitly opted into a flow (see above), pick the `kind`:

- Spec or design document → `kind: "spec-review"`
- Code changes or a pull request → `kind: "code-review"`

## If the flows MCP tools are not loaded

If `start_review_flow` / `submit_review_round` are not among the tools available in this session, do NOT try to obtain them:

- Do NOT run `kcap mcp flows` from a shell, do NOT handshake it over stdio/JSON-RPC, and do NOT edit any MCP configuration.
- The absence is deliberate: hosted review-flow reviewers run with all MCP servers stripped, so a reviewer cannot start a nested flow.
- If you were asked to review a spec, design, or code and these tools are absent, you are most likely the hosted reviewer inside an existing flow. This skill does not apply to you — skip the workflow below entirely. Perform the requested review directly, then deliver your result by calling the `submit_review_result` tool (from the injected `kcap-flow-result` server) exactly as the "Result contract" section of your prompt instructs, quoting its round token — `kind: "findings"` plus your findings text, or `kind: "clean"`. The tool is the ONLY delivery channel: the server does not read your reply text, so ending with `FINDINGS:`/`NO FINDINGS` markers delivers nothing and the round would sit unresolved until its timeout. If the tool call fails, retry it.

## Core rules

1. **Start exactly one flow per user task.** Call `start_review_flow` once and hold the returned `flow_run_id`. Do NOT start a new flow for follow-up rounds — reuse the same ID.
2. **After receiving a `findings` result**, address every finding, then call `submit_review_round` with the updated context and the same `flow_run_id`.
3. **Do NOT finish the user task while the flow has unresolved findings.** Keep iterating until the reviewer returns `clean`.
4. **Only call `close_review_flow` after a `clean` result.** Then report completion to the user.
5. **If reviewer output is unclear or requires user input**, pause and ask the user before proceeding.
6. **For code review, do NOT ask the reviewer to run tests.** CI covers test execution; reviewer feedback is on correctness, design, and adherence to conventions.
7. **State where your changes live.** The reviewer's worktree is mirrored from the working tree you LAUNCHED from (your cwd's git root) — nothing else. If any part of the changeset lives elsewhere (another git worktree, another repository, a different machine) or is not in that tree, say so explicitly in `context` and inline the relevant diffs or file contents — or pass `mode: "context-only"` so the reviewer treats your context as the sole source of truth. The reviewer is instructed to flag referenced changes it cannot find in its worktree; incomplete context wastes a full round.

## Server errors to act on

- **`400` starting `no_daemon_available:`** — no connected daemon has the repo checked out. Tell the user to run `kcap agent` on a machine with the repo cloned (or pass an explicit `daemon_name` + `repo_path`).
- **`400` starting `daemon_outdated:`** — the daemon's kcap is too old to host flow participants. Tell the user to update (`npm i -g @kurrent/kcap`) and restart `kcap agent`.
- **`400` starting `participant_unavailable:`** — the reviewer agent died and automatic relaunch is not available yet. Close this flow and start a new one, carrying your context forward; re-submitting will keep failing.
- **A round result of `unclear` whose text is exactly `participant_died` or `participant_stopped`** — the reviewer agent crashed or was stopped mid-round. The run stays open but has no live reviewer: close the flow and start a new one.

## Workflow

```
start_review_flow(kind, target_kind, target_ref, target_title, context)
  → reviewer returns a result: findings (with the findings text) | clean

if clean:
  close_review_flow(flow_run_id)
  report completion to user
  DONE

if findings:
  address each finding
  submit_review_round(flow_run_id, updated_context)
    → repeat until clean
  close_review_flow(flow_run_id)
  report completion to user
```

## Tool reference

| Tool | Required args | Optional args | When to call |
|---|---|---|---|
| `start_review_flow` | `kind` (`spec-review`\|`code-review`), `target_kind` (what is being reviewed: `spec`, `code`, `pr`, `branch`, `file`, etc.), `target_ref` (a path, branch name, or PR URL/number that identifies the target), `target_title` (short human-readable title, e.g. spec name or PR title), `context` (background context: what to focus on, constraints, definition of done) | `instructions`, `mode` (`context-only` — optional; by default, on the same machine, the reviewer's worktree is mirrored from your working tree including uncommitted changes, so it reads the actual source. Pass `context-only` to opt out and treat the submitted context as authoritative) | Once, at the start of a review task. |
| `submit_review_round` | `flow_run_id`, `context` | `instructions` | After addressing findings. Pass the same `flow_run_id` and the updated context. |
| `get_review_flow_status` | `flow_run_id` | — | Poll or check the current status of a flow (running, waiting, completed, failed). |
| `close_review_flow` | `flow_run_id` | — | Only after the reviewer returns `clean`. |

## Example (code review)

```
# Step 1 — start (all five required args must be provided; on the same machine the reviewer sees
# your working tree, uncommitted changes included — pass mode="context-only" to opt out)
start_review_flow(
  kind="code-review",
  target_kind="branch",
  target_ref="feature/add-null-check",
  target_title="Add null check on user input",
  context="Review the diff on this branch for correctness and adherence to project conventions."
)
# → returns flow_run_id, e.g. "flow_abc123"
# → reviewer returns kind findings: missing null check on line 42

# Step 2 — address findings, then re-submit
submit_review_round(
  flow_run_id="flow_abc123",
  context="Fixed null check on line 42. Updated diff attached."
)

# Step 3 — reviewer returns kind clean
close_review_flow(flow_run_id="flow_abc123")
# Report to user: review complete, all findings resolved
```
