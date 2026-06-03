# Kurrent Capacitor

Full observability for your Claude Code sessions. Record every session, visualize agent activity in real time, and review code changes grounded in the actual development transcripts.

Capacitor captures the complete picture — session lifecycle, transcript data, subagent trees, tool usage, and token consumption — then surfaces it through a real-time dashboard and PR review tools that give you context no diff can provide.

## Getting started

You need the server URL from your admin (e.g. `https://my-tenant.kapacitor.ai`).

### 1. Install the CLI

```bash
npm install -g @kurrent/kapacitor
```

npm automatically selects the right native binary for your platform:

| Platform | Architecture |
|----------|-------------|
| macOS | ARM64 (Apple Silicon) |
| Linux | x64, ARM64 |
| Linux (Alpine/musl) | x64, ARM64 |
| Windows | x64 |

The CLI is compiled with NativeAOT — fast startup, no runtime dependency.

### 2. Run setup

```bash
kapacitor setup
```

The setup wizard walks you through:

1. **Server URL** — enter the URL your admin provided
2. **Login** — authenticates via GitHub Device Flow (if the server requires auth)
3. **Default visibility** — choose how your sessions are visible to others
4. **Coding-agent hooks** — detects Claude Code and Codex CLI on `PATH`, and Cursor by user-dir presence (`~/.cursor/`), then offers to install hooks/skills for each (user-wide)
5. **Daemon** — configure the daemon name for remote agent execution

Verify with `kapacitor whoami` and `kapacitor status`.

For non-interactive environments:

```bash
kapacitor setup --server-url https://my-tenant.kapacitor.ai --default-visibility org_public --no-prompt
```

In `--no-prompt` mode, the wizard installs hooks for every detected agent by default. Opt out per agent with `--skip-claude-hooks`, `--skip-codex-hooks`, and/or `--skip-cursor-hooks`.

> **Need hooks for an agent installed after setup, or scoped to a single repo?**
> Run `kapacitor plugin install [--codex|--cursor]` (omit the flag for the Claude Code plugin), or pair Codex with `--project` for a per-repo install. Use `--skills` instead of `--codex` if you only want the agent skills without Codex hooks. Cursor uses user-scope only — `--project` has no effect with `--cursor`. After installing Codex hooks, run `/hooks` inside Codex and trust each kapacitor entry — Codex doesn't execute hooks until each is explicitly trusted. After a `--project` install, also run `codex` once in the repo and accept the trust prompt. Re-running after a kapacitor upgrade is rarely needed for user-scope installs — the npm postinstall hook auto-refreshes them on every `npm install -g @kurrent/kapacitor`.

