---
name: agent-flows
description: >-
  This skill should be used ONLY when the user explicitly asks to run a
  structured agent *flow* by name or definition id — e.g. "start a flow",
  "run the code-review flow", "run the X flow", "use flow definition X",
  "kick off an agent flow", or wants an iterative loop run by a separate
  hosted participant agent that continues until sign-off. It covers the same
  underlying tools as the `review-flows` skill (`start_review_flow` etc. are
  aliases of the generic tools documented here) — use `review-flows` for the
  two built-in review kinds (`spec-review`, `code-review`) and this skill for
  any other flow definition, or when the user names a `definition_id`
  explicitly. Do NOT use this skill (and do NOT call the flows MCP tools) for
  an ordinary request such as "review my PR", "do X for me", or "check this
  over" where the user just wants you to do the work yourself — perform that
  work directly instead.
---

# Agent Flows

Use the `kcap mcp flows` MCP tools (`start_flow`, `send_to_participant`, `get_flow_status`, `close_flow`) to run a structured agent **flow**: your work is handed to a **separate, hosted participant agent** driven by a flow definition from the server's catalog, which returns a result (kind `findings` with the participant's result text, or `clean`); you address a `findings` result and keep iterating until the clean signal. This is a deliberate, heavier workflow — use it only when the user explicitly opts into it.

## When NOT to use this skill / these tools

These tools do **not** perform the work themselves — they hand it off to a separate hosted participant agent running a named flow definition. If the user simply asked *you* to do something in a normal session — e.g. "review my PR", "review this diff", "check this spec", "do X" — just do it yourself and report the result directly. Do **NOT** call `start_flow` / `send_to_participant` for an ordinary request; that would spin up a hosted agent the user did not ask for.

Only start a flow when the user explicitly asks for a flow — e.g. "start a flow", "run the code-review flow", "use flow definition X", or "re-review after I address the findings" via a flow.

## Choosing the flow definition

Once the user has explicitly opted into a flow (see above), pick the `definition_id`:

