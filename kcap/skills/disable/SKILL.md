---
name: disable
description: >-
  This skill should be used when the user asks to "disable recording",
  "stop recording", "delete this session", "don't record this",
  "remove my session data", "erase this session", "turn off tracking",
  "stop tracking", "disable kcap", "remove session",
  or wants to stop the current session from being recorded.
  Stops the watcher, prevents future hooks, and deletes server data.
---

# Session Disable

Stop recording the current session and delete all data from the server.

## Usage

Run this command in the terminal:

```bash
kcap disable
```

This will:
1. Stop the transcript watcher process
2. Prevent all future hook events from being sent for this session
3. Delete all session data from the server (event streams and read models)

## Requirements

- `kcap disable` resolves the current session id from the environment when the host agent CLI exposes one. If no session id is available, pass it explicitly: `kcap disable <sessionId>`.
- The kcap CLI must be on PATH

## Notes

- This action is irreversible — all session data will be permanently deleted from the server
- The local transcript file (`.jsonl`) is not affected — only server-side data is removed
- Subsequent hooks (session-end, notification, etc.) will be silently skipped
