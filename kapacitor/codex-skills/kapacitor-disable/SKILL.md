---
name: kapacitor-disable
description: >-
  This skill should be used when the user asks to "disable recording",
  "stop recording", "delete this session", "don't record this",
  "remove my session data", "erase this session", "turn off tracking",
  "stop tracking", "disable kapacitor", "remove session",
  or wants to stop the current session from being recorded.
  Stops the watcher, prevents future hooks, and deletes server data.
---

# Kapacitor Disable

Stop recording the current session and delete all data from the server.

## Usage

```bash
# Active session — auto-resolved from CODEX_THREAD_ID (Codex 0.81+)
kapacitor disable

# Specific session by ID (from `kapacitor recap --repo`)
kapacitor disable <sessionId>
```

This will:
1. Stop the transcript watcher process
2. Prevent all future hook events from being sent for this session
3. Delete all session data from the server (event streams and read models)

## Requirements

Run inside an active Codex CLI session (0.81+) — the active session is auto-resolved from `CODEX_THREAD_ID`. To act on a different session, pass its ID explicitly. Use `kapacitor recap --repo` to list recent session IDs in this repository.

The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Notes

- This action is irreversible — all session data will be permanently deleted from the server.
- The local transcript file (Codex rollout `.jsonl`) is not affected — only server-side data is removed.
- Subsequent hooks (session-end, etc.) will be silently skipped.

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).
