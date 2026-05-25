---
name: kapacitor-errors
description: >-
  Use when the user asks to "show errors", "extract errors", "what went wrong",
  "find tool errors", "review errors from session", "check session errors",
  "list failures", "what failed in session X", "error report", "show mistakes",
  or wants to review tool call errors from a recorded session. Extracts tool
  errors via the `kapacitor errors` CLI.
---

# Kapacitor Errors

Extract tool call errors from a session recorded by Kurrent Capacitor. The output lists each failed tool call — shell commands, file reads/writes, agent delegations, etc. — along with the error message and the tool that caused it.

Codex CLI 0.81+ exports `CODEX_THREAD_ID`; `kapacitor errors` uses it the same way it uses `KAPACITOR_SESSION_ID` for Claude. No args needed for the current session.

## Usage

```bash
# Current session — auto-resolved from CODEX_THREAD_ID
kapacitor errors

# Errors from the full continuation chain of the current session
kapacitor errors --chain

# Errors from a specific session
kapacitor errors <sessionId>

# Errors from the full continuation chain starting at a session
kapacitor errors --chain <sessionId>
```

## Output Format

Each error is printed as a block with:

- **Session ID** and optional **agent ID** (if the error occurred in a subagent).
- **Event number** and **timestamp**.
- **Tool name** — the tool that failed (e.g., Bash, Read, Edit, Write, Grep, Glob).
- **Error message** — the error output or failure reason.

When using `--chain`, errors from all sessions in the continuation chain are included, ordered chronologically.

## When to Use Each Flag

- **No flag** (`kapacitor errors`) — reviewing errors from the current session.
- **`<sessionId>`** — reviewing errors from a specific session (e.g. from `kapacitor recap --repo`).
- **`--chain`** — reviewing errors across a full task that spanned multiple sessions.

## Practical Applications

- **Post-mortem review** — after finishing a session, identify recurring mistakes and update project rules (CLAUDE.md / AGENTS.md) with avoidance guidance.
- **Debugging** — quickly find what went wrong in a session without scrolling through the full timeline.
- **Pattern detection** — use `--chain` across a multi-session task to spot repeated error patterns (e.g., wrong file paths, incorrect API usage).

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).

## Tips

- After extracting errors, look for patterns: the same tool failing repeatedly, or the same type of mistake across sessions.
- Propose concrete avoidance rules based on the errors found — these can be added to AGENTS.md / CLAUDE.md.
- The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Error Handling

- If the session is not found, the command prints an error and exits with code 1.
- If no errors are found, the command prints "No errors found." — this is a good outcome.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
