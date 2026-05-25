---
name: kapacitor-validate-plan
description: >-
  This skill should be used when the user asks to "validate plan",
  "verify plan", "check plan completion", "did I finish everything",
  "is the plan done", "what's left to do", "validate my work",
  or wants to verify that all planned items were completed.
---

# Kapacitor Validate Plan

Verify that all items in the current session's plan have been completed.

## Usage

```bash
# Active session — auto-resolved from CODEX_THREAD_ID (Codex 0.81+)
kapacitor validate-plan

# Specific session by ID (from `kapacitor recap --repo`)
kapacitor validate-plan <sessionId>
```

## What It Returns

The command outputs three sections:

- **`## Plan`** — the full plan text.
- **`## What's Done`** — two sub-sections:
  - **Summary** — AI-generated summary of what was accomplished (from `WhatsDoneGenerated` events, if available).
  - **Details** — list of files created (`Write`) and modified (`Edit`) during the session.
- **`## Instructions`** — asks you to compare the plan against the summary and file list.

## What To Do With The Output

1. Read the plan carefully and identify each distinct planned item.
2. Compare each item against the summary and file list under "What's Done".
3. If all items are complete, confirm to the user that the plan is fully implemented.
4. If there are gaps, list the missing items and complete them now.

## When No Plan Is Found

If the output says "No plan found for this session", inform the user that no plan was detected for this session. Plans are recorded when a session emits an `ExitPlanMode`-style event; not every session has one.

## Requirements

Run inside an active Codex CLI session (0.81+) — the active session is auto-resolved from `CODEX_THREAD_ID`. To act on a different session, pass its ID explicitly.

The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Environment

`KAPACITOR_URL` overrides the default server URL (`http://localhost:5108`).
