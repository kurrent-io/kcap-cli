# Background commands in the desktop app

## Goal

When a Claude session waits on a background shell command, the desktop app shows it the way it
shows a subagent that outlives its turn: in the strip above the composer while it runs, and in the
session sidebar with its outcome once it ends. Today only the terminal shows it.

## Scope

In: Claude sessions, local and remote lanes; the strip above the composer; the sidebar section.

Out: the rail card status (it reads the daemon's hook-reported subagent count, so it still says
Idle while a shell runs — a separate issue), other harnesses (#1320).

## Transcript evidence

Measured over ~350 background shells in local Claude transcripts:

- **Start.** A `Bash` tool call. Its result's `toolUseResult.backgroundTaskId` is the only reliable
  mark that it went to the background: about a third of those results come from calls without
  `run_in_background`, foreground commands moved to the background when they hit their timeout
  (`timedOutAfterMs` beside the id). Ctrl+B backgrounding is the same shape.
- **End.** A `<task-notification>` carrying `<task-id>` (the background id), `<tool-use-id>` (the
  call id) and `<status>`: `completed`, `failed`, `killed`, and rarely `running`, which is not an
  end.
- **Stop.** A `KillShell`/`TaskStop` result whose `toolUseResult` has `task_type: "local_bash"` and a
  message starting `Successfully stopped task`. Output reads (`BashOutput`, `TaskOutput`, `Read` on
  the output file) carry the same id and must not end the row.
- `toolUseResult` is kept verbatim on the canonical event's `claude_code` slug, so the remote lane
  sees the same fields.

## Design

### Rename

The subagent types become run types, since a shell is not a subagent. Mechanical, its own commit:

| Before | After |
|---|---|
| `SubagentSignal` (Cli.Core) | `RunSignal` |
| `SubagentOutcome` | `RunOutcome` |
| `ChatProjectionResult.Subagents`, `IChatDisplayRules.Subagents` | `.Runs` |
| `SessionSubagents` (App) | `SessionRuns` |
| `SubagentRow`, `SubagentState`, `SubagentCount` | `RunRow`, `RunState`, `RunCount` |

New `RunKind { Agent, Shell }`.

### Signals

- `RunSignal.Started` gains `Kind` and `Provisional`. A provisional start creates no row; it is held
  until its call's result.
- `Detached` keeps its shape: the call id plus the handle (agent id or background task id).

### Claude rules

- `Agent`/`Task` call → `Started(Agent, provisional: false)`, as today.
- `Bash` call → `Started(Shell, provisional: true)`. Name: the input's `description`, else the
  `command` truncated to one line.
- Tool result with `backgroundTaskId` → `Detached(callId, backgroundTaskId)`.
- `<task-notification>`: `completed` → Done, `failed` → Failed, `killed` → Stopped; any other status
  emits nothing. This also fixes a killed subagent reading Failed and a `running` notification
  failing its row.
- The stop gate accepts `task_type` `local_bash` beside `local_agent`.

### SessionRuns

- A provisional start goes into a pending map by call id, not into `Rows`.
- A `Detached` whose call is pending promotes it: a Shell row dated from the call, marked background.
  A `Detached` for an existing row behaves as today.
- A tool result for a pending call without a `Detached` beside it drops the entry. So does the end
  of the session, so a call that never got a result does not linger.
- Everything else is unchanged: the end by call id first, then by handle; the session-over rule
  presenting a running row as stopped; elapsed time; per-state counts.

### Strip above the composer

`HasRunningSubagents` → `HasRunningRuns`, same visibility rule.

- One running row: its name and state line, e.g. `Run integration tests · running in background · 3m 12s`.
- Several, all one kind: `3 subagents running in background` / `2 commands running in background`
  (the agent-only, not-all-background wording stays `N subagents running`).
- Mixed: `2 subagents, 1 command running in background`.

### Sidebar section

- Title `Subagents` → `Agents & commands`. The list, the collapsed per-state counts and the outcome
  marks are unchanged.
- Each row carries a kind glyph before its name: the agent glyph and a terminal glyph, in
  `KcapMutedBrush`. Status colours stay on the outcome mark only.

## Testing

- Claude rules, from records shaped like the real lines: a flagged call; an unflagged call whose
  result has `backgroundTaskId` and `timedOutAfterMs`; a foreground `Bash` (no row); each
  notification status, `running` included; a `local_bash` stop; an output read that must not end.
- `SessionRuns`: promotion, the dropped provisional entry, a reused background id after the first
  ended, session over, mixed counts.
- `ChatTabViewModel`: the strip text for one, several of one kind, and mixed.
- Headless view test for the renamed section and the kind glyph.
