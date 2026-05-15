# AI-635 — Codex Stop hook reports "invalid stop hook JSON output"

**Linear:** [AI-635](https://linear.app/kurrent/issue/AI-635/codex-stop-hook-produces-an-error)
**Type:** Bug fix
**Repo:** kapacitor CLI

## Problem

Every Codex turn end produces:

```
Stop hook (failed)
error: hook returned invalid stop hook JSON output
```

The error is non-fatal (Codex still continues) but surfaces to the user as a red diagnostic, and breaks user trust in the recording integration.

## Root cause

Two independent bugs combine to break the Codex Stop hook's stdout contract.

### 1. `HandleStop` emits nothing on stdout

Codex parses `Stop` hook stdout against `codex-rs/hooks/schema/generated/stop.command.output.schema.json`:

```json
{
  "additionalProperties": false,
  "properties": {
    "continue":      { "type": "boolean", "default": true },
    "decision":      { "enum": ["block"] },
    "reason":        { "type": "string" },
    "stopReason":    { "type": "string" },
    "suppressOutput":{ "type": "boolean", "default": false },
    "systemMessage": { "type": "string" }
  },
  "type": "object"
}
```

No `required` fields, but the body must parse as a JSON object. Empty stdout is **not** a valid empty object — it's a parse error.

`CodexHookCommand.HandleStop` (src/kapacitor/Commands/CodexHookCommand.cs:86–107) writes nothing to stdout, then returns 0. Codex tries to parse the empty body, fails, and prints the error.

Compare with `HandlePermissionRequest` (src/kapacitor/Commands/CodexHookCommand.cs:109–124) which correctly writes its `hookSpecificOutput` JSON before returning. The Stop branch never got the equivalent.

### 2. `WatcherManager` writes diagnostic chatter to stdout

`KillWatcher` and `InlineDrainAsync` (both called by `HandleStop`) emit informational status lines via `Console.Out.WriteLineAsync(...)`:

- src/kapacitor/WatcherManager.cs:91 — `Spawned watcher for {key} (PID {pid})`
- src/kapacitor/WatcherManager.cs:128 — `Watcher {key} (PID {pid}) exited gracefully`
- src/kapacitor/WatcherManager.cs:138 — `Watcher {key} (PID {pid}) already exited`
- src/kapacitor/WatcherManager.cs:193 — `Watcher {key} not running, respawning...`
- src/kapacitor/WatcherManager.cs:227 — `Spawned what's-done generator for {sessionId} (PID {pid})`
- src/kapacitor/WatcherManager.cs:291 — `Inline drain for {sessionId}: no new lines to send`
- src/kapacitor/WatcherManager.cs:311 — `Inline drain for {sessionId}: sent {n} line(s)`

These are diagnostic messages, not part of any hook protocol. They pollute the stdout channel of every hook that calls `WatcherManager`. Even after fixing #1, those lines would still corrupt the JSON output and re-trigger the same parse failure.

This is also a latent issue for Claude's `SessionStart` hook (stdout there is interpreted as transcript context), but it has not been observably wrong because the strings are inert prose that Claude silently ignores. Codex's stricter schema validation makes the latent bug visible.

## Fix

### Change 1 — Move WatcherManager diagnostics to stderr

Replace every `Console.Out.WriteLineAsync(...)` / `Console.Out.WriteLine(...)` in `src/kapacitor/WatcherManager.cs` with `Console.Error.WriteLineAsync(...)` / `Console.Error.WriteLine(...)`. The error paths already use `Console.Error` — this just brings the success paths in line.

No existing test pins these strings on stdout (verified by grep over `test/`). The strings remain visible to operators when run interactively (terminals show stderr by default) and to the Capacitor daemon's log capture (which redirects both streams).

### Change 2 — Emit valid Stop output from `HandleStop`

After `KillWatcher` / `InlineDrainAsync` / `PostHookAsync` complete, write to stdout:

```json
{"continue": true}
```

`continue: true` is the schema default, so emitting it is semantically a no-op — but it makes the JSON body non-empty and unambiguously typed as an object, which is what Codex's parser needs.

Even after Change 1, we still emit the JSON unconditionally — the stdout channel is now "reserved" for hook output, so populating it is required, not optional.

### What stays the same

- HTTP routing (`/hooks/session-end/codex`) is unchanged.
- Watcher kill / inline drain ordering is unchanged.
- Exit codes are unchanged (0 on success, 1 on HTTP failure).
- `HandlePermissionRequest` already writes correct JSON — no change.
- `HandleSessionStart` writes nothing to stdout, but Codex's `SessionStart` output schema is also "JSON object required when exit 0". This will need the same `{"continue": true}` write to be fully correct. **In scope for this fix** since both bugs share the same one-line remediation and the same risk profile.

### Decision: include SessionStart fix in same PR

After review, both `HandleStop` and `HandleSessionStart` exit 0 with empty stdout — the SessionStart hook has the same latent bug (it just hasn't surfaced because users haven't reported it yet, possibly because Codex tolerates SessionStart parse failures more quietly). Add the same `{"continue": true}` write to `HandleSessionStart` to close the latent gap before it ships as another bug ticket.

## Files changed

- `src/kapacitor/WatcherManager.cs` — 7 lines: `Console.Out` → `Console.Error` on success-path log messages
- `src/kapacitor/Commands/CodexHookCommand.cs` — add `Console.Write(StopOutputJson)` in `HandleStop` and `HandleSessionStart`, where `StopOutputJson` is a shared constant `"""{"continue":true}"""`
- `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs` — extend `Stop_maps_to_session_end_codex_route` (and add `SessionStart_*` assertion) to capture stdout and assert valid JSON

## Test plan

**Unit (TUnit, kapacitor.Tests.Unit):**

1. Extend `Stop_maps_to_session_end_codex_route`:
   - Capture `Console.Out` via `Console.SetOut` (same pattern as the existing `PermissionRequest_returns_default_allow_decision` test).
   - Assert exit 0.
   - Assert the captured stdout parses as a JSON object.
   - Assert the JSON contains `"continue": true`.
   - Assert the captured stdout does **not** contain `"Watcher "`, `"Inline drain"`, or `"Spawned"` substrings — i.e. WatcherManager chatter no longer leaks into the hook channel.

2. Extend `SessionStart_posts_to_session_start_codex_with_normalized_session_id` analogously: capture stdout, assert it parses as a JSON object containing `"continue": true`.

3. Existing tests for `PermissionRequest`, swallowed events, malformed JSON, etc. must continue to pass — they don't depend on stdout shape and shouldn't be affected.

**Manual smoke (post-merge):**

- Run a Codex turn locally with the kapacitor hooks installed; confirm no red "Stop hook (failed)" line at turn end.
- Same for SessionStart at session boot.
- Confirm Capacitor server still receives `session-start/codex` and `session-end/codex` POSTs (no regression on the HTTP path).

**AOT publish check** (per CLAUDE.md):

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

No new IL3050/IL2026 warnings expected — the JSON output is a string literal, no reflection.

## Out of scope

- Changing the Codex Stop hook's HTTP behavior or server-side routing.
- Auditing every Console.Out usage in the CLI for hook-channel pollution (other commands aren't invoked as Codex hooks).
- Adding a generic "hook output writer" abstraction. One literal in two places isn't worth an abstraction.
- README updates — this is a pure bug fix with no user-visible CLI surface change.

## Risk

Low. Two single-file changes, both narrowly scoped, no API/contract changes, no schema migrations, no new dependencies. The stderr migration is the only change with observable side effects, and those side effects are improvements (diagnostic chatter no longer pollutes hook stdout).

## Linear

Branch: `alexeyzimarev/ai-635-codex-stop-hook-produces-an-error`
Parent issue: AI-67 (Codex hook surface)
