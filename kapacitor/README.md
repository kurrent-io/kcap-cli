# Kapacitor Plugin

This plugin integrates [Kurrent Capacitor](../README.md) with Claude Code and Codex CLI by automatically registering lifecycle hooks, providing skills for session review, and auto-installing MCP servers that expose past-session context to the agent.

## What it does

**MCP servers** — Two stdio servers, both auto-registered on plugin install (no manual `claude mcp add` or `~/.config/codex/mcp_servers.toml` edit):

### `kapacitor-sessions`

Search and recall past Kurrent Capacitor sessions from inside the agent.

| Tool | Description |
|------|-------------|
| `search_sessions` | Free-text + author search over past sessions (and subagent transcripts), defaulted to the cwd's repo |
| `get_session_summary` | Concise `summary_text` + `plan` for a session |
| `get_session_transcript` | Speaker-tagged transcript window, with `around_event` drill-in for search hits |

Repo-aware: it resolves the cwd to a repo hash at startup, so `search_sessions` defaults to *this* repo.

### `kapacitor-review`

PR review context tools, available automatically when the agent is on a branch with an open PR. The MCP server auto-detects the current repo and PR from git; if you're not on a PR branch, the tools return a helpful message suggesting `kapacitor review <pr>`.

| Tool | Description |
|------|-------------|
| `get_pr_summary` | Overview: sessions, files changed, test runs |
| `list_pr_files` | Files changed with session links and event counts |
| `get_file_context` | Why a specific file was changed, with transcript excerpts |
| `search_context` | Free-text search across session transcripts |
| `list_sessions` | Sessions that contributed to the PR |
| `get_transcript` | Full transcript of a specific session |

`kapacitor mcp judge` is intentionally not auto-registered. Add it with `claude mcp add kapacitor-judge -- kapacitor mcp judge` if you want it.

**Hooks** — Automatically captures session activity and forwards it to the Kurrent Capacitor server:

| Hook | Event |
|------|-------|
| `SessionStart` | Session begins |
| `SessionEnd` | Session ends |
| `SubagentStart` | Subagent spawned |
| `SubagentStop` | Subagent finished |
| `Notification` | Permission/idle prompts |
| `Stop` | Claude finishes a turn |

Each hook pipes its JSON payload through the `kapacitor` CLI, which enriches it with git/PR info and forwards it to the server. A background watcher process streams transcript lines in real time.

**Skills** — Slash commands for reviewing recorded sessions. Available on both harnesses (Claude reads `skills/`, Codex reads `codex-skills/`):

- `kapacitor-recap` — Retrieve a structured summary of a session (user prompts, assistant responses, plans, file changes)
- `kapacitor-errors` — Extract tool call errors from a session for post-session review and pattern detection
- `kapacitor-validate-plan` — Verify that all planned items were completed
- `kapacitor-disable` — Stop recording and delete all server data for the current session
- `kapacitor-hide` — Hide the current session (owner-only visibility)

In Claude they're invoked as `/kapacitor:session-recap`, `/kapacitor:session-errors`, etc.

## Prerequisites

- The `kapacitor` CLI must be on your PATH (`npm install -g @kurrent/kapacitor`)
- The Kurrent Capacitor server must be running (default: `http://localhost:5108`)

## Installation

### Option A: CLI command (recommended)

```bash
kapacitor plugin install            # Claude Code, user-wide
kapacitor plugin install --codex    # Codex CLI, user-wide
kapacitor plugin install --project  # current project only
```

### Option B: Interactive plugin manager

- Claude Code: run `/plugin` inside a session and browse the **Installed** tab.
- Codex CLI: `codex plugin marketplace add kurrent-io/kapacitor-cli` then enable from the marketplace.

### Option C: Settings file (manual)

Add to `.claude/settings.local.json` or `~/.claude/settings.json`:

```json
{
  "extraKnownMarketplaces": {
    "kapacitor": {
      "source": {
        "source": "directory",
        "path": "/path/to/kapacitor/kapacitor"
      }
    }
  },
  "enabledPlugins": {
    "kapacitor@kapacitor": true
  }
}
```

### Verify

- Claude Code: `/hooks` (hooks) and `claude mcp list` (MCP servers).
- Codex CLI: `/hooks` (then trust each kapacitor entry) and `codex mcp list`.

## Configuration

Set `KAPACITOR_URL` to override the default server URL:

```bash
export KAPACITOR_URL=http://my-server:5108
```

## Plugin structure

```
kapacitor/
  .claude-plugin/
    plugin.json          — Claude manifest (name, version, description)
    marketplace.json     — Marketplace manifest for plugin discovery
  .codex-plugin/
    plugin.json          — Codex manifest (refs ./.codex-mcp.json and ./codex-skills/)
  .mcp.json              — Claude MCP servers (camelCase mcpServers shape)
  .codex-mcp.json        — Codex MCP servers (snake_case mcp_servers shape)
  hooks/
    hooks.json           — Hook definitions for all Claude lifecycle events
  skills/
    session-recap/
      SKILL.md           — /kapacitor:session-recap skill (Claude)
    session-errors/
      SKILL.md
    validate-plan/
      SKILL.md
    session-disable/
      SKILL.md
    session-hide/
      SKILL.md
  codex-skills/
    kapacitor-recap/
      SKILL.md           — same skill, Codex variant
    kapacitor-errors/
      SKILL.md
    kapacitor-validate-plan/
      SKILL.md
    kapacitor-disable/
      SKILL.md
    kapacitor-hide/
      SKILL.md
```

The two MCP files exist because Claude requires top-level `mcpServers` (camelCase) while Codex accepts only `mcp_servers` (snake_case) or a bare server map — the schemas don't overlap. Keep them in sync when adding or removing servers.
