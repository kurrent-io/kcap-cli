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
> plugins (Claude / Codex / Cursor) on upgrade. The warning suggests
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
> Without either, re-run `kcap plugin install [--codex|--cursor|--skills] --if-installed` manually after each upgrade.

### 2. Run setup

```bash
kcap setup
```

The setup wizard walks you through:

1. **Server URL** — enter the URL your admin provided
2. **Login** — authenticates via GitHub Device Flow (if the server requires auth)
3. **Default visibility** — choose how your sessions are visible to others
4. **Coding-agent hooks** — detects Claude Code and Codex CLI on `PATH`, and Cursor by user-dir presence (`~/.cursor/`), then offers to install hooks/skills for each (user-wide)
5. **Daemon** — configure the daemon name for remote agent execution

When setup finishes, `kcap` sends a best-effort POST to the server's `/api/users/me/cli-setup` endpoint so the dashboard can mark your CLI as registered and surface the import-past-sessions hint. The call is capped at 5 seconds and failures are silent — they do not affect setup completion.

Verify with `kcap whoami` and `kcap status`.

For non-interactive environments:

```bash
kcap setup --server-url https://my-tenant.kcap.ai --default-visibility org_public --no-prompt
```

In `--no-prompt` mode, the wizard installs hooks for every detected agent by default. Opt out per agent with `--skip-claude-hooks`, `--skip-codex-hooks`, and/or `--skip-cursor-hooks`.

