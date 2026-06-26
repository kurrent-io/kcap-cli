# OpenCode historical import (`kcap import --opencode`) — Design

**Date:** 2026-06-26
**Status:** Approved (design), pending implementation plan
**Author:** tony.young@kurrent.io (with Claude)

## Problem

`kcap import` ingests historical sessions from every detected coding agent, but
**OpenCode is not among them**. The source list in
[`Program.cs`](../../../src/Capacitor.Cli/Program.cs) registers Claude, Codex,
Cursor, Copilot, Gemini, Kiro, and Pi only; `VendorSelection` does not recognize
a `--opencode` filter. OpenCode reached the *live* surfaces (status line, setup
wizard, plugin install, live watcher) but never the *import* surface, so a user
who installs kcap after using OpenCode cannot backfill their history.

The `--help` text omitting OpenCode from import is therefore **accurate today** —
this design adds the missing capability and then the docs.

## Key finding: OpenCode history lives in SQLite

Current OpenCode (verified on 1.17.11) stores all session history in a single
SQLite database at `~/.local/share/opencode/opencode.db` (honoring
`XDG_DATA_HOME`). There are **no per-session JSONL files** in OpenCode's native
store. Two JSONL sources that can cause confusion are *not* general history:

- `~/.local/share/opencode/storage/…` — the **old** file layout, replaced by the
  db (schema migrations run 2026-01 → 2026-03). Absent on current installs.
- `~/.cache/kcap/opencode/*.jsonl` — **kcap's own** live-plugin cache; only
  contains sessions the plugin watched live, which are already ingested.

So a real historical importer must read the SQLite db.

### Relevant schema

```
session(id TEXT pk, parent_id TEXT, directory TEXT, title TEXT, version TEXT,
        time_created INT, time_updated INT, …)
message(id TEXT pk, session_id TEXT, time_created INT, data TEXT)   -- data = info JSON
part(id TEXT pk, message_id TEXT, session_id TEXT, time_created INT, data TEXT) -- data = part JSON
```

### Line reconstruction (exact, deterministic)

The live plugin streams one JSONL line per message: `{info, parts}` taken from the
OpenCode SDK's `client.session.messages()`. The db `data` columns hold the same
objects **minus** the primary/foreign-key fields, which are promoted to columns.
Reconstruction merges them back:

- `info`  = `JSON.parse(message.data)` + `{ id: message.id, sessionID: message.session_id }`
- `part`  = `JSON.parse(part.data)`    + `{ id: part.id, messageID: part.message_id, sessionID: part.session_id }`

Verified against a live `~/.cache/kcap` JSONL line: reconstructed keys match
exactly (`info`: `agent,id,model,role,sessionID,summary,time`; `part`:
`id,messageID,sessionID,text,type`). The result is structurally identical to the
live path, so **the server's existing `opencode` normalizer consumes it with no
server changes**.

## Architecture

A new `OpenCodeImportSource : IImportSource` in
`src/Capacitor.Cli/Commands/`, structured like
[`PiImportSource`](../../../src/Capacitor.Cli/Commands/PiImportSource.cs): a
**routed** source (`FilePath = ""`, `SupportsTitleGeneration = false`) that runs
through `ImportSessionAsync` rather than the chain worker. SQLite read helpers
live in `Capacitor.Cli.Core/OpenCode/` (e.g. `OpenCodeDb.cs`), keeping storage
knowledge in Core beside `OpenCodePaths` / `OpenCodeSubagentDiscovery`.

### Components

| Unit | Responsibility | Depends on |
|---|---|---|
| `OpenCodeDb` (Core) | Open db read-only; query roots/children; synthesize `{info,parts}` lines per session | Microsoft.Data.Sqlite |
| `OpenCodeImportSource` (CLI) | `IImportSource` impl: discover, classify, import (roots + subagent children) | `OpenCodeDb`, `OpenCodePaths`, `OpenCodeSubagentDiscovery`, `SessionImporter` |
| `VendorSelection` / `Program.cs` | wire `--opencode` filter + register source | — |

## Data access

- Packages: **Microsoft.Data.Sqlite** + **SQLitePCLRaw.bundle_e_sqlite3**
  (bundles native `e_sqlite3`; AOT-compatible, cross-platform).
- Open **read-only** (`Mode=ReadOnly`), tolerating WAL (OpenCode may be running).
- `IsAvailable` = `File.Exists(Path.Combine(OpenCodePaths.DataDir(), "opencode.db"))`.
- **AOT gate:** verify zero `IL3050`/`IL2026` on `dotnet publish -c Release`
  before merge. If Microsoft.Data.Sqlite emits warnings, revisit access strategy.

## Discovery & classification

- **Roots:** `SELECT … FROM session WHERE parent_id IS NULL OR parent_id = ''`.
  Each → `DiscoveredSession` with:
  - `SessionId` = raw `ses_…` id (**not** GUID-normalized — matches live ingest)
  - `Cwd` = `session.directory`
  - `FirstTimestamp` = `session.time_created`
