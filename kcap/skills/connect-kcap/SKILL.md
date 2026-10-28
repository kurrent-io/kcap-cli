---
name: connect-kcap
description: >-
  This skill should be used when the user asks to "connect kcap", "set up
  kcap", "install kcap", "log in to kcap", "reconnect kcap", or pastes the
  npm package name @kurrent/kcap. Also recognizes the former name Capacitor,
  including "connect Capacitor" and "log in to Capacitor". Use when a kcap tool
  or skill fails because `kcap` is not installed or not signed in. Installs the
  kcap CLI and signs it in. On a Muse Assistant VM it also registers kcap's
  MCP servers as a custom connector, writes the `kcap` skill and keeps the
  sign-in warm with an hourly cron.
---

# Connect kcap

kcap records a team's coding-agent sessions and serves them back as
tools: search past sessions, team memory, analytics, work items, plans,
knowledge and shareable pages. `kcap` is its CLI. This skill installs `kcap`
and signs it in; what happens after that depends on the machine.

**Where am I?**

```bash
[ "$HOME" = /home/hatch ] && [ -d "$HOME/workspace" ] && [ -d /opt/hatch-image/bin ] && echo muse-assistant
```

- Prints `muse-assistant` → follow **Part B** (the Muse Assistant walkthrough).
- Prints nothing → follow **Part A** (any other machine), then stop.

Never print the contents of `~/.config/kcap/` — it holds the sign-in tokens.
Never start `kcap daemon` unless the user asks for hosted agents.

## Part A — any other machine

1. Check what is there: `command -v kcap && kcap whoami`. A `Server:` line
   naming the workspace the user wants (or any, if they named none) plus
   `Server:   accepts this token` means nothing to do; say so and stop.
   No `kcap` on PATH means step 2, then step 3; anything else, step 3.
2. Install: requires Node.js 18+ (`node -v`). `npm install -g @kurrent/kcap`,
   then `command -v kcap` must print a path (otherwise add `$(npm prefix -g)/bin`
   to PATH in the shell profile — the hooks run `kcap` by name).
3. Sign in: ask the user for their kcap server URL (for example
   `https://acme.kcap.ai`) unless they gave it, then run `kcap setup --server-url <url>`.
   With no browser on this machine add `--device`: it prints a URL and a short
   code — **show both to the user** and ask them to approve on their own device;
   the command finishes once they do. Let setup detect the installed coding
   agents and install their hooks; that is what records sessions here.
4. Confirm with `kcap status` (`Auth: … ✓ token valid`). Done.

## Part B — Muse Assistant

### What the user will see, and must do

First ask for the workspace address (for example `https://acme.kcap.ai`) if the
user has not given one. Never guess a tenant.

Then say this to the user, naming the workspace's own host:

> Connecting kcap needs a few approvals from you in this chat. Each time I
> first contact a new host, a card appears here with **Allow once / Always allow
> this site / Deny**. Please choose **Always allow this site** on each. The first
> two are your workspace (`<its host>`) and `phog.kurrent.io` (kcap's anonymous
> usage proxy); the sign-in then adds `api.workos.com` and `signin.kcap.ai`, or
> `github.com` for a GitHub workspace. After that I will show you a short code to
> approve on your own device.

Setup prints the exact hosts as it reaches each stage; relay them to the user.
I do not see the cards. If a command below fails with "cannot reach" or asks for
a card to be approved, the user has not approved that host yet: ask them to look
for the card, then run the **same command again** unchanged.

### B1. Check what is already there

```bash
KCAP=~/workspace/kcap/node_modules/.bin/kcap
[ -x "$KCAP" ] && "$KCAP" whoami
```

B2 and B3 are done only when `whoami` prints a `Server:` line naming the
workspace the user asked for **and** a `Server:   accepts this token` line —
the workspace itself checked the sign-in. Then continue at B4 and only create
what is missing. A different server, `could not verify` (on this VM, usually a
card not yet approved), `REJECTS this token`, or no sign-in at all means B3
still runs.

### B2. Install under the persistent workspace

Only `~` survives a VM replacement; the global npm prefix is `/usr` and is
wiped, so never `npm install -g` here.
If `npm install` cannot reach `registry.npmjs.org`, that host needs its card
approved too.

