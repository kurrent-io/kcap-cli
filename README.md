# Kurrent Capacitor

Full observability for your Claude Code sessions. Record every session, visualize agent activity in real time, and review code changes grounded in the actual development transcripts.

Capacitor captures the complete picture — session lifecycle, transcript data, subagent trees, tool usage, and token consumption — then surfaces it through a real-time dashboard and PR review tools that give you context no diff can provide.

## Getting started

You need the server URL from your admin (e.g. `https://my-tenant.kcap.ai`).

### 1. Install the CLI

```bash
npm install -g @kurrent/kcap
```

npm automatically selects the right native binary for your platform:

| Platform | Architecture |
|----------|-------------|
| macOS | ARM64 (Apple Silicon) |
| Linux | x64, ARM64 |
| Linux (Alpine/musl) | x64, ARM64 |
| Windows | x64 |

The CLI is compiled with NativeAOT — fast startup, no runtime dependency.

> **npm 11+ blocks install scripts by default.** You'll see a warning like
> `1 package has install scripts not yet covered by allowScripts`. The `kcap`
> binary works without the script; it only refreshes already-installed agent
> plugins (Claude / Codex / Cursor / Copilot / Gemini / Kiro / Pi) on upgrade. The warning suggests
> `npm approve-scripts @kurrent/kcap`, but that command rejects global installs
> (`EGLOBAL`) — a known npm UX bug. Instead, opt in one of two ways:
>
> ```bash
> # one-off
> npm install -g @kurrent/kcap --allow-scripts=@kurrent/kcap
> ```
>
> Or persistent — add this to `~/.npmrc` so every future `npm install -g`
> runs postinstall automatically:
>
> ```
> allow-scripts[]=@kurrent/kcap
> ```
>
> Without either, upgrade with **`kcap update`** instead of `npm install -g` — it
> runs the global npm upgrade and then refreshes your agent plugins itself, so it
> works regardless of the install-script gate. (You can also re-run `kcap plugin
> install [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--skills] --if-installed` manually.)

### 2. Run setup

```bash
kcap setup                      # discovers your tenant — no URL needed
kcap setup <tenant>             # shorthand for a known tenant slug → https://<tenant>.kcap.ai
kcap setup --server-url <url>   # explicit server (self-hosted, or a full URL)
```

The setup wizard walks you through:

1. **Server** — with no `--server-url`/`<tenant>`, kcap **discovers** your tenant: it signs you in with your organization's single sign-on (pass `--github` to use GitHub instead), then lets you choose from the tenants you belong to. A bare `<tenant>` slug expands to `https://<tenant>.kcap.ai`; a full URL is used as-is.
2. **Login** — authenticates via your tenant's configured sign-in method; discovery completes the sign-in inline
3. **Default visibility** — choose how your sessions are visible to others
4. **Coding-agent hooks** — detects Claude Code and Codex CLI on `PATH`, Cursor by user-dir presence (`~/.cursor/`), GitHub Copilot CLI by `~/.copilot/` or `copilot` on `PATH`, Google Gemini CLI by `~/.gemini/` or `gemini` on `PATH`, AWS Kiro CLI by `~/.kiro/` or `kiro`/`kiro-cli` on `PATH`, Pi by `~/.pi/` or `pi` on `PATH`, and SST OpenCode by `~/.config/opencode/` (or `~/.local/share/opencode/`) or `opencode` on `PATH`, then offers to install hooks/skills (or, for Pi/OpenCode, the live-ingest plugin) for each (user-wide). For Codex it also offers to enable **sandbox network access** for kcap (see below) — Codex blocks sandbox network by default, so the kcap skills can't reach the server without it. Each agent's own config-relocation environment variable is honored when set: `CLAUDE_CONFIG_DIR` (Claude), `CODEX_HOME` (Codex), `GEMINI_CLI_HOME` (Gemini — names the parent of `.gemini`), `KIRO_HOME` (Kiro), `COPILOT_HOME` (Copilot), `OPENCODE_CONFIG_DIR` (OpenCode), and `PI_CODING_AGENT_DIR` (Pi). Cursor's hooks path is fixed at `~/.cursor/hooks.json` and is not relocated.
5. **Daemon** — configure the daemon name for remote agent execution

When setup finishes, `kcap` sends a best-effort POST to the server's `/api/users/me/cli-setup` endpoint so the dashboard can mark your CLI as registered and surface the import-past-sessions hint. The call is capped at 5 seconds and failures are silent — they do not affect setup completion.

> **Restart your coding agent for live recording to begin.** Hooks only load at session start, so a session that was already running when you ran setup keeps running without them and won't stream live. Start a new session (or `claude --continue`) to pick the hooks up — setup prints this reminder when it installs any hooks. A manual `kcap import` of the in-progress session only yields a frozen snapshot.

Verify with `kcap whoami` and `kcap status`.

For non-interactive environments:

```bash
kcap setup --server-url https://my-tenant.kcap.ai --default-visibility org_public --no-prompt
```

In `--no-prompt` mode, the wizard installs hooks for every detected agent by default. Opt out per agent with `--skip-claude-hooks`, `--skip-codex-hooks`, `--skip-cursor-hooks`, `--skip-copilot-hooks`, `--skip-gemini-hooks`, `--skip-kiro-hooks`, `--skip-pi-hooks`, and/or `--skip-opencode-hooks`. When Codex hooks are installed, the wizard also enables Codex sandbox network access for your server(s) by default; pass `--skip-codex-network-access` to leave `~/.codex/config.toml` untouched.

