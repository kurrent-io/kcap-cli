---
name: kapacitor-recap
description: >-
  Use when the user asks to "read a previous session", "get session history",
  "recap session", "what happened in session X", "load context from a previous
  session", "continue from session", "what did we do last time", "catch me up",
  "summarize session", "what have we been working on", "recent changes",
  "recent sessions", or references prior work in this repo. Retrieves session
  summaries recorded by Kurrent Capacitor via the `kapacitor recap` CLI.
---

# Kapacitor Recap

Retrieve session history recorded by Kurrent Capacitor. Codex CLI 0.81+ exports `CODEX_THREAD_ID`; `kapacitor recap` uses it the same way it uses `KAPACITOR_SESSION_ID` for Claude. No args needed for the current session.

## Usage

Run the `kapacitor recap` CLI via the shell. Do NOT call the HTTP API directly — the CLI handles formatting, error handling, and server URL resolution.

```bash
# Current session — auto-resolved from CODEX_THREAD_ID
kapacitor recap

# Recent session summaries for the current repository (discovery)
kapacitor recap --repo

# A specific session by ID (from --repo output)
kapacitor recap <sessionId>

# Full transcript for a specific session
kapacitor recap --full <sessionId>

# Full continuation chain (all linked sessions, oldest first)
kapacitor recap --chain <sessionId>

# Both: full transcript across all chained sessions
kapacitor recap --chain --full <sessionId>
```

## Default Output (Summary)

Shows the plan (if any) and an AI-generated summary with:
- **Context** — why the work was done.
- **Key decisions** — trade-offs and design choices that matter for future work.
- **Unfinished/Risks** — anything deferred or left incomplete.

If no summary is available (e.g., active session), a hint is shown to use `--full`.

## Repository Recap (`--repo`)

Returns AI-generated summaries from the most recent ended sessions in the current git repository. Each entry includes the session title, date, summary, and the session ID needed to drill in further.

**Use this for discovery — not as the default entry point:**
- The user says "what have we been working on recently?"
- The user references prior work ("recently we implemented X")
- You need context about other sessions in this repo, not the current one

**Progressive disclosure:** Start with `kapacitor recap` (current session) for in-session work. Switch to `--repo` when you need cross-session context. Drill into a specific summary with `kapacitor recap --full <sessionId>`.

## Full Output (`--full`)

The complete transcript with these section types:

- **`## User Prompt`** — what the user asked.
- **`## Assistant`** — text responses.
- **`## Plan`** — plans that were created (Claude Code sessions only).
- **`## Write <path>`** — files that were created (with syntax-highlighted content).
- **`## Edit <path>`** — files that were edited (with diff content).

When using `--chain`, sessions are separated by `# Session <id>` headers, and agent activity appears under `### Agent (<type>)` sub-headers.

## When to Use Each Flag

- **No flag** (`kapacitor recap`) — quick context on the current session.
- **`--repo`** — recent session summaries across the repo.
- **`<sessionId>`** — quick context on a specific session (from `--repo` output).
- **`--full <sessionId>`** — when you need exact prompts, responses, or file contents.
- **`--chain <sessionId>`** — understanding the full history of a task that spanned multiple sessions.
- **`--chain --full <sessionId>`** — complete transcript across all continuations.

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).

## Tips

- Start with `kapacitor recap` (no args) for the active session, then `--repo` for cross-session discovery.
- Summarize key decisions and changes for the user rather than echoing the full recap output verbatim.
- The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Error Handling

- If the session is not found, the command prints "Session not found" and exits with code 1.
- If not in a git repository (for `--repo`), the command prints an error and exits with code 1.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