```bash
mkdir -p ~/workspace/kcap ~/workspace/capacitor
npm install --prefix ~/workspace/kcap @kurrent/kcap
~/workspace/kcap/node_modules/.bin/kcap --version
```

### B3. Sign in by device code

The workspace address goes into a shell command, and the sign-in travels to it,
so check it first: it must be `https://` followed only by letters, digits, `.`
and `-`, with an optional `:port` and nothing after it. `http://` is acceptable
only for `localhost` or `127.0.0.1`. Refuse anything else — plain `http://` to
a remote host, quotes, spaces, `$`, `;` or a path — and ask the user for the
plain `https://` address.

Run setup in the background and read its output while it waits for the user:

```bash
KCAP=~/workspace/kcap/node_modules/.bin/kcap
SERVER='<the checked workspace address>'
nohup "$KCAP" setup --server-url "$SERVER" --device --no-prompt --muse-assistant \
  > ~/workspace/capacitor/setup.log 2>&1 &
sleep 8; cat ~/workspace/capacitor/setup.log
```

`--muse-assistant` installs nothing into Codex or any other agent on this image
(Muse does not use them), makes every session this install records private by
default, lists the hosts above before its first request, and treats an
unreachable server as "not yet approved".

- The log first lists the hosts, and once the server answers, the sign-in
  hosts. If it then says **Cannot reach server** and **not yet approved**, or
  sign-in fails while a card is waiting: ask the user to approve it, then run
  the same command again.
- If the user declined the code, or it expired before they entered it, the log
  says **Login failed**: run the same command again, which starts a fresh
  sign-in, and show the user the new URL and code.
- Once the server answers, the log shows a **verification URL and a code**.
  **Show both to the user** and ask them to open the URL on their own device and
  enter the code. Keep reading the log (`sleep 15; tail -20 …`) until it reaches
  `Setup complete`.
- Confirm: `"$KCAP" status` → `Auth: <name> ✓ token valid`. Tokens live under
  `~/.config/kcap/` and persist; never paste them into the chat.

If sign-in lapses later (`kcap status` reports an expired or missing token):
`"$KCAP" login --device`, show the URL and code again.

### B4. Register kcap as a custom connector

kcap's tools are stdio MCP servers started by `kcap mcp <name>`. Register
them as one custom connector at `~/workspace/custom-connectors/custom.kcap/`.
The directory holds `connector.yaml`, `CONNECTOR.md`, and a stdio helper
`mcp_call.py` — write the helper yourself, the way other custom connectors on
this VM do: spawn the command with its args, speak JSON-RPC 2.0 over
newline-delimited stdin/stdout, send `initialize` (protocol version
`2024-11-05`, then `notifications/initialized`), then `tools/list` or
`tools/call`, with a 60-second timeout and the result printed as JSON.
If the connector already exists, bring `connector.yaml` and `CONNECTOR.md` up to
date with the text below; keep a helper that works.
For tool calls, accept `-` as the third argument and read the JSON arguments
from stdin with `json.load(sys.stdin)`. Start the MCP server with an argv list
and `shell=False`; never interpolate JSON values into a shell command.

`connector.yaml` — if another custom connector already exists here, copy its
field names; these are the servers, commands and args:

```yaml
name: kcap
description: >-
  kcap: the team's recorded coding-agent sessions, team memory,
  analytics, work items, plans, curated knowledge and shareable pages.
servers:
  kcap-sessions:
    transport: stdio
    command: /home/hatch/workspace/kcap/node_modules/.bin/kcap
    args: [mcp, sessions]
  kcap-memory:
    transport: stdio
    command: /home/hatch/workspace/kcap/node_modules/.bin/kcap
    args: [mcp, memory]
  kcap-analytics:
    transport: stdio
    command: /home/hatch/workspace/kcap/node_modules/.bin/kcap
    args: [mcp, analytics]
  kcap-workitems:
    transport: stdio
    command: /home/hatch/workspace/kcap/node_modules/.bin/kcap
    args: [mcp, workitems]
  kcap-plans:
    transport: stdio
    command: /home/hatch/workspace/kcap/node_modules/.bin/kcap
    args: [mcp, plans]
  kcap-knowledge:
    transport: stdio
    command: /home/hatch/workspace/kcap/node_modules/.bin/kcap
    args: [mcp, knowledge]
  kcap-pages:
    transport: stdio
    command: /home/hatch/workspace/kcap/node_modules/.bin/kcap
    args: [mcp, pages]
```

