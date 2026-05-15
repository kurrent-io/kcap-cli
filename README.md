# Kurrent Capacitor

Full observability for your Claude Code sessions. Record every session, visualize agent activity in real time, and review code changes grounded in the actual development transcripts.

Capacitor captures the complete picture — session lifecycle, transcript data, subagent trees, tool usage, and token consumption — then surfaces it through a real-time dashboard and PR review tools that give you context no diff can provide.

## Getting started

You need the server URL from your admin (e.g. `https://capacitor.example.com`).

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
4. **Claude Code plugin** — installs hooks, skills, and collaborative memory (user-wide or project-only)
5. **Agent daemon** — configure the daemon name for remote agent execution

Verify with `kapacitor whoami` and `kapacitor status`.

For non-interactive environments:

```bash
kapacitor setup --server-url https://capacitor.example.com --default-visibility org_public --no-prompt
```

#### Also using Codex CLI?

`setup` installs Claude Code hooks only. If you also use Codex CLI and want those sessions captured too, install the Codex hook surface separately:

```bash
kapacitor plugin install --codex            # user-wide  (~/.codex/hooks.json)
kapacitor plugin install --codex --project  # this repo only (<repo>/.codex/hooks.json)
kapacitor plugin remove --codex             # uninstall
```

`--codex` also installs two Codex skills into `~/.codex/skills/` — `kapacitor-recap` and `kapacitor-errors` — so the Codex CLI can pull repo session summaries and tool-error reports on demand. Codex sessions don't auto-populate `KAPACITOR_SESSION_ID`, so the recap skill leads with `kapacitor recap --repo` and falls back to explicit session IDs. The `validate-plan` skill is Claude Code–only (no Codex plan mode equivalent).

After a `--project` install, Codex won't actually run the hooks until you trust the directory: run `codex` once in the repo and accept the trust prompt. Skills are always installed user-wide regardless of `--project`.

`kapacitor status` reports installation state for the user-wide Claude Code and Codex hook surfaces — it does not currently detect `--project` installs. For a `--project` install, check that `<repo>/.claude/settings.local.json` or `<repo>/.codex/hooks.json` exists and contains kapacitor entries.

### 3. Import existing sessions (optional)

```bash
kapacitor history --org                          # sessions for the org bound to your active profile
kapacitor history --repo owner/repo              # sessions for one specific repo
kapacitor history --codex --org                  # same, but from Codex rollouts (~/.codex/sessions)
```

This backfills your past sessions from `~/.claude/projects/` (or `~/.codex/sessions` with `--codex`) so they appear in the dashboard. It's idempotent — safe to run multiple times.

You must pick an explicit scope (`--all`, `--org`, or `--repo`) so personal/private repos aren't uploaded by accident. `--org` uses the active profile name as the GitHub org login — it works out of the box when the profile was created by `kapacitor setup` (which names it after the picked tenant), and errors otherwise. Run with no scope on an interactive terminal to get a picker. See [Loading historical sessions](#loading-historical-sessions) for the full set of flags.

### 4. Open the dashboard

Open the server URL in your browser. The dashboard shows repositories, sessions, and agents. It updates in real time as Claude Code sessions are active.

## What it records

Once set up, Capacitor runs silently in the background. Every Claude Code (and Codex CLI, if you installed those hooks) session is captured automatically:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage

## CLI commands

### Session recap

By default, shows a concise AI-generated summary — why the work was done, key decisions, and anything left unfinished. Use `--full` for the complete transcript with all prompts, responses, and file changes.

```bash
kapacitor recap <sessionId>              # summary (default)
kapacitor recap --full <sessionId>       # full transcript
kapacitor recap --chain <sessionId>      # summaries across continuation chain
kapacitor recap --chain --full <sessionId>  # full transcript across chain
```

The identifier can be a session GUID or a meta session slug. Find these from the dashboard or the current session's hook payloads. When run inside a Claude Code session with the kapacitor plugin, the session ID is set automatically.

If the kapacitor plugin is installed, you can also use the `/kapacitor:session-recap` skill inside Claude Code, or just ask:

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
kapacitor review <pr-url>
```

Launches a Claude Code session equipped with MCP tools that query the implementation transcripts. Reviewers can ask *why* code was changed, understand design decisions, check what alternatives were considered, and verify test coverage — all grounded in what actually happened during development.

### Loading historical sessions

Backfill older sessions from local transcript files. The command requires an explicit scope so personal/private repos aren't uploaded by accident:

```bash
kapacitor history --all                            # every discovered session
kapacitor history --org                            # sessions whose repo owner matches your active profile name
kapacitor history --repo owner/repo                # one specific repo
kapacitor history --repo .                         # the repo at the current cwd (must be a git repo with an origin remote)
```

Run `kapacitor history` with no scope on an interactive terminal to get a picker. Each run shows a confirmation summary (scope, matched count, repo samples, visibility) before uploading anything.

`--org` is a shortcut: it takes the active profile *name* and uses it as a GitHub org login to filter on. `kapacitor setup` names the profile after the picked tenant, so `--org` works out of the box for tenant-bound profiles; on the `default` profile, or a manually-named profile like `work`, use `--repo <owner/name>` instead (or run `kapacitor setup` to bind a profile to your org).

Additional flags:

```bash
kapacitor history --org --yes                      # skip the confirmation prompt
kapacitor history --org --private                  # mark every imported session as Only Visible to You
kapacitor history --codex --org                    # Codex rollouts from ~/.codex/sessions
kapacitor history --org --since 2026-01-01         # only sessions on or after this date
kapacitor history --org --cwd /path/to/project     # filter by working directory
kapacitor history --org --session abc123           # single session
```

Non-interactive runs (no TTY, e.g. CI) must pass both a scope flag and `--yes`. The command is idempotent and resumable — re-running with the same scope only uploads what's missing or incomplete.

### Agent daemon

The agent daemon connects to the Capacitor server and runs Claude Code agents in isolated git worktrees, controlled from the dashboard.

```bash
kapacitor agent start                   # start in foreground (defaults --name to your OS username)
kapacitor agent start -d                # start in background (daemonize)
kapacitor agent start --name laptop -d  # run multiple daemons on the same machine by giving each a unique name
kapacitor agent status                  # list all running daemons
kapacitor agent status --name laptop    # show status of a specific daemon
kapacitor agent stop --name laptop      # stop just that one
kapacitor agent stop --yes              # stop all running daemons unattended (otherwise prompts on multi)
kapacitor agent doctor                  # diagnose lock-file state for every daemon name
kapacitor agent doctor --clean          # also remove stale lock/pid files (held entries are never touched)
```

Each daemon process holds an exclusive `flock` on `~/.config/kapacitor/agents/<name>.lock` for its entire lifetime. The kernel releases the lock automatically when the daemon exits (including `SIGKILL` or power-off), so leftover lock files on disk are never a blocker — only a live process holding the kernel-level lock can prevent another daemon from acquiring the same name.

Two daemons with **different** `--name` values can run side-by-side. Two daemons under the **same name** on the same machine collide on the flock and the second one exits with code 2. Even if that guard is bypassed somehow, the server rejects the second daemon's `DaemonConnect` with a typed error and the second daemon exits with code 3 — no more silent slot-displacement oscillation.

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
kapacitor profile add work --server-url https://cap.example.com
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

    kapacitor config set server_url https://your-server.example.com

Or remove the config file and re-run setup:

    rm ~/.config/kapacitor/config.json
    kapacitor setup

## License

[Kurrent License v1](LICENSE)
