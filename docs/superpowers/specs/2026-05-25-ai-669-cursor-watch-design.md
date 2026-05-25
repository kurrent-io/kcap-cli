# AI-669 - Cursor session ingest milestone B: periodic poll watcher

**Status:** design proposed, pending implementation plan
**Linear:** [AI-669](https://linear.app/kurrent/issue/AI-669/cursor-session-ingest-milestone-b-periodic-poll-watcher)
**Related:** [AI-661](https://linear.app/kurrent/issue/AI-661/cursor-session-ingest-milestone-a-post-hoc-cli-import), [AI-680](https://linear.app/kurrent/issue/AI-680/cursor-cli-daemon-support-for-hosted-agents)
**Source spec:** `../kapacitor-server/docs/superpowers/specs/2026-05-20-cursor-session-ingest-feasibility.md`

## Problem

Milestone A added `kapacitor cursor import`, which snapshots Cursor Composer/Agent sessions from Cursor's local SQLite state and posts them to the server. The command is idempotent, but it is manual. Cursor sessions appear in Capacitor only after the user remembers to run the import, which makes Cursor feel much less live than Claude and Codex.

AI-669 closes that UX gap for Cursor IDE sessions by adding an explicit foreground watcher that polls Cursor's SQLite state while Cursor is open and incrementally reuses the existing import path.

## Goals

- Add `kapacitor cursor watch` for near-real-time Cursor IDE session ingest.
- Reuse milestone A's allowlisted payload assembler, watermark check, server endpoint, and server idempotency model.
- Avoid expensive work when Cursor has not changed.
- Keep finalized-turn semantics: never import a bubble while Cursor reports it in `generatingBubbleIds`.
- Keep the watcher a normal foreground CLI process with predictable Ctrl+C shutdown.

## Non-goals

- No daemon integration for Cursor IDE session polling in this milestone.
- No hosted Cursor agent support. Cursor CLI daemon support is future AI-680.
- No Cursor VSCode extension.
- No partial assistant-token streaming from in-flight Cursor bubbles.
- No new server import protocol. The watcher continues to POST the same `CursorImportPayload` used by `cursor import`.

## Decisions

| Decision | Choice |
|---|---|
| User surface | `kapacitor cursor watch [--workspace P] [--all] [--interval N]` |
| Lifecycle | Foreground process only; Ctrl+C exits cleanly with code 0 |
| Default interval | 2 seconds, with a 1 second minimum |
| Change detection | File stat fingerprints over DB and WAL files before building payloads |
| Changed files watched | Both selected workspace DBs and `globalStorage/state.vscdb`, including `-wal` sidecars |
| First tick | Always runs an import pass so already-open Cursor sessions appear immediately |
| In-flight bubbles | Preserve existing skip behavior; next completed poll catches them |
| Server writes | Full composer payload reposted when progressed; server tracker/watermark owns dedup |
| Missing workspace | Watch mode waits and retries with throttled diagnostics instead of exiting immediately |

## Approaches Considered

### 1. Foreground polling command (chosen)

Add a long-running `cursor watch` command that loops a refactored import pass. It is simple to reason about, easy to run during development, and avoids inventing a background process model for an IDE integration. This is the right milestone-B shape.

### 2. Daemon-hosted IDE watcher (deferred)

The existing daemon is for hosted CLI agents and local permission bridging. Running a Cursor IDE SQLite watcher inside it would couple daemon lifetime to IDE session import without a hosted-agent lifecycle. This remains out of scope unless a later product decision asks for a user-level background IDE watcher.

### 3. Cursor CLI hosted agent integration (separate ticket)

Cursor now has `cursor-agent` with structured `stream-json` output. That is promising for daemon-managed hosted agents, but it is a different data source and lifecycle than Cursor IDE SQLite. Track it under AI-680, not AI-669.

## CLI Surface

`kapacitor cursor import` keeps its current behavior.

New command:

```bash
kapacitor cursor watch
kapacitor cursor watch --workspace ~/code/foo
kapacitor cursor watch --all
kapacitor cursor watch --interval 1
```

Flags:

| Flag | Meaning |
|---|---|
| `--workspace P` | Watch Cursor sessions for the workspace at path `P` |
| `--all` | Watch every Cursor workspace |
| `--interval N` | Poll interval in seconds; default `2`, minimum `1` |

Invalid combinations:

- `--workspace` and `--all` together return usage error.
- `--interval < 1` returns usage error.

Output is human-oriented progress on stderr, matching the existing Cursor import command's diagnostic style. The command does not reserve stdout for a machine-readable stream.

## Architecture

### Import Pass Refactor

Extract the body of `CursorCommand.RunAsync(args: ["import", ...])` into an internal reusable import service:

```csharp
internal sealed record CursorImportOptions(
    string? Workspace,
    bool All,
    int PayloadHardCapBytes);

internal sealed record CursorImportStats(
    int WorkspacesSeen,
    int ComposersSeen,
    int Posted,
    int SkippedUnchanged,
    int SkippedInFlight,
    int Failed);

internal static class CursorImporter {
    internal static Task<CursorImportStats> ImportOnceAsync(
        CursorImportOptions options,
        string baseUrl,
        CursorPaths? pathsOverride = null,
        CancellationToken ct = default);
}
```

`cursor import` becomes argument parsing plus a single `ImportOnceAsync` call. Its exit-code contract stays unchanged: nonzero if any payload build or POST fails.

`cursor watch` calls the same method repeatedly. Watch mode treats transient import failures as diagnostics and continues polling; argument/authentication failures still fail fast.

### Change Detection

The watcher must avoid building payloads on every tick when nothing changed. A cheap stat fingerprint is enough.

For each target workspace, compute a fingerprint from:

- `workspaceStorage/<hash>/state.vscdb`
- `workspaceStorage/<hash>/state.vscdb-wal`
- `globalStorage/state.vscdb`
- `globalStorage/state.vscdb-wal`

Each file contributes `(exists, length, lastWriteTimeUtcTicks)`. Missing WAL files are valid and part of the fingerprint.

Why include global state: milestone A reads headers, composer data, bubbles, and content blobs from `globalStorage/state.vscdb`. Watching only the workspace DB would miss the important writes.

When the global DB fingerprint changes, every target workspace is considered changed. When only one workspace DB changes, the watcher may import just that workspace if the implementation already has the resolved path, but it may also conservatively run one full pass for the selected target set. Server-side watermarks still make the conservative path safe.

The first tick ignores fingerprints and runs a pass immediately.

### Workspace Resolution

Reuse `CursorCommand.ResolveWorkspaces`.

Differences in watch mode:

- If no workspace matches, do not exit. Print a throttled message such as `[cursor] No matching Cursor workspace yet; waiting...` and retry.
- Re-resolve workspaces periodically, not just at startup. A 30 second rescan cadence is enough to catch Cursor creating a new `workspaceStorage` entry after the watcher starts.
- `--workspace` resolves against the provided path. With neither `--workspace` nor `--all`, the current working directory is the target, matching `cursor import`.

### Poll Loop

`CursorCommand` parses both subcommands. Keep the user-facing command in one file, but put the loop in a small internal `CursorWatcher` helper so the polling behavior can be tested without shelling through `Program.cs`.

`CursorWatcher` owns the foreground loop:

1. Parse flags and validate interval.
2. Resolve `CursorPaths`.
3. Print a short startup line with target and interval.
4. Until cancellation:
   - Re-resolve target workspaces if the rescan cadence elapsed.
   - Compute fingerprints.
   - If first tick or changed, call `CursorImporter.ImportOnceAsync`.
   - Print compact stats only when something was posted, skipped in-flight, or failed.
   - Delay for `interval`, observing cancellation.
5. On Ctrl+C/SIGTERM, exit 0.

Use `Console.CancelKeyPress` with `e.Cancel = true` and a `CancellationTokenSource`, consistent with the existing watcher command's graceful-shutdown pattern. No PID files, no detached process, no `WatcherManager`.

### HTTP and Auth

`ImportOnceAsync` should create an authenticated client per pass or otherwise refresh the token before network calls. A long-running watcher must not hold a bearer token forever after startup. The existing `TokenStore.GetValidTokensAsync` can refresh at client creation time, so per-pass client creation is acceptable and simple.

If a pass receives an unauthorized response or cannot acquire credentials, watch mode should print the actionable login message and exit nonzero. Network/server unavailability is treated as transient: print a throttled error and keep polling.

### Near-real-time Semantics

This is near-real-time completed-turn ingest, not live token streaming.

- During assistant generation, Cursor mutates SQLite, the fingerprint changes, and the watcher may run an import pass.
- If `composerData.generatingBubbleIds` is non-empty, the existing importer skips that composer.
- Once Cursor finalizes the bubble and updates SQLite again, the next poll imports it.
- The payload remains a full composer snapshot. The server's deterministic event IDs, import tracker, and watermark model prevent duplicates.

The default 2 second interval means the usual lag after a finalized Cursor write should be one polling interval plus import/upload time. Users can choose `--interval 1` for lower latency at the cost of more file stat calls and more frequent watermark checks after changes.

## Error Handling

| Failure | Watch behavior |
|---|---|
| Cursor user dir missing | Print waiting diagnostic and continue |
| Matching workspace DB missing | Print waiting diagnostic and continue |
| Global DB missing | Print waiting diagnostic and continue |
| SQLite read fails due concurrent write/lock | Print throttled warning and retry next tick |
| Payload build fails for one composer | Count as failed, continue other composers |
| Payload exceeds hard cap | Count as failed, continue other composers |
| Server unavailable | Print throttled warning, continue polling |
| Unauthorized/auth missing | Print login guidance, exit nonzero |
| Invalid args | Print usage, exit nonzero |
| Ctrl+C/SIGTERM | Exit 0 |

Throttle repeated diagnostics by message category so a broken setup does not spam every 2 seconds.

## Help and README

Update:

- `src/Kapacitor.Cli.Core/Resources/help-cursor.txt`
- `src/Kapacitor.Cli.Core/Resources/help-usage.txt`
- `README.md`

Help text should make the scope explicit:

- `cursor import` is a one-shot post-hoc import.
- `cursor watch` is a foreground near-real-time poller for Cursor IDE sessions.
- Cursor CLI hosted-agent support is separate future work.

## Testing

### Unit Tests

Add focused tests around the new seams:

- `CursorCommand` parsing accepts `watch`, `--workspace`, `--all`, and `--interval`.
- `--workspace` plus `--all` is rejected.
- `--interval 0` is rejected.
- `CursorDbFingerprint` reports changed on first observation.
- DB mtime/length changes are detected.
- WAL mtime/length changes are detected.
- Global DB changes invalidate target workspaces.
- Missing WAL files are valid and stable.
- Watch loop calls importer on first tick.
- Watch loop skips importer when fingerprint is unchanged.
- Watch loop calls importer again after fingerprint changes.
- Watch loop continues after transient importer failure.
- Watch loop exits cleanly on cancellation.

Keep watch-loop tests sleep-free by injecting a delay function or test clock.

### Existing Tests

Existing `CursorCommandTests` for one-shot import must continue to pass unchanged or with minimal call-site updates after the import refactor.

### Manual Smoke

Against a real Cursor workspace:

1. Start local server.
2. Run `kapacitor cursor watch --workspace <repo> --interval 1`.
3. Start or continue a Cursor Agent-mode composer in that repo.
4. Verify no payload is posted while `generatingBubbleIds` is non-empty.
5. Verify the completed turn appears in the dashboard within a few seconds after Cursor finalizes it.
6. Re-run the same prompt/import path and verify no duplicate events or doubled stats.
7. Stop the watcher with Ctrl+C and verify exit code 0.

## Open Questions

### Default interval after real-world smoke

The design chooses 2 seconds because change detection is cheap and the product goal is near-real-time visibility. If real Cursor workspaces produce excessive SQLite churn during streaming, increase the default to 5 seconds while keeping `--interval 1` available.

### Background mode

No background mode ships in AI-669. If users want this later, design it separately. Possible options are a user-level launcher/launchd/systemd helper or a future daemon-managed mode, but those need a lifecycle and support story beyond this milestone.

## Rollout

This is additive. Existing `kapacitor cursor import` users are unaffected. The only risky refactor is extracting the import pass; keep tests around current one-shot behavior before adding the watch loop.