- Spec or design document → `definition_id: "spec-review"` (built-in; same as `review-flows`' `spec-review` kind)
- Code changes or a pull request → `definition_id: "code-review"` (built-in; same as `review-flows`' `code-review` kind)
- Anything else → the definition id the user named, or one you look up in the server's flow-definition catalog at `/admin/flows`. If you're unsure which definition applies, ask the user rather than guessing.

## If the flows MCP tools are not loaded

If `start_flow` / `send_to_participant` are not among the tools available in this session, do NOT try to obtain them:

- Do NOT run `kcap mcp flows` from a shell, do NOT handshake it over stdio/JSON-RPC, and do NOT edit any MCP configuration.
- The absence is deliberate: hosted flow participants run with all MCP servers stripped, so a participant cannot start a nested flow.
- If you were asked to do work and these tools are absent, you are most likely the hosted participant inside an existing flow. This skill does not apply to you — skip the workflow below entirely. Perform the requested work directly, then deliver your result by calling the `submit_review_result` tool (from the injected `kcap-flow-result` server) exactly as the "Result contract" section of your prompt instructs, quoting its round token — `kind: "findings"` plus your result text, or `kind: "clean"`. The tool is the ONLY delivery channel: the server does not read your reply text, so result markers in your final message deliver nothing and the round would sit unresolved until its timeout. If the tool call fails, retry it.

## Core rules

1. **Start exactly one flow per user task.** Call `start_flow` once and hold the returned `flow_run_id`. Do NOT start a new flow for follow-up rounds — reuse the same ID.
2. **After receiving a non-clean result**, address it, then call `send_to_participant` with the same `flow_run_id` and the updated message.
3. **Do NOT finish the user task while the flow has unresolved results.** Keep iterating until the definition's clean/complete signal.
4. **Only call `close_flow` after the clean signal.** The run stays open until you explicitly close it — don't rely on it closing itself. Then report completion to the user.
5. **If participant output is unclear or requires user input**, pause and ask the user before proceeding.
6. **Never start a nested flow.** If you are the hosted participant (see above), do not call these tools yourself.
7. **Single participant.** In Phase D every flow definition has exactly one participant, `reviewer`. `send_to_participant` with any other `participant` value is rejected by the server, which names the valid participant in its error.
8. **For a code review flow (`definition_id: "code-review"`), do NOT ask the participant to run tests.** CI covers test execution; participant feedback is on correctness, design, and adherence to conventions.
9. **State where your changes live.** The participant's worktree is mirrored from the working tree you LAUNCHED from (your cwd's git root) — nothing else. If any part of the changeset lives elsewhere (another git worktree, another repository, a different machine) or is not in that tree, say so explicitly in `context`/`message` and inline the relevant diffs or file contents — or pass `mode: "context-only"` so the participant treats your context as the sole source of truth. The participant is instructed to flag referenced changes it cannot find in its worktree; incomplete context wastes a full round.

## Guardrail errors

The server enforces per-run budgets; watch for these in tool error responses:

- **`400` containing `max_rounds (N) reached for this run — close the flow.`** — the run is still **open**, it's just hit its round cap. Stop submitting further rounds, summarize what you have, and call `close_flow`.
- **`400` containing `budget_exceeded: …`** — the run has **already failed** and the participant agent has stopped. Report this to the user; do NOT retry and do NOT call `close_flow` — closing a failed run overwrites the failure status in the read model (the projector flips `failed` → `closed`), hiding what went wrong.
- **A round that exceeds the definition's `round_timeout`** lands as a terminal **`unclear`** round, with the timeout explained in its result text — if you check round status programmatically, look for `unclear` and read the text for the timeout reason. The run itself stays open — you may submit another round or close the flow.
- **Idle runs are auto-reaped** after the definition's `idle_ttl` (server default 24h). Don't rely on this — always call `close_flow` yourself once you're done, whether the outcome was clean or you're abandoning the task.
- **`400` starting `no_daemon_available:`** — no connected daemon has the repo checked out. Tell the user to run `kcap agent` on a machine with the repo cloned (or pass an explicit `daemon_name` + `repo_path`).
- **`400` starting `daemon_outdated:`** — the daemon's kcap is too old to host flow participants. Tell the user to update (`npm i -g @kurrent/kcap`) and restart `kcap agent`.
- **`400` starting `participant_unavailable:`** — the participant agent died and automatic relaunch is not available yet. Close this flow and start a new one, carrying your context forward; re-submitting will keep failing.
- **A round result of `unclear` whose text is exactly `participant_died` or `participant_stopped`** — the participant agent crashed or was stopped mid-round. The run stays open but has no live participant: close the flow and start a new one.

## Workflow

```
start_flow(definition_id, target_kind, target_ref, target_title, context)
  → participant returns a result: kind findings (with the result text) | kind clean

if clean:
  close_flow(flow_run_id)
  report completion to user
  DONE

if findings:
  address the result
  send_to_participant(flow_run_id, participant="reviewer", message=…)
    → repeat until clean
  close_flow(flow_run_id)
  report completion to user
```

## Tool reference

| Tool | Required args | Optional args | When to call |
|---|---|---|---|
| `start_flow` | `definition_id` (e.g. `spec-review`, `code-review`, or a custom catalog id), `target_kind` (what is being worked on: `spec`, `code`, `pr`, `branch`, `file`, etc.), `target_ref` (a path, branch name, or PR URL/number that identifies the target), `target_title` (short human-readable title), `context` (background context: what to focus on, constraints, definition of done) | `instructions`, `mode` (`context-only` — optional; by default, on the same machine, the participant's worktree is mirrored from your working tree including uncommitted changes, so it reads the actual source. Pass `context-only` to opt out and treat the submitted context as authoritative) | Once, at the start of a flow task. |
| `send_to_participant` | `flow_run_id`, `participant` (Phase D flows have a single participant: `reviewer`), `message` | `instructions`, `async` (defaults to `true`) | After addressing a non-clean result. Pass the same `flow_run_id` and the updated message. |
| `get_flow_status` | `flow_run_id` | — | Poll or check the current status of a flow run (running, waiting, completed, failed). |
| `close_flow` | `flow_run_id` | — | Only after the definition's clean signal — or when abandoning the task early; the run otherwise stays open until closed. |

## Example (custom definition)

```
# Step 1 — start (all five required args must be provided; on the same machine the participant sees
# your working tree, uncommitted changes included — pass mode="context-only" to opt out)
start_flow(
  definition_id="code-review",
  target_kind="branch",
  target_ref="feature/add-null-check",
  target_title="Add null check on user input",
  context="Review the diff on this branch for correctness and adherence to project conventions."
)
# → returns flow_run_id, e.g. "flow_abc123"
# → participant returns kind findings: missing null check on line 42

# Step 2 — address findings, then send a follow-up to the reviewer participant
send_to_participant(
  flow_run_id="flow_abc123",
  participant="reviewer",
  message="Fixed null check on line 42. Updated diff attached."
)

# Step 3 — participant returns kind clean
close_flow(flow_run_id="flow_abc123")
# Report to user: flow complete, all findings resolved
```