> **Need hooks for an agent installed after setup, or scoped to a single repo?**
> Run `kcap plugin install [--codex|--cursor]` (omit the flag for the Claude Code plugin), or pair Codex with `--project` for a per-repo install. Use `--skills` instead of `--codex` if you only want the agent skills without Codex hooks. Cursor uses user-scope only — `--project` has no effect with `--cursor`. After installing Codex hooks, the next `codex` launch prompts to trust the new hooks — accept once to trust them all (run `/hooks` inside Codex if you'd rather trust each entry individually). After a `--project` install, also run `codex` once in the repo and accept the workspace trust prompt. Re-running after a kcap upgrade is rarely needed for user-scope installs — the npm postinstall hook auto-refreshes them on every `npm install -g @kurrent/kcap` (npm 11+ blocks install scripts by default; add `allow-scripts[]=@kurrent/kcap` to `~/.npmrc` to opt in once).

> **Need at least one agent to capture sessions:** the setup wizard runs to completion without an agent CLI on `PATH` (it'll still configure your profile, auth, and daemon), but kcap only records work once Claude Code or Codex CLI is installed and the hooks are in place.

### 3. Import existing sessions (optional)

```bash
kcap import                     # every detected agent (Claude, Codex, Cursor)
kcap import --org               # sessions for the org bound to your active profile
kcap import --repo owner/repo   # sessions for one specific repo
kcap import --cursor            # only Cursor
```

This backfills your past sessions from `~/.claude/projects/` (Claude), `~/.codex/sessions/` (Codex), and `~/.cursor/projects/.../agent-transcripts/` (Cursor) so they appear in the dashboard. All agents are discovered automatically — pass `--claude`, `--codex`, or `--cursor` (one or more) to narrow the run. All forms are idempotent — safe to run multiple times.

You must pick an explicit scope (`--all`, `--org`, or `--repo`) so personal/private repos aren't uploaded by accident. `--org` uses the active profile name as the GitHub org login — it works out of the box when the profile was created by `kcap setup` (which names it after the picked tenant), and errors otherwise. Run with no scope on an interactive terminal to get a picker. See [Loading historical sessions](#loading-historical-sessions) for the full set of flags.

If your repo directories have been renamed or deleted on disk, the import prints a list of unresolved cwds up front. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) to recover those sessions.

### 4. Open the dashboard

Open the server URL in your browser. The dashboard shows repositories, sessions, and agents. It updates in real time as Claude Code sessions are active.

### Sessions MCP server for agents

The `kcap mcp sessions` stdio server lets coding agents search and recall past Capacitor sessions without leaving the chat. The Kurrent Capacitor plugin (installed by `kcap setup`) **auto-registers it for both Claude Code and Codex CLI** — no manual `claude mcp add` or TOML edit. The server is repo-aware: `cd` into a project before spawning your agent and `search_sessions` defaults to that repo's sessions.

## What it records

Once set up, Capacitor runs silently in the background. Every Claude Code (and Codex CLI, if you installed those hooks) session is captured automatically:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage
- **In-agent upgrade prompts** — in Claude Code sessions, when the server is running a newer kcap release than the local CLI, additional context is injected into the session so the agent can offer the user an upgrade via `npm install -g @kurrent/kcap`. The stderr `kcap` update hint continues to fire for direct command-line use.

## CLI commands

### Initial setup

```bash
kcap setup                                   # interactive wizard
kcap setup --server-url <url> --no-prompt    # CI / scripted
```

The setup wizard detects every supported coding agent and offers to install hooks for each, then configures the daemon. Claude Code and Codex CLI are detected via `PATH`; Cursor is detected by user-dir presence (`~/.cursor/`), so IDE users without the `cursor` shell command are covered. Re-run any time to update the configuration.

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
```

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

Score a recorded session against safety, plan adherence, quality, and efficiency criteria. Each of 13 questions (e.g. *"Did the agent run destructive commands?"*, *"Did it write tests when appropriate?"*, *"Were there repeated failed attempts at the same operation?"*) is answered by a separate headless Claude judge with **no tools** — the full compacted session trace is embedded in the prompt, so the judge reasons from evidence rather than hitting any external service.

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

The server is repo-aware — it resolves the current working directory to a repo hash at startup, and `search_sessions` defaults its `repo` filter to that hash unless you override it.

### Loading historical sessions

Backfill older sessions from every detected coding agent in a single run. All three agents ship per-session `.jsonl` transcripts (`~/.claude/projects/`, `~/.codex/sessions/`, `~/.cursor/projects/<sanitized-workspace>/agent-transcripts/`). They're discovered automatically and the command requires an explicit scope so personal/private repos aren't uploaded by accident:

```bash
kcap import --all                            # every discovered session from every agent
kcap import --org                            # sessions whose repo owner matches your active profile name
kcap import --repo owner/repo                # one specific repo
kcap import --repo .                         # the repo at the current cwd (must be a git repo with an origin remote)
```

Run `kcap import` with no scope on an interactive terminal to get a picker. Each run shows a confirmation summary (scope, matched count, repo samples, visibility) before uploading anything.

`--org` is a shortcut: it takes the active profile *name* and uses it as a GitHub org login to filter on. `kcap setup` names the profile after the picked tenant, so `--org` works out of the box for tenant-bound profiles; on the `default` profile, or a manually-named profile like `work`, use `--repo <owner/name>` instead (or run `kcap setup` to bind a profile to your org).

By default every available agent is imported. Pass one or more vendor filters to restrict the run:

```bash
kcap import --claude --org                   # only Claude transcripts
kcap import --codex --org                    # only Codex rollouts
kcap import --cursor --all                   # only Cursor — every discovered transcript
kcap import --cursor --cwd /path/to/proj     # only Cursor sessions whose workspace folder matches
```

Cursor historical import walks every JSONL transcript under `~/.cursor/projects/*/agent-transcripts/*/*.jsonl` and posts each line through the same `POST /hooks/transcript` route the live hook path uses, so live and historical ingest converge on one canonical event stream. The walker resolves each session's working directory by matching its sanitized workspace name against `~/Library/Application Support/Cursor/User/workspaceStorage/*/workspace.json` (on Linux: `~/.config/Cursor/User/...`); sessions whose workspace can't be resolved are still imported, just without `cwd` and git owner/repo enrichment.

Additional flags:

```bash
kcap import --org --yes                      # skip the confirmation prompt
kcap import --org --private                  # mark every imported session as Only Visible to You
kcap import --org --since 2026-01-01         # only sessions on or after this date
kcap import --org --cwd /path/to/project     # filter by working directory
kcap import --org --session abc123           # single session
```

Non-interactive runs (no TTY, e.g. CI) must pass both a scope flag and `--yes`. The command is idempotent and resumable — re-running with the same scope only uploads what's missing or incomplete. A server-side tracker deduplicates events on `(stream, eventId)` so previously-imported turns don't get re-appended.

After discovery, the import surfaces a one-shot report of any transcript working directories that no longer exist on disk. Sessions whose cwd was an ephemeral worktree (e.g. `~/dev/my-repo/.claude/worktrees/<slug>` or `~/dev/my-repo/.capacitor/worktrees/<slug>`) are transparently attributed to their parent project when that project still exists, so deleted-worktree paths drop out of the missing-cwds list. What remains is typically local repo dirs that have been renamed — those won't match an `--org` / `--repo` scope until you tell kcap how their old paths map to the new ones. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) below for the fix.

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
kcap daemon doctor                  # diagnose lock-file state for every daemon name
kcap daemon doctor --clean          # also remove stale lock/pid files (held entries are never touched)
```

`KCAP_DAEMON_NAME` overrides the active profile's daemon name (superseded by an explicit `--name` flag).

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

Installing with `--codex` (or `--skills`) writes five skills under `~/.agents/skills/`:

| Skill | Wraps | Purpose |
|---|---|---|
| `kcap-recap` | `kcap recap` | Session summary / continuation chain / repo history |
| `kcap-errors` | `kcap errors` | Tool-call error extraction |
| `kcap-hide` | `kcap hide` | Mark session owner-only |
| `kcap-disable` | `kcap disable` | Stop recording + delete server data |
| `kcap-validate-plan` | `kcap validate-plan` | Verify plan items were completed |

All five auto-resolve the active session from `CODEX_THREAD_ID`; pass `<sessionId>` explicitly to operate on a different session.

The daemon starts Codex with `--sandbox workspace-write` and `--ask-for-approval on-request`. This lets Codex edit files in the agent's worktree but escalates sensitive operations (e.g. network calls, shell commands outside the worktree) through the daemon's permission bridge to the dashboard.

> **Upgrading from an earlier version of kcap?** The npm postinstall hook refreshes all user-scope kcap installations on every `npm install -g @kurrent/kcap`, so you always pick up the current CLI version's skills (`~/.agents/skills/kcap-*`), Codex hook commands (`~/.codex/hooks.json`), and Claude plugin registration (`~/.claude/settings.json`). Each refresh is gated on a marker file written by your previous setup — fresh systems that never opted in are left untouched. Project-scope installs (`--project`) are not auto-refreshed; re-run `kcap plugin install [--codex] --project` after upgrading if you want the latest config for a specific repo.
>
> npm 11+ blocks install scripts by default. Add `allow-scripts[]=@kurrent/kcap` to your `~/.npmrc` (or pass `--allow-scripts=@kurrent/kcap` on the install command line) to opt in to the auto-refresh; otherwise re-run the relevant `kcap plugin install ... --if-installed` commands manually after each upgrade. (`npm approve-scripts` does not work for global installs — that's a known npm UX bug.)

PR review for hosted Codex agents is not yet supported (tracked in AI-632). The sandbox and approval-mode selectors in the launch dialog are also planned as a follow-up (AI-633).

#### Cursor IDE hooks

Cursor is detected by the presence of `~/.cursor/` — you don't need the `cursor` shell command on `PATH`. If `kcap setup` found Cursor and you said yes, hooks are already in place. To install or remove later:

```bash
kcap plugin install --cursor                # writes ~/.cursor/hooks.json
kcap plugin remove --cursor                 # remove Cursor hooks
```

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

**Default session visibility** controls how your sessions appear to other users. Set during `kcap setup` or change at any time:

```bash
kcap config set default_visibility private      # only you can see your sessions
kcap config set default_visibility org_public   # org repos visible, others private (default)
kcap config set default_visibility public       # all sessions visible to everyone
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

`uninstall` covers every supported agent: it stops running daemons and watcher processes, strips kcap entries from user-level Claude Code, Codex CLI, and Cursor hook files (preserving any non-kcap entries), removes agent skills under `~/.agents/skills/` (plus the legacy `~/.codex/skills/kcap-*` folders), and deletes `~/.config/kcap/`.

`--project` additionally cleans up `<repo>/.claude/settings.local.json` and `<repo>/.codex/hooks.json` in the current git working tree (errors if you're not inside one). Cursor only has a user-scope `hooks.json`, so `--project` does not affect it. Project-scope hooks in other repos are not touched — re-run from each repo that has them.

Use `--keep-config` to preserve profiles, tokens, and ignore lists when you plan to reinstall. Per-agent selective cleanup is not exposed here — use `kcap plugin remove [--codex|--cursor|--skills]` for finer-grained removal.

### Other commands

```bash
kcap status         # server health check
kcap whoami         # show current authenticated user
kcap login          # authenticate via OAuth (browser flow by default)
kcap login --device # force device-code flow (use in SSH / headless envs)
kcap update         # check for CLI updates
kcap logout         # delete stored tokens
```

`kcap whoami` output varies by account type:

Personal account tenant:
```
Personal account: @alice
Provider: GitHubApp
Token expires in 23h
Server: https://kapacitor.alice.example
```

Organization tenant:
```
Organization: acme
Provider: GitHubApp
Token expires in 23h
Server: https://kapacitor.acme.example
```

## Upgrading from v1

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kcap` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

    kcap config set server_url https://my-tenant.kcap.ai

Or remove the config file and re-run setup:

    rm ~/.config/kcap/config.json
    kcap setup

## License

[Kurrent License v1](LICENSE)