`CONNECTOR.md`:

```markdown
# kcap

Seven stdio MCP servers over the team's kcap workspace. Call a tool with
`python3 mcp_call.py <server> <tool> - < args.json`, where `args.json` contains
the JSON arguments. Pass JSON through stdin rather than shell quoting it.
List tools with
`python3 mcp_call.py <server> tools/list`.

| Server | Use it for |
|---|---|
| kcap-sessions | `search_sessions`, `list_repo_sessions`, `get_session_summary`, `list_turns`, `get_turn`, `get_session_transcript`, `list_repo_plans`, `get_declared_plans`, `get_session_evals` |
| kcap-memory | `search_memories`, `get_memory`, `save_memory`, `update_memory`, `rescope_memory`, `archive_memory`; for a log, `append_memory_entry`, `settle_memory_entry`, `reopen_memory_entry`, `list_memory_entries` |
| kcap-analytics | `get_analytics_schema` once, then `query_analytics` (read-only SQL) |
| kcap-workitems | work items, breakdowns, loose ends, next work |
| kcap-plans | declared plan documents and tasks |
| kcap-knowledge | curated facts and skills |
| kcap-pages | `publish_page`, `list_my_pages`, `set_page_visibility`; for a page that collects answers, `get_page_results`, `await_page_responses`, `close_page_responses` |

This VM is not a git checkout: pass `"repo": "all"` to `search_sessions` and
`list_repo_sessions`, or they search nothing. A call is cut off after 60
seconds, so pass `await_page_responses` a `timeout_s` of 50 or less and call it
again to keep waiting. Sign-in is `kcap`'s own
(`~/.config/kcap/`); if a call returns 401, run `kcap status` and follow the
kcap skill's reconnect step.
```

Verify with a real call (keyword queries, one to three terms):

```bash
cd ~/workspace/custom-connectors/custom.kcap
python3 mcp_call.py kcap-sessions tools/list | head -c 600
python3 mcp_call.py kcap-sessions search_sessions - <<'KCAP_ARGS' | head -c 1200
{"query":"setup","repo":"all"}
KCAP_ARGS
```

A hit list with `hit_kind`, `session_id` and `title` fields means the connector
works.

### B5. Write the `kcap` skill

Create `~/workspace/skills/kcap/SKILL.md` with this content. If it already
exists, replace it with this content.

When upgrading, replace the former `capacitor` skill rather than keeping both.
Keep `~/workspace/capacitor` and its logs and recording data in place.

```markdown
---
name: kcap
description: >-
  Use kcap — the team's recorded coding-agent sessions, team
  memory, analytics, work items, plans, curated knowledge and shareable
  pages — through the `kcap` custom connector. Triggers: "what did my team
  work on", "what did we do last week", "find the session where", "did we
  ever", "who worked on", "recap session", "what's next", "team memory", "save
  this for the team", "how much did we spend on agents", "make this a kcap
  page", "share this as a page", and any question about past coding work.
metadata:
  includeInPrompt: true
---

# kcap

kcap's tools are the `kcap` custom connector
(`~/workspace/custom-connectors/custom.kcap/`): `python3 mcp_call.py <server>
<tool> - < args.json`. The arguments file contains JSON; do not embed its
contents in a shell command. Read `CONNECTOR.md` there for the servers and tools.

## Operating rules

1. **Keyword queries.** `search_sessions` takes one to three terms or an
   identifier, not a sentence. "what did the team do on billing last week" →
   `{"query":"billing","repo":"all"}` then narrow by date with
   `list_repo_sessions` (`repo:"all"`, a period).
2. **Always `"repo": "all"`** for `search_sessions` and `list_repo_sessions`.
   This VM is not a git checkout, so the default repo scope is empty.
3. **Check `hit_kind`.** `transcript` and `title` hits contain your terms;
   `turn_prose` and `semantic` hits can be near neighbours that do not, so check
   the snippet first. Open a session with `get_session_summary`, then drill in:
   a `transcript` hit's `hit_event_index` (with its `agent_id`) goes to
   `get_session_transcript`, a `turn_prose` hit's `hit_turn_index` to `get_turn`.
4. **Analytics:** call `get_analytics_schema` once per conversation, then write
   Postgres `SELECT`s for `query_analytics` with `"scope": "global"` — this VM
   has no repository, so the default repo scope refuses the query. A rejection
   names the reason — fix the SQL and retry.
5. **Memory:** search before saving; save with an explicit `audience` and
   `global: true`, since nothing here is scoped to a repository.
6. **Pages:** write one self-contained HTML file under `~/workspace` and pass
   `publish_page` a `title` and the file's `path`. The page is served with no
   network access, so inline every style, script and image. It is private to
   the user until `visibility` says otherwise; set `org` or `scoped` only when
   the user names who should open it. Give the user the returned link. Publish
   a revision with `update_id` so the link stays the same; a revision refuses
   `visibility` and `grants`, so change who can open a page with
   `set_page_visibility`.
7. **Never paste `~/.config/kcap/` or any token** into the chat or a file.
8. **Never start `kcap daemon`**, run `kcap import`, or install hooks. This VM
   records only the chats the user puts in scope through the
   `kcap-recording` skill: if the user asks to record their chats, copy
   `~/workspace/kcap/node_modules/@kurrent/kcap/kcap/muse-assistant/kcap-recording/SKILL.md`
   to `~/workspace/skills/kcap-recording/SKILL.md` and follow its
   "Granting" section.
9. **A 401 or "token" error:** run `~/workspace/kcap/node_modules/.bin/kcap
   status`. If the sign-in has lapsed, tell the user to say "reconnect
   kcap"; the connect-kcap skill runs `kcap login --device` and shows
   them the code.
10. Cite what you found: session title, date, author, and the `session_id`, so
   the user can open it in kcap.
```