> **Need hooks for an agent installed after setup, or scoped to a single repo?**
> Run `kcap plugin install [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode]` (omit the flag for the Claude Code plugin), or pair Codex with `--project` for a per-repo install. Use `--skills` instead of `--codex` if you only want the agent skills without Codex hooks. Cursor uses user-scope only — `--project` has no effect with `--cursor`. After installing Codex hooks, the next `codex` launch prompts to trust the new hooks — accept once to trust them all (run `/hooks` inside Codex if you'd rather trust each entry individually). After a `--project` install, also run `codex` once in the repo and accept the workspace trust prompt. Re-running after a kcap upgrade is rarely needed for user-scope installs — the npm postinstall hook auto-refreshes them on every `npm install -g @kurrent/kcap`, and `kcap update` refreshes them too (npm 11+ blocks install scripts by default — `kcap update` works regardless, or add `allow-scripts[]=@kurrent/kcap` to `~/.npmrc` to opt the postinstall in once).

> **Need at least one agent to capture sessions:** the setup wizard runs to completion without an agent CLI on `PATH` (it'll still configure your profile, auth, and daemon), but kcap only records work once Claude Code or Codex CLI is installed and the hooks are in place.

> **Keep the daemon running:** `kcap daemon start -d` stops when the process dies (a crash, or an OS memory-pressure kill — macOS jetsam / Linux OOM). To auto-restart it and start it at login, install it as a per-user service: `kcap daemon service install`. See [Daemon](#daemon).

### 3. Import existing sessions (optional)

```bash
kcap import                     # every detected agent (Claude, Codex, Cursor, Copilot, Gemini, Kiro, Pi, OpenCode)
kcap import --org EventStore    # sessions whose git-remote owner is EventStore
kcap import --org               # pick an org from your discovered repos (and remember it)
kcap import --repo owner/repo   # sessions for one specific repo
kcap import --cursor            # only Cursor
kcap import --copilot           # only Copilot
kcap import --gemini            # only Gemini
kcap import --kiro              # only Kiro
kcap import --pi                # only Pi (badlogic/pi-mono)
kcap import --opencode          # only OpenCode
```

> **Pi** has no shell hooks, so live capture uses a shipped Pi extension rather than a hooks file: run `kcap plugin install --pi` (or accept the `kcap setup` prompt) to write `~/.pi/agent/extensions/kcap.ts`, which `pi` auto-loads and streams each session live. Historical `kcap import --pi` works with or without it.

> **OpenCode** likewise has no shell hooks: live capture uses a shipped OpenCode plugin. Run `kcap plugin install --opencode` (or accept the `kcap setup` prompt) to write `~/.config/opencode/plugins/kcap.ts`, which `opencode` auto-loads and streams each session live (`vendor=opencode`). Subagents (the `task` tool / `@agent`) are captured too — the plugin fetches each child session via the SDK and streams it, so it nests under the parent in the trace. Historical `kcap import --opencode` reads OpenCode's SQLite database (`~/.local/share/opencode/opencode.db`) and imports child sessions as subagents, so it backfills sessions from before the plugin was installed.

This backfills your past sessions from `~/.claude/projects/` (Claude), `~/.codex/sessions/` (Codex), `~/.cursor/projects/.../agent-transcripts/` (Cursor), `~/.copilot/session-state/` (Copilot), `~/.gemini/tmp/<project>/chats/` (Gemini), `~/.kiro/sessions/cli/` (Kiro), `~/.pi/agent/sessions/` (Pi), and `~/.local/share/opencode/opencode.db` (OpenCode) so they appear in the dashboard. All agents are discovered automatically — pass `--claude`, `--codex`, `--cursor`, `--copilot`, `--gemini`, `--kiro`, `--pi`, or `--opencode` (one or more) to narrow the run. All forms are idempotent — safe to run multiple times.

You must pick an explicit scope (`--all`, `--org`, or `--repo`) so personal/private repos aren't uploaded by accident. `--org <owner>` filters by the git-remote owner (GitHub org/user) detected on each session — independent of your profile name, so it behaves identically under GitHub and WorkOS sign-in. A bare `--org` lets you pick an owner from your discovered repos and remembers it for next time. Run with no scope on an interactive terminal to get a picker. See [Loading historical sessions](#loading-historical-sessions) for the full set of flags.

If your repo directories have been renamed or deleted on disk, the import prints a list of unresolved cwds up front. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) to recover those sessions.

### 4. Open the dashboard

Open the server URL in your browser. The dashboard shows repositories, sessions, and agents. It updates in real time as Claude Code sessions are active.

### Sessions and Flows MCP servers for agents

The `kcap mcp sessions` stdio server lets coding agents search and recall past Capacitor sessions without leaving the chat. The Kurrent Capacitor plugin (installed by `kcap setup`) **auto-registers it for both Claude Code and Codex CLI** — no manual `claude mcp add` or TOML edit. The server is repo-aware: `cd` into a project before spawning your agent and `search_sessions` defaults to that repo's sessions.

The `kcap mcp flows` stdio server lets agents start and interact with AI-powered review flows. The plugin **auto-registers it for Claude Code** (Codex stays manual). See the [Flows MCP server](#flows-mcp-server-for-agents) section for details.

## What it records

Once set up, Capacitor runs silently in the background. Every Claude Code (and Codex CLI, if you installed those hooks) session is captured automatically:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Durable lifecycle delivery** — if the server is briefly unreachable when a `SessionStart`/`SessionEnd` hook (or a per-subagent `SubagentStop` carrying an `agent_id`) fires (for example during a deploy), the event is spooled to `~/.config/kcap/spool/` and automatically re-sent on the next hook, so sessions don't get stuck "active", lose their start record, or leave subagents stuck "running". No action needed; stale spool entries are reaped after 30 days.
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage
- **In-agent upgrade prompts** — in Claude Code sessions, when the server is running a newer kcap release than the local CLI, additional context is injected into the session so the agent can offer the user an upgrade via `kcap update`. The stderr `kcap` update hint continues to fire for direct command-line use.
- **SessionStart context injection** — at every session start the server injects top evaluation-derived fact clusters for the current repo into Claude's `additionalContext`. The injected block is split into two sections: `## Known patterns` (repo/project facts relevant to any reader) and `## Guidance from past sessions` (agent-targeted action items derived from prior eval suggestions with `audience: "agent"`). Opt out by setting `disable_session_guidelines: true` in `~/.config/kcap/config.json` or via `kcap config set disable_session_guidelines true`.

## CLI commands

### Initial setup

```bash
kcap setup                                   # interactive wizard (discovers your tenant)
kcap setup <tenant>                          # shorthand: https://<tenant>.kcap.ai
kcap setup --server-url <url> --no-prompt    # CI / scripted
```

With no server argument, setup (and `kcap login`) runs **tenant discovery**: it signs you in with your organization's single sign-on, then lets you pick from the tenants you belong to. Pass `--github` to sign in with GitHub instead; `--discover` forces discovery even when a server is configured. In SSH / headless environments (no browser), discovery falls back to GitHub Device Flow, since SSO needs a local browser.

The setup wizard detects every supported coding agent and offers to install hooks for each, then configures the daemon. Claude Code and Codex CLI are detected via `PATH`; Cursor is detected by user-dir presence (`~/.cursor/`), so IDE users without the `cursor` shell command are covered; GitHub Copilot CLI is detected via `~/.copilot/` or `copilot` on `PATH`; Google Gemini CLI via `~/.gemini/` or `gemini` on `PATH`; AWS Kiro CLI via `~/.kiro/` or `kiro`/`kiro-cli` on `PATH`; Pi via `~/.pi/agent/` or `pi` on `PATH`; and SST OpenCode via `~/.config/opencode/` (or `~/.local/share/opencode/`) or `opencode` on `PATH` (Pi and OpenCode have no shell hooks, so for those the wizard installs a live-ingest plugin rather than hook config). Re-run any time to update the configuration.

In `--no-prompt` mode, hooks install for every detected agent by default. Opt out per agent:

```bash
kcap setup --server-url <url> --no-prompt --skip-codex-hooks --skip-cursor-hooks   # only Claude
kcap setup --server-url <url> --no-prompt --skip-claude-hooks --skip-cursor-hooks  # only Codex
kcap setup --server-url <url> --no-prompt --skip-claude-hooks --skip-codex-hooks   # only Cursor
```

After installing Codex hooks, the next `codex` launch prompts to trust the new hooks — accept once to trust them all (run `/hooks` inside Codex if you'd rather trust each entry individually). For project-scope installs (a single repo), use `kcap plugin install [--codex] --project` after setup.

Legacy `--plugin-scope <user|project|skip>` is retained for backwards compatibility:

- `user` — no-op (matches the new default)
- `project` — install the Claude Code plugin into `<repo>/.claude/settings.local.json`. Must be run from inside a git working tree; setup exits with an error otherwise.
- `skip` — alias for `--skip-claude-hooks`

New scripts should prefer `--skip-claude-hooks` / `--skip-codex-hooks` and `kcap plugin install --project` for project scope.

If you run `kcap setup` outside any git working tree, it still completes — hooks install user-scope and fire for every session — but a tip at the end reminds you that sessions recorded from non-repo directories won't capture owner/repo/branch/PR context.

### Session recap

By default, shows a concise AI-generated summary — why the work was done, key decisions, and anything left unfinished. Use `--full` for the complete transcript with all prompts, responses, and file changes.

```bash
kcap recap <sessionId>              # summary (default)
kcap recap --full <sessionId>       # full transcript
kcap recap --chain <sessionId>      # summaries across continuation chain
kcap recap --chain --full <sessionId>  # full transcript across chain
kcap recap --per-turn <sessionId>   # compact per-turn index (prompt, tools, files, tokens, time)
kcap recap --get-turn <N> <sessionId>  # full event transcript for a single turn
```

`--per-turn` prints a one-block-per-turn index — useful for orienting in a long session before drilling into a specific turn with `--get-turn <N>` (the turn number shown in the `--per-turn` index). `--get-turn` takes the turn number as its value; the session id is the usual positional (or comes from the current session), so `kcap recap <sessionId> --get-turn <N>` works too.

The identifier can be a session GUID or a meta session slug. Find these from the dashboard or the current session's hook payloads. When run inside a Claude Code session with the kcap plugin, the session ID is set automatically.

If the kcap plugin is installed, you can also use the `/kcap:recap` skill inside Claude Code, or just ask:

```
Recap session c4de7fbe-cff5-4e2c-bf80-9858d02f58be and propose what should be done next.
```

### Plan validation

Verify that all items in a session's plan were completed.

```bash
kcap validate-plan <sessionId>
```

With the plugin installed, use the `/kcap:validate-plan` skill or ask naturally:

```
Did I finish everything in the plan? Check what's left to do.
```

### Hide session

Mark a session as owner-only so other users no longer see it in the dashboard.

```bash
kcap hide                 # current session
kcap hide <sessionId>     # specific session
```

### Disable recording

Stop the watcher, silence future hooks, and delete server-side data for a session.

```bash
kcap disable                 # current session
kcap disable <sessionId>     # specific session
```

This is irreversible on the server side; the local transcript file is untouched.

### Error extraction

Scan a recorded session for tool call errors — failed bash commands, file read/write errors, agent failures, etc.

```bash
kcap errors <sessionId>              # single session
kcap errors <meta-session-slug>      # meta session
kcap errors --chain <sessionId>      # full continuation chain
```

Useful for post-session review: identify recurring mistakes, discover patterns to avoid, and update project instructions accordingly.

### Session evaluation (LLM-as-judge)

Score a recorded session against safety, plan adherence, quality, and efficiency criteria. Each of 13 questions (e.g. *"Did the agent run destructive commands?"*, *"Did it write tests when appropriate?"*, *"Were there repeated failed attempts at the same operation?"*) is answered by a separate headless Claude judge with **no filesystem or network tools**. Most judges reason from the compacted session trace embedded in the prompt; some questions — and any session whose trace is too large to embed — instead investigate the session on demand through a read-only, session-scoped MCP tool surface (summary, search, transcript, errors, recap, tool results). The embed-vs-tools threshold is tunable via `KCAP_EVAL_TRACE_TOKEN_BUDGET` (default ~200K estimated tokens).

```bash
kcap eval <sessionId>                      # default: sonnet judge
kcap eval --model opus <sessionId>         # stronger judge
kcap eval --chain <sessionId>              # include the full continuation chain
kcap eval --threshold 5000 <sessionId>     # keep more of each tool output before truncation
kcap eval --questions safety <sessionId>   # run only the 4 safety judges
kcap eval --skip efficiency <sessionId>    # run everything except efficiency
kcap eval --list-questions                 # print the question taxonomy
```

Output is a per-category + overall score (1-5, with `pass`/`warn`/`fail` verdicts), with a specific finding and supporting evidence per question. The aggregate is also persisted back to the session's stream as a `SessionEvalCompleted` event, so past evaluations can be queried from the dashboard or used to track quality trends across sessions.

Expect ~1-3 minutes total depending on the model and session size — judges run sequentially.

> **Server requirement:** `kcap eval` fetches its question catalog from the server (`GET /api/eval/catalog`) and posts results to `POST /api/sessions/{id}/evals/v3`. It fails fast with a clear error against a server that doesn't expose the catalog endpoint, so the server must be running a build that includes the eval catalog (Capacitor AI-9). Upgrade the server if `kcap eval` reports the catalog endpoint is unavailable.

### PR review with full context

```bash
kcap review <pr-url-or-owner/repo#number>
```

Launches a Claude Code session equipped with MCP tools that query the implementation transcripts. Reviewers can ask *why* code was changed, understand design decisions, check what alternatives were considered, and verify test coverage — all grounded in what actually happened during development.

The same MCP server (`kcap-review`) is also auto-registered by the Kurrent Capacitor plugin and available in any Claude Code session, not just ones launched via `kcap review`. Each PR-scoped tool (`get_pr_summary`, `list_pr_files`, `get_file_context`, `search_context`, `list_sessions`) accepts an optional `pr` argument — pass `"owner/repo#123"` or a GitHub PR URL to review any PR from any branch. When omitted, the server falls back to the PR passed at startup (set by `kcap review <pr>`) or to git auto-detection against the current branch. `get_transcript` keys off `session_id` and doesn't need a `pr` argument.

### Sessions MCP server (for agents)

```bash
kcap mcp sessions
```

Stdio MCP server that exposes past Capacitor sessions to coding agents (Claude Code, Codex) so they can search and recall prior work without leaving the chat. The Kurrent Capacitor plugin auto-registers it for both Claude Code (via `.mcp.json`) and Codex CLI (via `.codex-plugin/plugin.json` → `.codex-mcp.json`), so there's nothing extra to do after `kcap setup`. If you installed the kcap plugin via Codex's native plugin manager (rather than `kcap setup` / `kcap plugin install --codex`), the MCP server is still auto-registered, but you'll also want to run `kcap plugin install --codex` to get hooks and agent skills.

It provides three tools:

- **`search_sessions`** — free-text search over past sessions (and subagent transcripts) in the current repo. Pass `repo: "all"` to search across every repo you can see, or `repo: "owner/name"` for a different one. Filter by `author` / `author_github_id`. Returns ranked hits with `session_id`, snippet, and (for transcript hits) `hit_event_index` + `agent_id` for drilling in.
- **`get_session_summary`** — concise `summary_text` + `plan` for a session. Use this to orient before reading the transcript.
- **`get_session_transcript`** — speaker-tagged events from a session. Pair `around_event` (and `agent_id` if the hit was in a subagent) with the values returned by `search_sessions` to fetch the exact decision context.
- **`get_turn`** — the full event transcript for one turn (user prompt, tool calls + results, assistant text) by `session_id` + `turn_index`. A turn is one user message and the assistant's full response up to the next user message.

The server is repo-aware — it resolves the current working directory to a repo hash at startup, and `search_sessions` defaults its `repo` filter to that hash unless you override it.

### Flows MCP server (for agents)

```bash
kcap mcp flows
```

Stdio MCP server that lets coding agents start and interact with AI-powered review flows directly from within a session. The Kurrent Capacitor plugin **auto-registers it for Claude Code** (via `.mcp.json`), so there's nothing to do after `kcap setup` — the flows server derives the target repo from its launch working directory, and Claude Code always runs inside the repo, so one registration works for every repo. It's registered even with no daemon connected; the tools simply stay inert (and `start_review_flow` returns an error) until a daemon with the repo is available.

Codex CLI/desktop apps have no single repo working directory, so they stay manual — add it to `~/.codex/config.toml`:

```toml
[mcp_servers.kcap-flows]
command = "kcap"           # use an absolute path (e.g. /opt/homebrew/bin/kcap) for the
args    = ["mcp", "flows"] # desktop app, which launches MCP servers without your shell PATH
```

It provides four tools:

- **`start_review_flow`** — start a new review flow (`spec-review` or `code-review`). Provide `kind`, `target_kind`, `target_ref`, `target_title`, and `context`. Requester context (session ID, cwd, repo root, owner, name) is resolved automatically from the environment. Returns a `flow_run_id`.
- **`submit_review_round`** — submit a follow-up round to an existing flow with updated context or a response to the reviewer's findings. Returns the new round's findings.
- **`get_review_flow_status`** — get the current status (running, waiting, completed, failed) and last result of a flow.
- **`close_review_flow`** — mark a completed review flow as closed.

Requires `kcap login` **and a running daemon with this repo checked out** — the server discovers a connected daemon to launch the hosted Codex reviewer, and `start_review_flow` errors if none (or more than one) matches. The server creates an authenticated HTTP client at startup and posts to the Capacitor server's `/api/flows/*` endpoints.

### Curate guidelines

Sync the repo's promoted curation guidelines into its `CLAUDE.md` and/or `AGENTS.md` via a managed block. The server tracks which guidelines have been promoted for the current repo; `curate apply` fetches them and writes (or updates) a `<!-- kcap:curated:start -->…<!-- kcap:curated:end -->` block in the relevant files. Content outside the markers is never touched.

```bash
kcap curate apply             # preview changes and confirm interactively
kcap curate apply --dry-run   # print what would change without writing anything
kcap curate apply --yes       # apply without prompting (CI / scripted)
kcap curate apply -y          # shorthand for --yes
```


### Loading historical sessions

Backfill older sessions from every detected coding agent in a single run. All seven agents ship per-session `.jsonl` transcripts (`~/.claude/projects/`, `~/.codex/sessions/`, `~/.cursor/projects/<sanitized-workspace>/agent-transcripts/`, `~/.copilot/session-state/`, `~/.gemini/tmp/<project>/chats/`, `~/.kiro/sessions/cli/`, `~/.pi/agent/sessions/`). They're discovered automatically and the command requires an explicit scope so personal/private repos aren't uploaded by accident:

```bash
kcap import --all                            # every discovered session from every agent
kcap import --org EventStore                 # sessions whose git-remote owner is EventStore
kcap import --org                            # pick an owner from discovered repos, then remember it
kcap import --repo owner/repo                # one specific repo
kcap import --repo .                         # the repo at the current cwd (must be a git repo with an origin remote)
```

Run `kcap import` with no scope on an interactive terminal to get a picker. Each run shows a confirmation summary (scope, matched count, repo samples, visibility) before uploading anything.

`--org` filters by the **git-remote owner** (GitHub org/user) detected on each session — not by your profile name. This makes it independent of how you signed in: under WorkOS the active profile is named after the tenant slug (which is not a GitHub org), so the owner to scope on is taken from the flag value or chosen from your discovered repos instead. Pass it explicitly as `--org <owner>`, or run a bare `kcap import --org` once on an interactive terminal to pick an owner from your discovered repos — the choice is saved to the active profile (`import_org`) and reused by later bare `--org` runs. Non-interactive runs (CI) must pass `--org <owner>` (or have a remembered org).

By default every available agent is imported. Pass one or more vendor filters to restrict the run:

```bash
kcap import --claude --org                   # only Claude transcripts
kcap import --codex --org                    # only Codex rollouts
kcap import --cursor --all                   # only Cursor — every discovered transcript
kcap import --cursor --cwd /path/to/proj     # only Cursor sessions whose workspace folder matches
kcap import --copilot --all                  # only Copilot — every discovered transcript
kcap import --gemini --all                   # only Gemini — every discovered transcript
kcap import --kiro --all                     # only Kiro — every session log under ~/.kiro/sessions/cli
kcap import --pi --all                       # only Pi — every discovered session
kcap import --opencode --all                 # only OpenCode — every session in opencode.db
```

Cursor historical import walks every JSONL transcript under `~/.cursor/projects/*/agent-transcripts/*/*.jsonl` and posts each line through the same `POST /hooks/transcript` route the live hook path uses, so live and historical ingest converge on one canonical event stream. The walker resolves each session's working directory by matching its sanitized workspace name against `~/Library/Application Support/Cursor/User/workspaceStorage/*/workspace.json` (on Linux: `~/.config/Cursor/User/...`); sessions whose workspace can't be resolved are still imported, just without `cwd` and git owner/repo enrichment.

Kiro historical import reads each session's append-only log at `~/.kiro/sessions/cli/{id}.jsonl` (plus the sibling `{id}.json` for cwd / model / title) and posts the lines through `POST /hooks/transcript` — the same lines the live watcher tails, so live and historical ingest converge. Set `KIRO_HOME` to point at a non-default location. Kiro persists no token counts, so imported Kiro sessions show no token usage (by design). Re-imports are idempotent — event ids are deterministic over `(session id, message/tool id, kind)`.

OpenCode historical import reads its SQLite database (`~/.local/share/opencode/opencode.db`, honouring `XDG_DATA_HOME`) and reconstructs the same `{info,parts}` lines the live plugin streams — so live and historical ingest converge on one canonical event stream (`vendor=opencode`). Child sessions are imported as subagents of their parent (`/hooks/subagent-*`), matching the live nesting. Because the server exposes no completeness signal, kcap records each fully-imported session in a local ledger (`~/.cache/kcap/opencode-imported.json`, keyed by server) to skip it on re-run; a session interrupted mid-import is repaired on the next run. Re-imports are idempotent — canonical event ids derive from OpenCode's stable `prt_` part ids.

Claude (`CLAUDE_CONFIG_DIR`), Codex (`CODEX_HOME`), Gemini (`GEMINI_CLI_HOME`, which names the parent of `.gemini`), OpenCode (`OPENCODE_CONFIG_DIR`), and Pi (`PI_CODING_AGENT_DIR`, which names the `~/.pi/agent` leaf) historical/live paths follow each agent's own config-relocation environment variable when it is set, so a relocated config is discovered automatically.

Additional flags:

```bash
kcap import --org EventStore --yes           # skip the confirmation prompt
kcap import --org EventStore --private        # mark every imported session as Only Visible to You
kcap import --org EventStore --since 2026-01-01  # only sessions on or after this date
kcap import --org EventStore --cwd /path/to/project  # filter by working directory
kcap import --org EventStore --session abc123    # single session
```

Non-interactive runs (no TTY, e.g. CI) must pass both a scope flag and `--yes`. The command is idempotent and resumable — re-running with the same scope only uploads what's missing or incomplete. A server-side tracker deduplicates events on `(stream, eventId)` so previously-imported turns don't get re-appended.

After discovery, the import surfaces a one-shot report of any transcript working directories that no longer exist on disk. Sessions whose cwd was an ephemeral worktree (e.g. `~/dev/my-repo/.claude/worktrees/<slug>` or `~/dev/my-repo/.capacitor/worktrees/<slug>`) are transparently attributed to their parent project when that project still exists, so deleted-worktree paths drop out of the missing-cwds list. kcap's own background helper runs (the headless `claude -p` calls behind title generation and "what's done" summaries) record their transcripts in a throwaway temp directory that is removed the moment the run ends; these are never imported, and they're also excluded from the missing-cwds report so its dead temp paths don't drown out real ones. What remains is typically local repo dirs that have been renamed — those won't match an `--org` / `--repo` scope until you tell kcap how their old paths map to the new ones. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) below for the fix.

### Daemon

The daemon connects to the Capacitor server and runs Claude Code or Codex agents in isolated git worktrees, controlled from the dashboard. The daemon supports hosted Claude and Codex agents on macOS and Linux — choose the vendor from the dashboard's launch dialog. At startup the daemon probes `daemon.claude_path` and `daemon.codex_path` and advertises only the vendors it can actually spawn, so the launch dialog hides whichever agent isn't installed on the selected daemon.

```bash
kcap daemon start                   # start in foreground (defaults --name to your OS username)
kcap daemon start -d                # start in background (daemonize)
kcap daemon start --name laptop -d  # run multiple daemons on the same machine by giving each a unique name
kcap daemon status                  # list all running daemons
kcap daemon status --name laptop    # show status of a specific daemon
kcap daemon stop --name laptop      # stop just that one
kcap daemon stop --yes              # stop all running daemons unattended (otherwise prompts on multi)
kcap daemon restart --name laptop              # restart now if idle; refuses while agents/evals run
kcap daemon restart --name laptop --when-idle  # queue the restart for the next idle moment
kcap daemon restart --name laptop --force      # restart now even if busy (tears down running agents)
kcap daemon doctor                  # diagnose lock-file state for every daemon name
kcap daemon doctor --clean          # also remove stale lock/pid files (held entries are never touched)
```

`KCAP_DAEMON_NAME` overrides the active profile's daemon name (superseded by an explicit `--name` flag).

**Updating:** after `kcap update`, a running daemon on macOS/Linux detects the new binary and restarts itself once it's **idle** (no running hosted agents and no in-flight eval) — service-managed daemons exit so the supervisor relaunches the new binary; background (`-d`) daemons re-spawn themselves. `kcap daemon status` shows a pending restart; `kcap daemon restart --force` applies it now. On **Windows**, stop the daemon (`kcap daemon stop` / `kcap daemon service stop`) before `kcap update` — a running daemon locks its binary, so the update can't replace it (the launcher detects this and aborts with instructions).

#### Run it as a service (auto-restart)

`kcap daemon start -d` runs only until the process dies — a crash, or an OS memory-pressure kill (macOS **jetsam** / Linux **OOM killer**) that sends an uncatchable `SIGKILL`. To have the daemon auto-restart and start at login, install it as a **per-user** OS service:

```bash
kcap daemon service install                # launchd (macOS) / systemd --user (Linux) / Scheduled Task (Windows)
kcap daemon service install --name laptop  # a service per daemon name
kcap daemon service status                 # installed / running state
kcap daemon service stop                   # stop the running service (stays installed)
kcap daemon service start                  # start it again
kcap daemon service uninstall              # stop and remove the service
```

`install` pins the active profile via `KCAP_PROFILE` and captures your current `PATH` into the unit, so the supervised daemon resolves the same server URL, `claude`/`codex` binaries, and profile settings it would from your shell. Pass `--profile P` to pin a different profile, `--max-agents N` to bake an override, or `--no-start` to register without starting. The service restarts the daemon on crash/`SIGKILL` but **not** on a clean stop.

Because the service auto-restarts, stop a service-managed daemon with `kcap daemon service stop` (or `uninstall`) rather than `kcap daemon stop` — a raw stop would be relaunched immediately. `kcap daemon status` and `kcap daemon doctor` both report installed services.

Each daemon process holds an exclusive `flock` on `~/.config/kcap/daemons/<name>.lock` for its entire lifetime. The kernel releases the lock automatically when the daemon exits (including `SIGKILL` or power-off), so leftover lock files on disk are never a blocker — only a live process holding the kernel-level lock can prevent another daemon from acquiring the same name.

Two daemons with **different** `--name` values can run side-by-side. Two daemons under the **same name** on the same machine collide on the flock and the second one exits with code 2. Even if that guard is bypassed somehow, the server rejects the second daemon's `DaemonConnect` with a typed error and the second daemon exits with code 3 — no more silent slot-displacement oscillation.

#### Hosted Codex agents

Hosted Codex agents require the Codex hook surface — if you said yes during `kcap setup`, you already have it. Otherwise install it manually:

Codex CLI 0.81+ exports `CODEX_THREAD_ID`; kcap reads it the same way it reads `KCAP_SESSION_ID` for Claude sessions — no manual session ID needed for any of the Codex skills (`kcap-recap`, `kcap-errors`, `kcap-hide`, `kcap-disable`, `kcap-validate-plan`).

```bash
kcap plugin install --codex                          # user scope (~/.codex/hooks.json + ~/.agents/skills/)
kcap plugin install --codex --project                # project scope (<repo>/.codex/hooks.json), skills still user-wide
kcap plugin install --skills                         # skills only (~/.agents/skills/), no Codex hooks
kcap plugin install --skills --if-installed          # refresh only if skills were previously installed (used by npm postinstall, harmless to call by hand)
kcap plugin install --codex --if-installed           # refresh Codex hooks only if previously installed (used by npm postinstall)
kcap plugin install --if-installed                   # refresh Claude plugin registration only if previously installed (used by npm postinstall)
```

Installing with `--codex` (or `--skills`) writes six skills under `~/.agents/skills/`:

| Skill | Wraps | Purpose |
|---|---|---|
| `kcap-recap` | `kcap recap` | Session summary / continuation chain / repo history |
| `kcap-errors` | `kcap errors` | Tool-call error extraction |
| `kcap-hide` | `kcap hide` | Mark session owner-only |
| `kcap-disable` | `kcap disable` | Stop recording + delete server data |
| `kcap-validate-plan` | `kcap validate-plan` | Verify plan items were completed |
| `kcap-review-flows` | `kcap mcp flows` | Structured iterative spec/code review loops |

The first five (`kcap-recap`, `kcap-errors`, `kcap-hide`, `kcap-disable`, `kcap-validate-plan`) auto-resolve the active session from `CODEX_THREAD_ID`; pass `<sessionId>` explicitly to operate on a different session. `kcap-review-flows` works differently — it operates via flow IDs through `kcap mcp flows` rather than session auto-resolution; see [Flows MCP server (for agents)](#flows-mcp-server-for-agents) for details.

> **Codex sandbox network access (AI-794).** The skills shell out to `kcap …`, which talks to the Capacitor server — but Codex runs the agent's shell tool in a `workspace-write` sandbox that **blocks network by default**, so the skills fail (or demand escalation) until network access is allowed. Both `kcap setup` (one yes/no prompt after the Codex hooks step) and `kcap plugin install --codex` enable it for you. They write a constrained allowlist to `~/.codex/config.toml` rather than opening the network wholesale:
>
> ```toml
> [sandbox_workspace_write]
> network_access = true          # required — the proxy only enforces; it doesn't grant
>
> [features.network_proxy]
> enabled = true
> domains = { "**.kcap.ai" = "allow" }
> ```
>
> The `**.kcap.ai` wildcard covers every SaaS tenant — current and future — plus `auth.kcap.ai`, so switching profiles and adding tenants just work with no per-tenant edits. **Self-hosted** servers are added as exact-host entries, derived from every configured profile's `server_url` and refreshed on each `kcap setup`. Existing config is respected: if you already run a `network_proxy` policy, kcap's hosts are merged into your `domains` (yours preserved); if you've already opened the network (`network_access = true` with no proxy), nothing changes. Opt out with `--skip-codex-network-access` on either command (and the npm-postinstall `--if-installed` refresh never touches `config.toml`). A localhost dev server additionally needs `allow_local_binding = true`, which kcap does not set. Uninstall leaves these keys in place — they're your security posture, not kcap state.

The daemon starts Codex with `--sandbox workspace-write` and `--ask-for-approval on-request`. This lets Codex edit files in the agent's worktree but escalates sensitive operations (e.g. network calls, shell commands outside the worktree) through the daemon's permission bridge to the dashboard.

> **Upgrading from an earlier version of kcap?** Run `kcap update` (or `npm install -g @kurrent/kcap`) — the npm postinstall hook, and `kcap update` itself, refresh all user-scope kcap installations, so you always pick up the current CLI version's skills (`~/.agents/skills/kcap-*`), Codex hook commands (`~/.codex/hooks.json`), and Claude plugin registration (`~/.claude/settings.json`). Each refresh is gated on a marker file written by your previous setup — fresh systems that never opted in are left untouched. Project-scope installs (`--project`) are not auto-refreshed; re-run `kcap plugin install [--codex] --project` after upgrading if you want the latest config for a specific repo.
>
> npm 11+ blocks install scripts by default. Add `allow-scripts[]=@kurrent/kcap` to your `~/.npmrc` (or pass `--allow-scripts=@kurrent/kcap` on the install command line) to opt in to the auto-refresh; otherwise re-run the relevant `kcap plugin install ... --if-installed` commands manually after each upgrade. (`npm approve-scripts` does not work for global installs — that's a known npm UX bug.)

PR review is supported for hosted Codex agents as well as Claude — the same `kcap-review` MCP context is injected either way. The sandbox and approval-mode selectors in the launch dialog are planned as a follow-up (AI-633).

#### Cursor IDE hooks

Cursor is detected by the presence of `~/.cursor/` — you don't need the `cursor` shell command on `PATH`. If `kcap setup` found Cursor and you said yes, hooks are already in place. To install or remove later:

```bash
kcap plugin install --cursor                # writes ~/.cursor/hooks.json
kcap plugin remove --cursor                 # remove Cursor hooks
```

#### GitHub Copilot CLI hooks

Copilot CLI is detected via `~/.copilot/` (created on Copilot's first run) or the `copilot` binary on `PATH`. kcap writes its own hooks file — Copilot merges every `*.json` under `~/.copilot/hooks/`, so your other hook files are never touched. Copilot loads hook config at startup: restart any running `copilot` session after installing.

```bash
kcap plugin install --copilot               # writes ~/.copilot/hooks/kcap.json
kcap plugin remove --copilot                # deletes ~/.copilot/hooks/kcap.json
```

Live sessions stream from `~/.copilot/session-state/<session-id>/events.jsonl` (`$COPILOT_HOME` is honoured); historical sessions import via `kcap import --copilot`, which also forwards Copilot's auto-generated session names as titles. Sessions resumed with `copilot --continue` / `--resume` reattach to the same recorded session.

#### Google Gemini CLI hooks

Gemini CLI is detected via `~/.gemini/` (created on Gemini's first run) or the `gemini` binary on `PATH`. Gemini keeps its hooks in the shared `~/.gemini/settings.json`, so kcap **merges** its entries into the `hooks` block and preserves your other settings and any hand-authored hook entries. Gemini loads hook config at startup: restart any running `gemini` session after installing.

```bash
kcap plugin install --gemini                # merges kcap hooks into ~/.gemini/settings.json
kcap plugin remove --gemini                 # removes only kcap's entries
```

Live sessions stream from the chat-recording JSONL Gemini names in each hook's `transcript_path` (`~/.gemini/tmp/<project>/chats/session-*.jsonl`); historical sessions import via `kcap import --gemini`. Sessions resumed with `gemini --resume` reattach to the same recorded session. (Historical import leaves working-directory / repo enrichment empty — Gemini doesn't record the cwd in a machine-readable header; live capture gets it from the hook payload.)

Spawned subagents are captured too: Gemini records each in a nested `chats/<session>/<subId>.jsonl`, and both live capture (a child watcher per subagent) and `kcap import --gemini` discover and stream them, so they nest under the parent session in the trace.

#### AWS Kiro CLI hooks

AWS Kiro CLI (the rebranded Amazon Q Developer CLI) is detected via `~/.kiro/` or the `kiro` / `kiro-cli` binary on `PATH`. Kiro hooks fire only for the **active** agent — there is no global hook — so to capture every session transparently, `install --kiro` **clones your current default agent** into `~/.kiro/agents/kcap.json` (preserving its tools; a minimal agent would lose tool access), adds kcap's `agentSpawn` hook, and makes it your default agent (`chat.defaultAgent` in `~/.kiro/settings/cli.json`). This needs `kiro-cli` on `PATH` to perform the clone. Restart any running `kiro` session after installing. `remove --kiro` restores your previous default agent and deletes `kcap.json`.

```bash
kcap plugin install --kiro                  # clone default agent + add hook, set as default
kcap plugin remove --kiro                   # restore previous default, delete kcap.json
```

Kiro writes an append-only JSONL log per session at `~/.kiro/sessions/cli/{id}.jsonl` (plus a sibling `{id}.json` for cwd / model / title; honours `KIRO_HOME`), so the kcap watcher tails it like every other vendor. Lifecycle comes from Kiro's `agentSpawn` hook (fires every prompt → deduped server-side); since Kiro has **no session-end trigger**, the watcher synthesizes session-end on `kiro-cli` exit. Historical sessions import via `kcap import --kiro`. Kiro persists no token counts, so Kiro sessions show no token usage by design.

#### SST OpenCode plugin

SST OpenCode is detected via `~/.config/opencode/` (or `~/.local/share/opencode/`) or the `opencode` binary on `PATH`. OpenCode has **no shell hooks** — it exposes an in-process plugin API — so `install --opencode` writes a dependency-free plugin to `~/.config/opencode/plugins/kcap.ts`, which `opencode` auto-loads at startup. Restart any running `opencode` session after installing.

```bash
kcap plugin install --opencode              # write ~/.config/opencode/plugins/kcap.ts
kcap plugin remove --opencode               # delete it
```

On `session.created` the plugin runs `kcap hook --opencode` (POSTs lifecycle + spawns the watcher); on each `session.idle` it fetches the session's full messages via OpenCode's in-process SDK and appends them as native `{info, parts}` JSONL to a file the watcher tails (`vendor=opencode`) — so kcap must be on `PATH`. Since OpenCode has **no session-end event**, the watcher synthesizes session-end when the `opencode` process exits. OpenCode records per-message tokens/cost, so those flow through. Capture is **live-only** — OpenCode's on-disk format is unstable (SQLite, version-dependent), so there is no historical `kcap import --opencode`.

Cursor uses a single user-scope `hooks.json`; there is no project-scope variant.

`kcap setup` writes all 8 supported Cursor hook entries. Use `--skip-cursor-hooks` to opt out during setup:

```bash
kcap setup --server-url <url> --no-prompt --skip-cursor-hooks
```

#### Daemon config settings

Use `kcap config set` to configure the binary paths used by the daemon. The values are stored in the active profile and take effect the next time the daemon starts.

```bash
kcap config set daemon.claude_path /opt/claude/bin/claude
kcap config set daemon.codex_path  /opt/codex/bin/codex
```

| Key | Default | Description |
|-----|---------|-------------|
| `daemon.claude_path` | `"claude"` | Path to the Claude CLI binary. Resolved via `PATH` when not an absolute path. |
| `daemon.codex_path`  | `"codex"`  | Path to the Codex CLI binary. Resolved via `PATH` when not an absolute path. |

You can also override these at runtime with environment variables (take precedence over the profile):

```bash
KCAP_CLAUDE_PATH=/opt/claude/bin/claude kcap daemon
KCAP_CODEX_PATH=/opt/codex/bin/codex  kcap daemon
```

**Codex session-end tuning.** Because Codex has no session-end hook, the watcher owns session-end via two triggers: parent `codex` process exit, and rollout-file idle timeout. The idle trigger is particularly important for the Codex desktop app, whose shared `codex app-server` process never exits per-conversation.

| Environment variable | Default | Description |
|----------------------|---------|-------------|
| `KCAP_CODEX_IDLE_MINUTES` | `60` | How long a Codex rollout file may be idle (no new rollout lines and no Codex tool call in flight) before the `kcap watch` background watcher ends the session (`reason: idle_timeout`). Increase for very long thinking/compute turns; decrease for faster cleanup of abandoned sessions. Invalid or non-positive values fall back to the 60-minute default. |

#### Daemon log verbosity

The daemon logs at `Information` by default. Raise the level for transport diagnostics — for example, per-tick `DaemonPing` round-trip times (logged at `Debug`) are useful for telling whether SignalR reconnects are caused by network/proxy latency. Set it either way:

```bash
kcap daemon start --log-level debug        # foreground or with -d; forwarded to the daemon
KCAP_DAEMON_LOG_LEVEL=debug kcap daemon    # env var; read directly, works in any launch mode (service, container)
```

Accepted values: `trace`, `debug`, `information` (default), `warning`, `error`, `critical`, `none`. The `--log-level` flag wins over the env var when both are set. `Debug` is verbose — it also enables the SignalR client's framework logs — so use it for a diagnostic window rather than steady state.

### Local agents (run-agent / attach / ls)

Start a coding agent from your own terminal that the daemon hosts for you. Because the daemon owns the agent (not your terminal), you can **detach and the agent keeps running**, then **re-attach later** — like `tmux` for your coding agent.

```bash
kcap run-agent claude                       # start Claude in the current directory, attached
kcap run-agent claude -- --model opus       # everything after `--` is passed to the agent CLI verbatim
kcap run-agent codex --worktree -- -m gpt-5 # run in an isolated git worktree instead of in place
kcap run-agent claude --detached            # start without attaching; prints the agent id
```

- **`--` boundary:** flags before `--` are kcap's; everything after `--` is forwarded to the `claude`/`codex` CLI unchanged. kcap flags: `--worktree`, `--private`, `--name <daemon>`, `--detached`.
- **Visibility:** by default the agent is **registered with the server**, so it appears in your own web UI immediately and you can drive it from the browser — start in the terminal, continue from anywhere. It is **visible only to you** until you share it. Pass `--private` to keep it purely local: unregistered, not streamed to the server, and not shown in the web UI.
- **Work location:** by default the agent runs **in place in your current directory** (it edits your real files). Pass `--worktree` to run in a throwaway git worktree instead.
- **Detach** without stopping the agent with the prefix key **`Ctrl-Q` then `d`**. The agent keeps running in the daemon.
- **Permissions:** for a registered agent, permission prompts appear in the web UI (the same dialog as hosted agents); with `--private`, prompts are answered natively in your terminal.

```bash
kcap ls                 # list daemon-hosted agents (id, status, repo)
kcap attach <agent-id>  # re-attach your terminal to a running agent
```

`run-agent` auto-starts the daemon if one isn't already running. It needs a configured server (like the rest of kcap) — it is not an offline command. A locally-started agent now appears in **your own** web UI (owner-only until you share it from the web UI); use `--private` to opt out of registration entirely. Unix only for now.

### Repository paths

Manage known repo paths for the agent launch dialog. Repos are automatically added when agents are launched, but you can also manage the list manually:

```bash
kcap repos                    # list known repos (sorted by last used)
kcap repos add .              # add current directory
kcap repos add ~/dev/project  # add a specific path
kcap repos remove ~/dev/old   # remove a path
```

Known repos are persisted to `~/.config/kcap/repos.json` and reported to the server when the daemon connects, so the launch dialog always shows previously-used repos even after restarts.

### Profiles

Profiles let you work with multiple Capacitor servers — for example, a company server for work repos and a separate one for open-source projects. Each profile stores its own server URL, visibility settings, and daemon configuration.

```bash
kcap profile add work --server-url https://my-other-tenant.kcap.ai
kcap profile add oss --server-url https://cap.oss.dev --remote "github.com/myorg/*"
kcap profile list
kcap profile show work
kcap profile remove work
```

The `--remote` flag associates a profile with git remote patterns. When you open a repo whose remote matches a pattern, that profile activates automatically.

#### Switching profiles

```bash
kcap use work                  # bind 'work' profile to current repo/directory
kcap use work --global         # set 'work' as the global default
kcap use oss --save            # bind and write .kcap.json for team sharing
```

Without `--global`, `use` binds the profile to the current git repo root (or the current directory if not in a repo). With `--save`, it writes a `.kcap.json` file that can be committed so the whole team uses the same profile.

#### Profile resolution order

The CLI resolves which profile to use in this order:

1. `--server-url` CLI flag
2. `KCAP_URL` environment variable
3. `KCAP_PROFILE` environment variable
4. `.kcap.json` in the repo root (or current directory if not in a repo)
5. Git remote pattern matching from `--remote` flags
6. Directory binding from `kcap use`
7. Global active profile (or `default`)

### Configuration

```bash
kcap config show    # show current configuration
kcap config set <key> <value>
```

**Default session visibility** controls how your sessions appear to other users in the same Kurrent Capacitor account. Set during `kcap setup` or change at any time:

```bash
kcap config set default_visibility private      # only you can see your sessions
kcap config set default_visibility org_public   # org repos visible, others private (default)
kcap config set default_visibility public       # all sessions visible to others in your account
```

**Repository exclusions** prevent specific repos from sending any data to the server — hooks are silently skipped, no session is recorded:

```bash
kcap config set excluded_repos "myorg/secret-project,personal/diary"
```

**Path exclusions** silently skip any session whose working directory is, or sits inside, a configured path — useful for ignoring scratch dirs, worktrees, or monorepo subtrees regardless of git remote:

```bash
kcap ignore .                       # ignore the current directory
kcap ignore ~/code/secret-project   # ignore a specific tree
kcap ignore --list                  # show all ignored paths
kcap ignore --remove ~/code/secret-project
```

Entries are stored on the **active profile**, so switching profiles with `kcap use` switches the ignore list too. Symlinks are resolved on both the stored entry and the session's reported cwd, so a worktree symlink and its target match.

**Provider API keys for headless calls.** Title generation, summaries, and judges shell out to `claude -p` / `codex exec` in the background. By default kcap scrubs `ANTHROPIC_API_KEY` and `OPENAI_API_KEY` from those spawns so your subscription login (claude.ai / ChatGPT account) is used — a globally-set key would otherwise override subscription auth and fail the call. If you intentionally authenticate via API key (PAYG), opt back in:

```bash
kcap config set use_provider_api_key true     # keep keys in headless spawns
KCAP_USE_PROVIDER_API_KEY=1 kcap recap …      # one-off override
```

`kcap setup` also prompts for this when it detects either key in the current environment. The env var (`1`/`true`/`yes`/`on` or `0`/`false`/`no`/`off`) wins over the profile setting.

#### Renamed repo directories (`kcap remap`)

Historic transcripts record the absolute working directory they ran in. If you've since renamed or moved that directory on disk (e.g. `~/dev/foo-cli → ~/dev/bar-cli`), `kcap import --org` / `--repo` can't resolve those sessions to a GitHub repo any more and silently drops them from the matched count.

Manage the rewrites with `kcap remap`:

```bash
kcap remap ~/dev/eventstore/foo-cli ~/dev/eventstore/bar-cli   # add or replace a mapping
kcap remap --list                                              # show all mappings
kcap remap --remove ~/dev/eventstore/foo-cli                   # drop one
```

Entries are stored at the top of `~/.config/kcap/config.json` under `cwd_remap` (a top-level JSON array of `{ "from": ..., "to": ... }` objects) — you can also edit the file directly for bulk changes.

Semantics:

- `from` / `to` are **path-prefix** rewrites with `~` expanding to the current user's home directory (`~\` is also accepted on Windows). The match requires a path boundary (`from` exactly equal, or `from` followed by `/` — or `\` on Windows), so `from: "~/dev/foo"` will **not** spuriously rewrite `~/dev/foo-cli`.
- Comparisons follow the host filesystem's case policy: case-insensitive on Windows, case-sensitive elsewhere.
- When multiple rules could apply to the same transcript cwd, the **longest** `from` wins.
- Rules are applied once (no chaining), so the result of one rule isn't fed into another.
- Remaps are global, not per-profile — same rename affects all profiles' imports.

After adding a remap, re-run `kcap import --org` (or whichever scope you use). The missing-cwd report at the top of the import will show what's still unresolved. Ephemeral worktree paths under `<project>/.<anything>/worktrees/<slug>` are auto-attributed to `<project>` when it still exists on disk, so deleted-worktree cwds don't need a remap entry.

### Uninstalling

To remove kcap from this machine, run:

```bash
kcap uninstall                  # interactive, user-scope removal
kcap uninstall --yes            # non-interactive
kcap uninstall --project --yes  # also strip project-scope hooks in cwd's repo
kcap uninstall --keep-config    # remove integrations, keep ~/.config/kcap
```

`uninstall` covers every supported agent: it stops running daemons and watcher processes, strips kcap entries from user-level Claude Code, Codex CLI, Cursor, and Copilot CLI hook files (preserving any non-kcap entries), deletes the Pi live-ingest extension (`~/.pi/agent/extensions/kcap.ts`) and the OpenCode plugin (`~/.config/opencode/plugins/kcap.ts`), removes agent skills under `~/.agents/skills/` (plus the legacy `~/.codex/skills/kcap-*` folders), and deletes `~/.config/kcap/`.

`--project` additionally cleans up `<repo>/.claude/settings.local.json` and `<repo>/.codex/hooks.json` in the current git working tree (errors if you're not inside one). Cursor only has a user-scope `hooks.json`, so `--project` does not affect it. Project-scope hooks in other repos are not touched — re-run from each repo that has them.

Use `--keep-config` to preserve profiles, tokens, and ignore lists when you plan to reinstall. Per-agent selective cleanup is not exposed here — use `kcap plugin remove [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--skills]` for finer-grained removal.

### Other commands

```bash
kcap status         # server health check
kcap whoami         # show current authenticated user
kcap login          # authenticate via OAuth (browser flow by default)
kcap login --device # force device-code flow (use in SSH / headless envs)
kcap update         # upgrade the CLI and refresh agent plugins (npm-global installs)
kcap logout         # delete stored tokens
```

> `kcap update` is the one-step upgrade for npm-global installs: it checks the
> registry, runs `npm install -g @kurrent/kcap@latest`, then refreshes your
> opted-in agent plugins — so it picks up new skills/hooks even when your package
> manager blocks install scripts. It exits early if you're already up to date,
> and tells you what to run instead for non-npm installs (e.g. Homebrew). Use
> `kcap update --check` for a machine-readable `{current, latest, newer}` probe.

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kcap` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

    kcap config set server_url https://my-tenant.kcap.ai

Or remove the config file and re-run setup:

    rm ~/.config/kcap/config.json
    kcap setup

## License

[Kurrent License v1](LICENSE.md)
