---
name: kapacitor-hide
description: >-
  This skill should be used when the user asks to "hide this session",
  "make this private", "hide session", "owner only", "make private",
  "hide from others", "set private", "don't show this session",
  or wants to change the current session visibility to owner-only.
  Sets session visibility so only the owner can see it.
---

# Kapacitor Hide

Hide the current session so only you (the owner) can see it.

## Usage

```bash
# Active session — auto-resolved from CODEX_THREAD_ID (Codex 0.81+)
kapacitor hide

# Specific session by ID (from `kapacitor recap --repo`)
kapacitor hide <sessionId>
```

This sets the session visibility to "none" (owner-only). Other users will no longer see this session in the Capacitor dashboard.

## Requirements

Run inside an active Codex CLI session (0.81+) — the active session is auto-resolved from `CODEX_THREAD_ID`. To act on a different session, pass its ID explicitly. Use `kapacitor recap --repo` to list recent session IDs in this repository.

The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Notes

- This is reversible — visibility can be changed back via the Capacitor dashboard.
- The session data remains on the server, just hidden from other users.
- Unlike `kapacitor disable`, recording continues normally.

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).
