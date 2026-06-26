# OpenCode historical import (`kcap import --opencode`) — Design

**Date:** 2026-06-26
**Status:** Approved (design), revised after Codex design review, pending implementation plan
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
`id,messageID,sessionID,text,type`).

**This is final-state reconstruction, not byte-identical to live JSONL.** The
live plugin appends *multiple* snapshots for one message as its parts stream in
or a tool transitions to a terminal state ([dedup key on
`parts.length` + terminal-tool count](../../../src/Capacitor.Cli.Core/OpenCode/OpenCodeExtensionInstaller.cs)).
SQLite holds only the final state, so import emits *one* line per message. This
is **normalizer-compatible**: the server keeps the first non-skipped snapshot per
`prt_` id and skips non-terminal tool snapshots, so the single final-state line is
exactly the record it retains. No server changes required.

**Consequence for watermarks:** a session that was also live-ingested has a
larger server line count (more snapshot lines) than import reconstructs, so
line-number comparison across the two paths is not meaningful. This is safe
because (a) a live-captured session ran to session-end and is already fully
ingested → import classifies it `AlreadyLoaded` and skips, and (b) content is
keyed by deterministic message/`prt_` ids, so any re-send is idempotent
regardless of line numbering.

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
| `OpenCodeDb` (**CLI**, not Core) | Open db read-only; query roots/children; synthesize `{info,parts}` lines per session | Microsoft.Data.Sqlite |
| `OpenCodeImportSource` (CLI) | `IImportSource` impl: discover, classify, import (roots + subagent children) | `OpenCodeDb`, `OpenCodePaths`, `OpenCodeSubagentDiscovery`, `SessionImporter` |
| `VendorSelection` / `Program.cs` | wire `--opencode` filter + register source | — |

**Why `OpenCodeDb` lives in the CLI project, not Core:** `Capacitor.Cli.Core` is
marked `IsAotCompatible`/`IsTrimmable` and is referenced by the AOT-published
**daemon** as well as the CLI. Adding the Microsoft.Data.Sqlite +
SQLitePCLRaw native bundle to Core would push that dependency onto the daemon,
which never imports. Import is a CLI-only concern, so the SQLite-touching code
stays in `Capacitor.Cli`. Pure path logic remains in Core (`OpenCodePaths`).

## Data access

- Packages: **Microsoft.Data.Sqlite** + **SQLitePCLRaw.bundle_e_sqlite3**
  (bundles native `e_sqlite3`; AOT-compatible, cross-platform), pinned via the
  repo's central package management, referenced only by the **CLI** project.
- Open **read-only** (`Mode=ReadOnly`), tolerating WAL (OpenCode may be running);
  test reading while OpenCode is actively writing.
- `IsAvailable` = `File.Exists(Path.Combine(OpenCodePaths.DataDir(), "opencode.db"))`.
- **AOT gate (expanded):** publish AOT for the major RIDs and verify zero
  `IL3050`/`IL2026` for **both** the CLI and the daemon, and record the binary
  size delta. If Microsoft.Data.Sqlite emits trimming warnings, revisit access
  strategy before merge.

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

**Ordering:** messages by `(time_created, id)`; parts within a message by
`(time_created, id)`. Empirically validated on a real session: `time_created` is
distinct per part within a message (no ties in the sample) and the `prt_…` ids
sort in the same order as `time_created` (OpenCode ids embed a monotonic
timestamp), so `id` is a safe deterministic tie-breaker. Implementation must
re-verify ordered output against live SDK order for a multi-part / tool-heavy
session before merge.

**Avoid the N+1 and unbounded memory:** do not issue one part query per message
and do not hold a whole session in memory. Either (a) one ordered pass —
`SELECT … FROM part WHERE session_id = ? ORDER BY message_id, time_created, id`
joined/grouped to its message in a single streaming read — or (b) a forward
cursor that emits each `{info, parts}` line into the batch sender as it is built.
Large sessions stream to batches incrementally.

Streamed via `SessionImporter.SendTranscriptBatches`. Synthesized content is fed
directly rather than reusing the plugin's `~/.cache` files (those only cover
live-seen sessions). If `SendTranscriptBatches` requires a file path, write a
transient file under the scratch dir and clean up in a `finally`.

**Send-failure behavior (known limitation, accepted):**
`SessionImporter.PostTranscriptBatch` swallows `HttpRequestException` and still
counts the batch as sent, so a failed batch does not abort the import and
`session-end` still fires. This is the **shared** behavior of every routed
importer (Gemini/Pi/Kiro/Copilot), not OpenCode-specific. We accept it here for
consistency and rely on server-side idempotency (deterministic message/`prt_`
ids) so a re-run repairs a partially-sent session. Hardening
`SendTranscriptBatches` across all importers is out of scope for this issue.

