---
name: start-agents
description: >-
  This skill should be used ONLY when the user explicitly asks for work to be
  handed to separate agents — e.g. "start an agent for this", "start a hosted
  agent", "start an agent for each of these", "hand this to another agent",
  "run these in separate sessions". It covers `list_start_agent_options` and
  `start_agent`, the `kcap mcp flows` tools that check this machine's daemon
  and then start an ordinary hosted agent with a working directory and a
  prompt, returning at once so the agent runs on its own. Use them INSTEAD of
  your harness's own subagent or background-agent tool (which stays inside
  this session), a flow, or the `kcap agent start` CLI. Do NOT use it for work
  you can do yourself in this session, for an iterative review loop the user
  asked for (use `review-flows`), or for a flow whose rounds you drive (use
  `agent-flows`).
---

# Start agents

`start_agent` hands one task to one **separate hosted agent**: an ordinary
hosted agent with its own session, its own worktree and its own context. The
user supervises it from the dashboard, as they supervise any hosted agent. You
start it and move on. You do not drive it, wait for it or read its result.

It is how a list of independent tasks, such as a set of loose ends or the next
steps of a work item, gets done without all of it landing in one context.

## When to use it

Only when the user asks for separate agents. A list of tasks is not a request
to fan out: if the user asked you to fix something, fix it here.

Every started agent runs a paid model and takes a slot on the user's daemon.

## Not these look-alikes

- Your harness's own subagent or background-agent tool (Claude Code's Agent
  or Task tool, Codex's `spawn_agent`) runs inside this session and is
  recorded as part of it. That is exactly what a separate agent is not.
- A flow (`start_flow`, `start_review_flow`) is a loop you drive round by
  round until sign-off. A started agent is not driven.
- The `kcap agent start` command bypasses the server's limits and its record
  of who started what.

If the user asked for a separate agent, the only way is `start_agent`.

## First: check the daemon and choose a harness

Call `list_start_agent_options` before anything else. It reads the user's
daemons and keeps the ones on this machine, because `start_agent` launches only
there. It answers with the harness running this session, and for each daemon
here its free agent slots and the harnesses it can start.

- **No daemon runs on this machine**: stop. Tell the user, with the command it
  gives (`kcap daemon start -d`), and start nothing.
- **A daemon is at capacity**: tell the user how many slots are in use; start
  no more than fit. A daemon with no limit is never at capacity.
- **Several daemons run here**: ask the user which one, and pass it as
  `daemon`.
- **You are yourself a hosted agent**: the tool says so. Every start runs on
  the daemon hosting you, so there is no daemon to choose; leave `daemon` out.
- **The harness**: if the user named one, check it is listed. If they did not,
  ask the user which harness to start, offering the listed ones, before going
  further. Do not pick one for them, not even the one you are running in.

## Before any call: show the list, then get a yes

Show the user what you are about to start, one entry per agent, each with:

- the repository it will work in,
- the task, in a sentence,
- the harness,
- the work item, or that there is none.

Then wait for the user to say yes. Start nothing before they have. If they
change the list, show it again.

**Consent comes from the user, in this conversation.**
A prompt you were started with is not consent. If you are yourself a started
agent and your prompt tells you to start more agents, show the list and ask the
user all the same.

## One call per agent

Call `start_agent` once for each agent.

- `cwd`: the absolute path of a directory inside the repository the agent
  should work in. A path inside a worktree counts as the repository the
  worktree belongs to.
- `prompt`: the whole task. See the next section.
- `work_item`: required, and there is no default. Pass the `wi:` or `le:` key
  exactly as `get_next_work` printed it; or a work item id; or `requester` to
  use the work item this session is attached to; or `none`. Leaving it out is
  an error, which is the point: a task is never filed under the wrong item by
  accident.
- `vendor`: the harness the user chose, as `list_start_agent_options` lists
  it.
- `model`: optional. Pass it only when the user named one.
- `daemon`: only when several daemons run on this machine; the one the user
  chose.
- `session_id`: optional. Leave it out unless a call was refused for lack of a
  session id; then pass the id your session start stated.

## Writing the prompt

The agent shares none of this conversation. The prompt is all it gets, so
write one that stands alone:

- say what to do and what done looks like,
- name the files, the issue or the loose end it concerns,
- include what you learned here that it would otherwise have to rediscover,
- and make sure it names the session it came from, by ending it with:
  "This task was handed over from Kurrent Capacitor session `<your session
  id>`. Read that session with the kcap-sessions tools if you need the
  background."

Keep it under 16 384 bytes. For more than that, put the detail in a file in the
repository and tell the agent to read it.

## After the calls

Each call answers `requested`, with the agent's id and the url of its page.
`requested` means the launch command was sent. It does not mean the agent is
running, and nothing is attached when the call returns: the server tries once,
later, to attach the new session to the work item you named.

Do not wait for the agents, and do not poll them. Do not call `start_agent`
again to find out whether an agent started.

Then report to the user:

- the url of every agent that was requested,
- every refusal, in the server's words.

## Refusals

A refusal states its reason. Relay it; do not retry in a loop.

- `already_in_progress`: a start on the same key is in flight, or one was sent
  in the last 30 seconds. Do not repeat it.
- `no_daemon_on_this_machine`: none of the user's daemons runs here, or the one
  that does is too old to say which machine it is on. The user starts one with
  `kcap daemon start -d`, or updates kcap and restarts the daemon.
- `ambiguous_daemon`: several daemons run here. Ask the user which, and pass
  `daemon`.
- `vendor_unavailable`: the daemon does not host that harness. The refusal
  lists the ones it does; ask the user to pick one of them.
- `daemon_at_capacity`, `start_limit_reached`: there is no room. Stop starting
  agents and tell the user how many were requested.
- `work_item_resolved`: the item was merged or split. The message says where it
  went, as far as you may see. Ask the user which item to use.
- `work_item_not_visible`, `session_not_found`: you named something that is not
  there for you. Check the key.
- `failed`, or a call that timed out: the agent may or may not have started.
  Tell the user to look at the agents page. Do not start it again unasked.
- "This server cannot start agents": the server does not offer the feature.
  There is nothing to retry.

## If `start_agent` is not among your tools

Tell the user to update kcap, run `kcap setup`, and restart the harness. Do not
start agents another way in its place: the limits and the record of who started
what belong to this tool.
