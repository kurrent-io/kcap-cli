---
name: hide
description: >-
  This skill should be used when the user asks to "hide this session",
  "make this private", "hide session", "owner only", "make private",
  "hide from others", "set private", "don't show this session",
  or wants to change the current session visibility to owner-only.
  Sets session visibility so only the owner can see it.
---

# Session Hide

Hide the current session so only you (the owner) can see it.

## Usage

Run this command in the terminal:

```bash
kapacitor hide
```

This sets the session visibility to "none" (owner-only). Other users will no longer see this session in the Capacitor dashboard.

## Requirements

- `kapacitor hide` resolves the current session id from the environment when the host agent CLI exposes one. If no session id is available, pass it explicitly: `kapacitor hide <sessionId>`.
- The kapacitor CLI must be on PATH

## Notes

- This is reversible — visibility can be changed back via the Capacitor dashboard
- The session data remains on the server, just hidden from other users
- Unlike `kapacitor disable`, recording continues normally