## Subagent (child) parity

Children (`parent_id` set) are imported as **subagents of their parent**, not as
standalone sessions, following the **import** precedent in
[`GeminiImportSource.ImportSubagentsAsync`](../../../src/Capacitor.Cli/Commands/GeminiImportSource.cs)
and reusing the OpenCode payload builders in
[`OpenCodeSubagentDiscovery`](../../../src/Capacitor.Cli.Core/OpenCode/OpenCodeSubagentDiscovery.cs).

**Ordering (corrected — was a blocker):** children are imported **between the
parent's transcript and the parent's `session-end`**, so `SubagentStarted` /
`SubagentCompleted` land in the parent stream *ahead of* `SessionEnded` — exactly
as `GeminiImportSource` sequences it ([:229](../../../src/Capacitor.Cli/Commands/GeminiImportSource.cs)).
Full order per root:

1. `POST /hooks/session-start/opencode` (parent).
2. Stream parent transcript.
3. For each child with `parent_id` = this root:
   - `POST /hooks/subagent-start` (parent session id, `agent_id` = `CanonicalAgentId(childSid)`,
     `agent_type` from child's `info.agent`, fallback `"subagent"`) — **fail-closed:
     skip the child's content if start fails**, so a child stream never exists
     without its `SubagentStarted`.
   - Stream the synthesized child transcript under `agent_id = childSid`,
     `startLine: 0`.
   - `POST /hooks/subagent-stop`.
4. `POST /hooks/session-end/opencode` (parent).

**Watermark (open item resolved):** children are **not** watermark-probed. The
import precedent sends each child from `startLine: 0` and relies on server-side
idempotency (deterministic ids) — no `agentId`-scoped `last-line` query. (The live
watcher *does* use an `agentId`-scoped probe because it streams incrementally;
import does not need it and Gemini import does not do it.) A re-import re-sends
children idempotently. Child failures never fail the already-imported parent.

## Lifecycle, idempotency, resume

Mirrors Pi exactly:

- Lifecycle-before-transcript ordering: `session-start/opencode` → transcript →
  `session-end/opencode`. A transcript that advanced the watermark past a failed
  lifecycle POST would orphan the session; idempotent server-side via
  deterministic event ids, so re-runs are safe.
- **Title (corrected contract):** unlike Pi, OpenCode's db has a real
  `session.title`. Forward it via **`POST /hooks/set-title`** after the transcript
  send — matching the established native-title importers
  ([Copilot](../../../src/Capacitor.Cli/Commands/CopilotImportSource.cs),
  [Kiro](../../../src/Capacitor.Cli/Commands/KiroImportSource.cs)) — **not** by
  stuffing `title` into the session-start payload (the start hook is not confirmed
  to consume it). Best-effort: a title miss must not fail the import.
- `started_at` = `session.time_created`; `ended_at` = `session.time_updated`
  (epoch **milliseconds** in the db — confirmed sample values ~1.78e12 — convert
  to `DateTimeOffset` via `FromUnixTimeMilliseconds`).

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

## Edge cases

- **Zero-message / empty sessions:** treat as `TooShort` (below `MinLines`) and
  skip — no lifecycle POSTs, consistent with Pi/Gemini empty-transcript handling.
- **Null/empty `session.directory`:** import with `Cwd = null`. A `--cwd` filter
  cannot match a null directory, so such sessions are excluded under `--cwd`
  (same as Pi when a header has no cwd). Scope resolution (`--org`/`--repo`) that
  needs a repo simply can't attribute them — they fall to the unattributed bucket.
- **`time_created` units:** epoch **milliseconds** (see Lifecycle) — guard against
  a future seconds-based column by sanity-checking magnitude.
- **Raw `ses_…` ids:** confirm with the server that a non-GUID session id is
  accepted as a stable key on the lifecycle + transcript routes. The live
  OpenCode hook path already posts these ids, so this is established, but the
  importer must canonicalize identically (no GUID normalization;
  `CanonicalAgentId` only strips dashes, of which `ses_…` has none).

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
- **Subagent watermark contract** — resolved: import sends children from
  `startLine: 0` and relies on idempotency (Gemini import precedent), so no
  `agentId`-scoped watermark is needed.
- **Part-ordering vs SDK** — empirically validated on one session; implementation
  must re-verify against live SDK order for a multi-part/tool session before merge.