> **Need at least one agent to capture sessions:** the setup wizard runs to completion without an agent CLI on `PATH` (it'll still configure your profile, auth, and daemon), but kapacitor only records work once Claude Code or Codex CLI is installed and the hooks are in place.

### 3. Import existing sessions (optional)

```bash
kapacitor import                     # every detected agent (Claude, Codex, Cursor)
kapacitor import --org               # sessions for the org bound to your active profile
kapacitor import --repo owner/repo   # sessions for one specific repo
kapacitor import --cursor            # only Cursor
```

This backfills your past sessions from `~/.claude/projects/` (Claude), `~/.codex/sessions/` (Codex), and `~/.cursor/projects/.../agent-transcripts/` (Cursor) so they appear in the dashboard. All agents are discovered automatically — pass `--claude`, `--codex`, or `--cursor` (one or more) to narrow the run. All forms are idempotent — safe to run multiple times.

You must pick an explicit scope (`--all`, `--org`, or `--repo`) so personal/private repos aren't uploaded by accident. `--org` uses the active profile name as the GitHub org login — it works out of the box when the profile was created by `kapacitor setup` (which names it after the picked tenant), and errors otherwise. Run with no scope on an interactive terminal to get a picker. See [Loading historical sessions](#loading-historical-sessions) for the full set of flags.

### 4. Open the dashboard

Open the server URL in your browser. The dashboard shows repositories, sessions, and agents. It updates in real time as Claude Code sessions are active.

### Sessions MCP server for agents

The `kapacitor mcp sessions` stdio server lets coding agents search and recall past Capacitor sessions without leaving the chat. The Kapacitor plugin (installed by `kapacitor setup`) **auto-registers it for both Claude Code and Codex CLI** — no manual `claude mcp add` or TOML edit. The server is repo-aware: `cd` into a project before spawning your agent and `search_sessions` defaults to that repo's sessions.

## What it records

Once set up, Capacitor runs silently in the background. Every Claude Code (and Codex CLI, if you installed those hooks) session is captured automatically:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage

## CLI commands

### Initial setup

```bash
kapacitor setup                                   # interactive wizard
kapacitor setup --server-url <url> --no-prompt    # CI / scripted
```

The setup wizard detects every supported coding agent and offers to install hooks for each, then configures the daemon. Claude Code and Codex CLI are detected via `PATH`; Cursor is detected by user-dir presence (`~/.cursor/`), so IDE users without the `cursor` shell command are covered. Re-run any time to update the configuration.

In `--no-prompt` mode, hooks install for every detected agent by default. Opt out per agent:

```bash
kapacitor setup --server-url <url> --no-prompt --skip-codex-hooks --skip-cursor-hooks   # only Claude
kapacitor setup --server-url <url> --no-prompt --skip-claude-hooks --skip-cursor-hooks  # only Codex
kapacitor setup --server-url <url> --no-prompt --skip-claude-hooks --skip-codex-hooks   # only Cursor
```

After installing Codex hooks, run `/hooks` inside Codex and trust each kapacitor entry — Codex does not execute hooks until each is explicitly trusted. For project-scope installs (a single repo), use `kapacitor plugin install [--codex] --project` after setup.

Legacy `--plugin-scope <user|project|skip>` is retained for backwards compatibility:

- `user` — no-op (matches the new default)
- `project` — install the Claude Code plugin into `<repo>/.claude/settings.local.json`. Must be run from inside a git working tree; setup exits with an error otherwise.
- `skip` — alias for `--skip-claude-hooks`

New scripts should prefer `--skip-claude-hooks` / `--skip-codex-hooks` and `kapacitor plugin install --project` for project scope.

If you run `kapacitor setup` outside any git working tree, it still completes — hooks install user-scope and fire for every session — but a tip at the end reminds you that sessions recorded from non-repo directories won't capture owner/repo/branch/PR context.

### Session recap

By default, shows a concise AI-generated summary — why the work was done, key decisions, and anything left unfinished. Use `--full` for the complete transcript with all prompts, responses, and file changes.

```bash
kapacitor recap <sessionId>              # summary (default)
kapacitor recap --full <sessionId>       # full transcript
kapacitor recap --chain <sessionId>      # summaries across continuation chain
kapacitor recap --chain --full <sessionId>  # full transcript across chain
```

The identifier can be a session GUID or a meta session slug. Find these from the dashboard or the current session's hook payloads. When run inside a Claude Code session with the kapacitor plugin, the session ID is set automatically.

If the kapacitor plugin is installed, you can also use the `/kapacitor:recap` skill inside Claude Code, or just ask:

```
Recap session c4de7fbe-cff5-4e2c-bf80-9858d02f58be and propose what should be done next.
```

### Plan validation

Verify that all items in a session's plan were completed.

```bash
kapacitor validate-plan <sessionId>
```

With the plugin installed, use the `/kapacitor:validate-plan` skill or ask naturally:

```
Did I finish everything in the plan? Check what's left to do.
```

### Hide session

Mark a session as owner-only so other users no longer see it in the dashboard.

```bash
kapacitor hide                 # current session
kapacitor hide <sessionId>     # specific session
```

### Disable recording

Stop the watcher, silence future hooks, and delete server-side data for a session.

```bash
kapacitor disable                 # current session
kapacitor disable <sessionId>     # specific session
```

This is irreversible on the server side; the local transcript file is untouched.

### Error extraction

Scan a recorded session for tool call errors — failed bash commands, file read/write errors, agent failures, etc.

```bash
kapacitor errors <sessionId>              # single session
kapacitor errors <meta-session-slug>      # meta session
kapacitor errors --chain <sessionId>      # full continuation chain
```

Useful for post-session review: identify recurring mistakes, discover patterns to avoid, and update project instructions accordingly.

### Session evaluation (LLM-as-judge)

Score a recorded session against safety, plan adherence, quality, and efficiency criteria. Each of 13 questions (e.g. *"Did the agent run destructive commands?"*, *"Did it write tests when appropriate?"*, *"Were there repeated failed attempts at the same operation?"*) is answered by a separate headless Claude judge with **no tools** — the full compacted session trace is embedded in the prompt, so the judge reasons from evidence rather than hitting any external service.

```bash
kapacitor eval <sessionId>                      # default: sonnet judge
kapacitor eval --model opus <sessionId>         # stronger judge
kapacitor eval --chain <sessionId>              # include the full continuation chain
kapacitor eval --threshold 5000 <sessionId>     # keep more of each tool output before truncation
kapacitor eval --questions safety <sessionId>   # run only the 4 safety judges
kapacitor eval --skip efficiency <sessionId>    # run everything except efficiency
kapacitor eval --list-questions                 # print the question taxonomy
```

Output is a per-category + overall score (1-5, with `pass`/`warn`/`fail` verdicts), with a specific finding and supporting evidence per question. The aggregate is also persisted back to the session's stream as a `SessionEvalCompleted` event, so past evaluations can be queried from the dashboard or used to track quality trends across sessions.

Expect ~1-3 minutes total depending on the model and session size — judges run sequentially.

### PR review with full context

```bash
kapacitor review <pr-url-or-owner/repo#number>
```

Launches a Claude Code session equipped with MCP tools that query the implementation transcripts. Reviewers can ask *why* code was changed, understand design decisions, check what alternatives were considered, and verify test coverage — all grounded in what actually happened during development.

The same MCP server (`kapacitor-review`) is also auto-registered by the Kapacitor plugin and available in any Claude Code session, not just ones launched via `kapacitor review`. Each PR-scoped tool (`get_pr_summary`, `list_pr_files`, `get_file_context`, `search_context`, `list_sessions`) accepts an optional `pr` argument — pass `"owner/repo#123"` or a GitHub PR URL to review any PR from any branch. When omitted, the server falls back to the PR passed at startup (set by `kapacitor review <pr>`) or to git auto-detection against the current branch. `get_transcript` keys off `session_id` and doesn't need a `pr` argument.

### Sessions MCP server (for agents)

```bash
kapacitor mcp sessions
```

Stdio MCP server that exposes past Capacitor sessions to coding agents (Claude Code, Codex) so they can search and recall prior work without leaving the chat. The Kapacitor plugin auto-registers it for both Claude Code (via `.mcp.json`) and Codex CLI (via `.codex-plugin/plugin.json` → `.codex-mcp.json`), so there's nothing extra to do after `kapacitor setup`. If you installed the kapacitor plugin via Codex's native plugin manager (rather than `kapacitor setup` / `kapacitor plugin install --codex`), the MCP server is still auto-registered, but you'll also want to run `kapacitor plugin install --codex` to get hooks and agent skills.

It provides three tools:

- **`search_sessions`** — free-text search over past sessions (and subagent transcripts) in the current repo. Pass `repo: "all"` to search across every repo you can see, or `repo: "owner/name"` for a different one. Filter by `author` / `author_github_id`. Returns ranked hits with `session_id`, snippet, and (for transcript hits) `hit_event_index` + `agent_id` for drilling in.
- **`get_session_summary`** — concise `summary_text` + `plan` for a session. Use this to orient before reading the transcript.
- **`get_session_transcript`** — speaker-tagged events from a session. Pair `around_event` (and `agent_id` if the hit was in a subagent) with the values returned by `search_sessions` to fetch the exact decision context.

The server is repo-aware — it resolves the current working directory to a repo hash at startup, and `search_sessions` defaults its `repo` filter to that hash unless you override it.

### Loading historical sessions

Backfill older sessions from every detected coding agent in a single run. All three agents ship per-session `.jsonl` transcripts (`~/.claude/projects/`, `~/.codex/sessions/`, `~/.cursor/projects/<sanitized-workspace>/agent-transcripts/`). They're discovered automatically and the command requires an explicit scope so personal/private repos aren't uploaded by accident:

```bash
kapacitor import --all                            # every discovered session from every agent
kapacitor import --org                            # sessions whose repo owner matches your active profile name
kapacitor import --repo owner/repo                # one specific repo
kapacitor import --repo .                         # the repo at the current cwd (must be a git repo with an origin remote)
```

Run `kapacitor import` with no scope on an interactive terminal to get a picker. Each run shows a confirmation summary (scope, matched count, repo samples, visibility) before uploading anything.

`--org` is a shortcut: it takes the active profile *name* and uses it as a GitHub org login to filter on. `kapacitor setup` names the profile after the picked tenant, so `--org` works out of the box for tenant-bound profiles; on the `default` profile, or a manually-named profile like `work`, use `--repo <owner/name>` instead (or run `kapacitor setup` to bind a profile to your org).

By default every available agent is imported. Pass one or more vendor filters to restrict the run:

```bash
kapacitor import --claude --org                   # only Claude transcripts
kapacitor import --codex --org                    # only Codex rollouts
kapacitor import --cursor --all                   # only Cursor — every discovered transcript
kapacitor import --cursor --cwd /path/to/proj     # only Cursor sessions whose workspace folder matches
```

Cursor historical import walks every JSONL transcript under `~/.cursor/projects/*/agent-transcripts/*/*.jsonl` and posts each line through the same `POST /hooks/transcript` route the live hook path uses, so live and historical ingest converge on one canonical event stream. The walker resolves each session's working directory by matching its sanitized workspace name against `~/Library/Application Support/Cursor/User/workspaceStorage/*/workspace.json` (on Linux: `~/.config/Cursor/User/...`); sessions whose workspace can't be resolved are still imported, just without `cwd` and git owner/repo enrichment.

Additional flags:

```bash
kapacitor import --org --yes                      # skip the confirmation prompt
kapacitor import --org --private                  # mark every imported session as Only Visible to You
kapacitor import --org --since 2026-01-01         # only sessions on or after this date
kapacitor import --org --cwd /path/to/project     # filter by working directory
kapacitor import --org --session abc123           # single session
```

Non-interactive runs (no TTY, e.g. CI) must pass both a scope flag and `--yes`. The command is idempotent and resumable — re-running with the same scope only uploads what's missing or incomplete. A server-side tracker deduplicates events on `(stream, eventId)` so previously-imported turns don't get re-appended.

### Daemon

The daemon connects to the Capacitor server and runs Claude Code or Codex agents in isolated git worktrees, controlled from the dashboard. The daemon supports hosted Claude and Codex agents on macOS and Linux — choose the vendor from the dashboard's launch dialog. At startup the daemon probes `daemon.claude_path` and `daemon.codex_path` and advertises only the vendors it can actually spawn, so the launch dialog hides whichever agent isn't installed on the selected daemon.

```bash
kapacitor daemon start                   # start in foreground (defaults --name to your OS username)
kapacitor daemon start -d                # start in background (daemonize)
kapacitor daemon start --name laptop -d  # run multiple daemons on the same machine by giving each a unique name
kapacitor daemon status                  # list all running daemons
kapacitor daemon status --name laptop    # show status of a specific daemon
kapacitor daemon stop --name laptop      # stop just that one
kapacitor daemon stop --yes              # stop all running daemons unattended (otherwise prompts on multi)
kapacitor daemon doctor                  # diagnose lock-file state for every daemon name
kapacitor daemon doctor --clean          # also remove stale lock/pid files (held entries are never touched)
```

Each daemon process holds an exclusive `flock` on `~/.config/kapacitor/daemons/<name>.lock` for its entire lifetime. The kernel releases the lock automatically when the daemon exits (including `SIGKILL` or power-off), so leftover lock files on disk are never a blocker — only a live process holding the kernel-level lock can prevent another daemon from acquiring the same name.

Two daemons with **different** `--name` values can run side-by-side. Two daemons under the **same name** on the same machine collide on the flock and the second one exits with code 2. Even if that guard is bypassed somehow, the server rejects the second daemon's `DaemonConnect` with a typed error and the second daemon exits with code 3 — no more silent slot-displacement oscillation.

#### Hosted Codex agents

Hosted Codex agents require the Codex hook surface — if you said yes during `kapacitor setup`, you already have it. Otherwise install it manually:

Codex CLI 0.81+ exports `CODEX_THREAD_ID`; kapacitor reads it the same way it reads `KAPACITOR_SESSION_ID` for Claude sessions — no manual session ID needed for any of the Codex skills (`kapacitor-recap`, `kapacitor-errors`, `kapacitor-hide`, `kapacitor-disable`, `kapacitor-validate-plan`).

```bash
kapacitor plugin install --codex                          # user scope (~/.codex/hooks.json + ~/.agents/skills/)
kapacitor plugin install --codex --project                # project scope (<repo>/.codex/hooks.json), skills still user-wide
kapacitor plugin install --skills                         # skills only (~/.agents/skills/), no Codex hooks
kapacitor plugin install --skills --if-installed          # refresh only if skills were previously installed (used by npm postinstall, harmless to call by hand)
kapacitor plugin install --codex --if-installed           # refresh Codex hooks only if previously installed (used by npm postinstall)
kapacitor plugin install --if-installed                   # refresh Claude plugin registration only if previously installed (used by npm postinstall)
```

Installing with `--codex` (or `--skills`) writes five skills under `~/.agents/skills/`:

| Skill | Wraps | Purpose |
|---|---|---|
| `kapacitor-recap` | `kapacitor recap` | Session summary / continuation chain / repo history |
| `kapacitor-errors` | `kapacitor errors` | Tool-call error extraction |
| `kapacitor-hide` | `kapacitor hide` | Mark session owner-only |
| `kapacitor-disable` | `kapacitor disable` | Stop recording + delete server data |
| `kapacitor-validate-plan` | `kapacitor validate-plan` | Verify plan items were completed |

All five auto-resolve the active session from `CODEX_THREAD_ID`; pass `<sessionId>` explicitly to operate on a different session.

The daemon starts Codex with `--sandbox workspace-write` and `--ask-for-approval on-request`. This lets Codex edit files in the agent's worktree but escalates sensitive operations (e.g. network calls, shell commands outside the worktree) through the daemon's permission bridge to the dashboard.

> **Upgrading from an earlier version of kapacitor?** The npm postinstall hook refreshes all user-scope kapacitor installations on every `npm install -g @kurrent/kapacitor`, so you always pick up the current CLI version's skills (`~/.agents/skills/kapacitor-*`), Codex hook commands (`~/.codex/hooks.json`), and Claude plugin registration (`~/.claude/settings.json`). Each refresh is gated on a marker file written by your previous setup — fresh systems that never opted in are left untouched. Project-scope installs (`--project`) are not auto-refreshed; re-run `kapacitor plugin install [--codex] --project` after upgrading if you want the latest config for a specific repo.

PR review for hosted Codex agents is not yet supported (tracked in AI-632). The sandbox and approval-mode selectors in the launch dialog are also planned as a follow-up (AI-633).

#### Cursor IDE hooks

Cursor is detected by the presence of `~/.cursor/` — you don't need the `cursor` shell command on `PATH`. If `kapacitor setup` found Cursor and you said yes, hooks are already in place. To install or remove later:

```bash
kapacitor plugin install --cursor                # writes ~/.cursor/hooks.json
kapacitor plugin remove --cursor                 # remove Cursor hooks
```

Cursor uses a single user-scope `hooks.json`; there is no project-scope variant.

`kapacitor setup` writes all 8 supported Cursor hook entries. Use `--skip-cursor-hooks` to opt out during setup:

```bash
kapacitor setup --server-url <url> --no-prompt --skip-cursor-hooks
```

#### Daemon config settings

Use `kapacitor config set` to configure the binary paths used by the daemon. The values are stored in the active profile and take effect the next time the daemon starts.

```bash
kapacitor config set daemon.claude_path /opt/claude/bin/claude
kapacitor config set daemon.codex_path  /opt/codex/bin/codex
```

| Key | Default | Description |
|-----|---------|-------------|
| `daemon.claude_path` | `"claude"` | Path to the Claude CLI binary. Resolved via `PATH` when not an absolute path. |
| `daemon.codex_path`  | `"codex"`  | Path to the Codex CLI binary. Resolved via `PATH` when not an absolute path. |

You can also override these at runtime with environment variables (take precedence over the profile):

```bash
KAPACITOR_CLAUDE_PATH=/opt/claude/bin/claude kapacitor daemon
KAPACITOR_CODEX_PATH=/opt/codex/bin/codex  kapacitor daemon
```

### Repository paths

Manage known repo paths for the agent launch dialog. Repos are automatically added when agents are launched, but you can also manage the list manually:

```bash
kapacitor repos                    # list known repos (sorted by last used)
kapacitor repos add .              # add current directory
kapacitor repos add ~/dev/project  # add a specific path
kapacitor repos remove ~/dev/old   # remove a path
```

Known repos are persisted to `~/.config/kapacitor/repos.json` and reported to the server when the daemon connects, so the launch dialog always shows previously-used repos even after restarts.

### Profiles

Profiles let you work with multiple Capacitor servers — for example, a company server for work repos and a separate one for open-source projects. Each profile stores its own server URL, visibility settings, and daemon configuration.

```bash
kapacitor profile add work --server-url https://my-other-tenant.kapacitor.ai
kapacitor profile add oss --server-url https://cap.oss.dev --remote "github.com/myorg/*"
kapacitor profile list
kapacitor profile show work
kapacitor profile remove work
```

The `--remote` flag associates a profile with git remote patterns. When you open a repo whose remote matches a pattern, that profile activates automatically.

#### Switching profiles

```bash
kapacitor use work                  # bind 'work' profile to current repo/directory
kapacitor use work --global         # set 'work' as the global default
kapacitor use oss --save            # bind and write .kapacitor.json for team sharing
```

Without `--global`, `use` binds the profile to the current git repo root (or the current directory if not in a repo). With `--save`, it writes a `.kapacitor.json` file that can be committed so the whole team uses the same profile.

#### Profile resolution order

The CLI resolves which profile to use in this order:

1. `--server-url` CLI flag
2. `KAPACITOR_URL` environment variable
3. `KAPACITOR_PROFILE` environment variable
4. `.kapacitor.json` in the repo root (or current directory if not in a repo)
5. Git remote pattern matching from `--remote` flags
6. Directory binding from `kapacitor use`
7. Global active profile (or `default`)

### Configuration

```bash
kapacitor config show    # show current configuration
kapacitor config set <key> <value>
```

**Default session visibility** controls how your sessions appear to other users. Set during `kapacitor setup` or change at any time:

```bash
kapacitor config set default_visibility private      # only you can see your sessions
kapacitor config set default_visibility org_public   # org repos visible, others private (default)
kapacitor config set default_visibility public       # all sessions visible to everyone
```

**Repository exclusions** prevent specific repos from sending any data to the server — hooks are silently skipped, no session is recorded:

```bash
kapacitor config set excluded_repos "myorg/secret-project,personal/diary"
```

**Path exclusions** silently skip any session whose working directory is, or sits inside, a configured path — useful for ignoring scratch dirs, worktrees, or monorepo subtrees regardless of git remote:

```bash
kapacitor ignore .                       # ignore the current directory
kapacitor ignore ~/code/secret-project   # ignore a specific tree
kapacitor ignore --list                  # show all ignored paths
kapacitor ignore --remove ~/code/secret-project
```

Entries are stored on the **active profile**, so switching profiles with `kapacitor use` switches the ignore list too. Symlinks are resolved on both the stored entry and the session's reported cwd, so a worktree symlink and its target match.

### Uninstalling

To remove kapacitor from this machine, run:

```bash
kapacitor uninstall                  # interactive, user-scope removal
kapacitor uninstall --yes            # non-interactive
kapacitor uninstall --project --yes  # also strip project-scope hooks in cwd's repo
kapacitor uninstall --keep-config    # remove integrations, keep ~/.config/kapacitor
```

`uninstall` covers every supported agent: it stops running daemons and watcher processes, strips kapacitor entries from user-level Claude Code, Codex CLI, and Cursor hook files (preserving any non-kapacitor entries), removes agent skills under `~/.agents/skills/` (plus the legacy `~/.codex/skills/kapacitor-*` folders), and deletes `~/.config/kapacitor/`.

`--project` additionally cleans up `<repo>/.claude/settings.local.json` and `<repo>/.codex/hooks.json` in the current git working tree (errors if you're not inside one). Cursor only has a user-scope `hooks.json`, so `--project` does not affect it. Project-scope hooks in other repos are not touched — re-run from each repo that has them.

Use `--keep-config` to preserve profiles, tokens, and ignore lists when you plan to reinstall. Per-agent selective cleanup is not exposed here — use `kapacitor plugin remove [--codex|--cursor|--skills]` for finer-grained removal.

### Other commands

```bash
kapacitor status         # server health check
kapacitor whoami         # show current authenticated user
kapacitor login          # authenticate via OAuth (browser flow by default)
kapacitor login --device # force device-code flow (use in SSH / headless envs)
kapacitor update         # check for CLI updates
kapacitor logout         # delete stored tokens
```

## Upgrading from v1

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kapacitor` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

    kapacitor config set server_url https://my-tenant.kapacitor.ai

Or remove the config file and re-run setup:

    rm ~/.config/kapacitor/config.json
    kapacitor setup

## License

[Kurrent License v1](LICENSE)