### B6. Keep the sign-in warm: an hourly `kcap status` cron

Ask the user first: the cron runs every hour while they are away, uses the
kcap sign-in to refresh it, appends to `~/workspace/capacitor/cron.log`,
and survives a VM replacement. Create it only once they agree; without it the
sign-in can lapse on a VM left idle, and "reconnect kcap" restores it.

Create a cron (title `kcap-hourly-status`, every hour) whose instruction is below.
If `kcap-hourly-status` or the former `capacitor-hourly-status` already exists,
update that cron in place instead of creating a second one.

> Run `~/workspace/kcap/node_modules/.bin/kcap status --no-update-check >> ~/workspace/capacitor/cron.log 2>&1`,
> then keep only the last 500 lines of that file. Do nothing else and do not
> wake the user — unless the output says the token is expired, missing or the
> server rejected it, in which case add one line to the chat: "kcap
> sign-in lapsed — say 'reconnect kcap' when you have a minute."

`kcap status` refreshes the token when it can, so the hourly run is what keeps a
long-idle VM signed in; `~/workspace/capacitor/cron.log` is where a lapse shows.
`--no-update-check` keeps it from contacting `registry.npmjs.org`, a host an
unattended run has no one to approve.

### B7. Recording (only when the user asks for it)

Connecting records nothing. If the user asks to record their chats — now or later —
install the recording skill and follow it; never grant recording from this skill:

When upgrading, replace the former `capacitor-recording` skill with `kcap-recording`;
keep existing recording data and consent unchanged.

```bash
mkdir -p ~/workspace/skills/kcap-recording
cp ~/workspace/kcap/node_modules/@kurrent/kcap/kcap/muse-assistant/kcap-recording/SKILL.md ~/workspace/skills/kcap-recording/SKILL.md
```

If that file does not exist, the installed kcap predates recording: run B2's
`npm install` again to update it, then copy.

Then follow `~/workspace/skills/kcap-recording/SKILL.md` from its "Granting"
section: it reads the consent scope back to the user, runs
`kcap muse-assistant scope add` in that same turn, and installs the 15-minute cron.

### B8. Tell the user

- kcap is connected: ask me things like "what did my team work on this
  week", "find the session where we fixed X", "save this for the team", or
  "make this a kcap page".
- Nothing on this VM is recorded until you ask: say "record my chats" and I
  will read the exact scope back to you before anything is sent.
- Everything lives under your home — `~/workspace/kcap`, `~/.config/kcap`,
  the connector, the skill and the cron (if you agreed to it) — so it survives
  a VM replacement.
- If a tool ever says the sign-in lapsed, say "reconnect kcap".