- Discovery filters honored as in Pi: `--session`, `--cwd`, `--since`.
- Classification mirrors Pi: count importable lines, probe
  `/api/sessions/{id}/last-line`, assign New / Partial / AlreadyLoaded / TooShort
  / ProbeError; resume from `serverLastLine + 1` when Partial.

## Transcript synthesis

Per session, build lines in memory:

```
SELECT data, id FROM message WHERE session_id = ? ORDER BY time_created, id
  per message:  SELECT data, id, message_id, session_id FROM part
                WHERE message_id = ? ORDER BY time_created, id
  emit {info, parts} via the merge rule above
```

Streamed via `SessionImporter.SendTranscriptBatches`. Preference: feed synthesized
content directly (in-memory) rather than reusing the plugin's `~/.cache` files
(those only cover live-seen sessions). If `SendTranscriptBatches` requires a file
path, write a transient file under the kcap cache/scratch dir and clean up.

## Subagent (child) parity

Children (`parent_id` set) are imported as **subagents of their parent**, not as
standalone sessions, mirroring the live watcher
([`WatchCommand`](../../../src/Capacitor.Cli/Commands/WatchCommand.cs) +
[`OpenCodeSubagentDiscovery`](../../../src/Capacitor.Cli.Core/OpenCode/OpenCodeSubagentDiscovery.cs)):

1. Import the root (session-start → transcript → session-end).
2. For each child with `parent_id` = this root:
   - `POST /hooks/subagent-start` (parent session id, `agent_id` = childSid,
     `agent_type` from child's `info.agent`, fallback `"subagent"`).
   - Stream the synthesized child transcript under `agent_id = childSid`.
   - `POST /hooks/subagent-stop`.

Reuse `OpenCodeSubagentDiscovery.BuildStartPayload` / `BuildStopPayload` and
`CanonicalAgentId`. **Open item to confirm during implementation:** the exact
watermark key the server uses for subagent subsessions (parent id vs. agentId) —
mirror whatever `WatchCommand`'s opencode subagent streaming uses.

## Lifecycle, idempotency, resume

Mirrors Pi exactly:

- Lifecycle-before-transcript ordering: `session-start/opencode` → transcript →
  `session-end/opencode`. A transcript that advanced the watermark past a failed
  lifecycle POST would orphan the session; idempotent server-side via
  deterministic event ids, so re-runs are safe.
- **Title:** unlike Pi, OpenCode's db has a real `session.title`. Pass it on the
  session-start payload so imported sessions get their true title instead of the
  server fallback.
- `started_at` = `session.time_created`; `ended_at` = `session.time_updated`.

## Wiring & docs (same PR)

- `VendorSelection` ([VendorSelection.cs:15](../../../src/Capacitor.Cli/Commands/VendorSelection.cs)):
  add `--opencode` → `"opencode"` to `KnownVendorFlags`, the `switch`, and the
  `--opencode-` prefix guards.
- `Program.cs` ([:459](../../../src/Capacitor.Cli/Program.cs)): add
  `new OpenCodeImportSource()` to `allSources`.
- Docs (required by CLAUDE.md in the same PR):
  - `help-import.txt` vendor-filter list (add `--opencode`).
  - `help-usage.txt:45` one-liner (add OpenCode to the parenthesized list).
  - `README.md` quick-start (`## Getting started`) and the `kcap import` section
    under `## CLI commands`.

## Testing

Unit tests (TUnit), with a fixture `opencode.db` built in test setup via
Microsoft.Data.Sqlite (or a small checked-in sample db):

- Discovery filters: `--cwd`, `--session`, `--since`.
- Root-only listing (children excluded from the top-level set).
- Line-shape fidelity: assert reconstructed `{info,parts}` keys match the live
  contract (the merge rule).
- Child → subagent routing: a parented session produces subagent-start/stop +
  child transcript under `agent_id = childSid`, not a standalone session.
- Classification states against a stubbed `/api/sessions/{id}/last-line`
  (WireMock): New / Partial / AlreadyLoaded.

## Out of scope / deliberate decisions

- **No GUID normalization** of session ids — keep raw `ses_…` (matches live).
- **No reuse** of `~/.cache/kcap/opencode` JSONL as an import source.
- **No server changes** — reconstructed lines match the existing normalizer.

## Risks

- **AOT warnings** from Microsoft.Data.Sqlite — gated on publish; fallback is to
  revisit access strategy.
- **Schema drift** — OpenCode changed storage backends once already (files →
  SQLite). Reconstruction reads only stable columns (`id`, `session_id`,
  `message_id`, `parent_id`, `directory`, `title`, `time_*`, `data`); a schema
  change would require revisiting, but the surface touched is minimal.
- **Subagent watermark contract** — flagged above; resolve by mirroring
  `WatchCommand`.
