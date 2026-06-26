# OpenCode historical import (`kcap import --opencode`) — Design

**Date:** 2026-06-26
**Status:** Approved; rev5 — completeness-gated repair (strict send + replay-above-HWM) after two design passes + two plan-review passes. Implementation plan written (rev3, with Task 0 server-contract confirmation).
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

**Consequence for watermarks (revised — line spaces are incompatible):** the live
path numbers lines by *snapshot* (many per message), import numbers by
*final-state message* (one per message). Unlike Pi/Gemini — whose live and
historical transcripts share one format, making line numbers comparable — an
OpenCode session's two line spaces are **not** comparable. The server's
high-water-mark filter drops batch lines with `line_number <= currentHwm`
*before* normalization, so a naive `startLine`-based resume would either no-op or
mis-resume. Therefore OpenCode import does **not** do line-number resume. See the
explicit classification policy under *Discovery & classification*.

## Architecture

A new `OpenCodeImportSource : IImportSource` in
`src/Capacitor.Cli/Commands/`, structured like
[`PiImportSource`](../../../src/Capacitor.Cli/Commands/PiImportSource.cs): a
**routed** source (`FilePath = ""`, `SupportsTitleGeneration = false`) that runs
through `ImportSessionAsync` rather than the chain worker. The SQLite read helper
(`OpenCodeDb`) lives in the **CLI** project (`src/Capacitor.Cli/Commands/`), not
Core — see the AOT rationale below. Pure path logic stays in Core
(`OpenCodePaths`), and subagent payload builders are reused from Core's
`OpenCodeSubagentDiscovery`.

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

**Classification policy (OpenCode-specific — completeness-gated repair).** The live
and import line spaces are incompatible (see *Line reconstruction*), so there is no
line-by-line resume. But a pure binary "any watermark → skip" is **unsafe**: the
server HWM advances on *transcript ingest*, independent of `session-end`, so a
partial multi-batch import (batch 1 accepted, batch 2 failed) leaves a watermark
and would be skipped forever, never repaired. Instead we gate on **completeness**,
using the server's `ended` signal (we only ever post the terminal `session-end` /
`subagent-stop` on a fully-successful import, so "ended" ⟺ "complete"):

- **`TooShort`** — fewer than `MinLines` *importable* lines (see below).
- **`New`** — no server watermark → import full transcript, line numbers `0..N`.
- **`AlreadyLoaded`** — watermark present **and** the session is `ended` → proven
  complete; skip.
- **`Partial` (repair)** — watermark present but **not** `ended` → re-send the full
  reconstructed transcript with line numbers offset **above** the server HWM
  (carried in `ResumeFromLine`). Already-ingested content dedupes by canonical
  `prt_` id (keep-first); the missing tail lands; `session-end` then flips the
  session to `ended`. Idempotent and self-repairing.
- **`ProbeError`** — watermark probe failed; skip with a reason, like Pi.

This depends on three server behaviors confirmed in **Task 0** of the plan: (1) a
per-session/per-subsession `ended` signal on the `last-line` endpoint; (2)
transcript dedup by canonical id independent of line number; (3) the HWM filter
drops `line_number <= HWM` before normalization. **Fallback** if (1) is
unavailable: treat any watermark as not-ended (always replay above HWM) — correct
but re-sends already-loaded sessions each run. Subsessions are gated identically
via `?agentId=` (HWM + `SubagentCompleted`).

**Importable-line count (role-aware, mirrors Pi's `IsImportRelevantLine`).** The
`MinLines` threshold counts only lines that produce a canonical server event, not
raw synthesized lines. The predicate is **role-aware and hidden-aware**, matching
the server's `OpenCodeTranscriptNormalizer`:
- **user** → a non-hidden `text` part with non-empty text;
- **assistant** → a non-hidden `reasoning` or `text` part with non-empty text;
- **assistant** → a `tool` part with `id` + `callID` + `tool` + terminal
  `state.status` (completed/error).

A part is *hidden* when `synthetic: true` or `ignored: true`. Everything else
(structural `step-start`/`step-finish`, empty text, user-role tools/reasoning,
non-terminal tools, tools missing `callID`/`tool`) is non-importable. The
predicate must not *over*-count, since it gates `TooShort`; keep it in sync with
the server normalizer (same coupling Pi documents) and re-read that file before
finalizing.

## Transcript synthesis

**Ordering:** messages by `(message.time_created, message.id)`; parts within a
message by `(part.time_created, part.id)`. Empirically validated on a real
session: `time_created` is distinct per part within a message (no ties in the
sample) and the `prt_…` ids sort in the same order as `time_created` (OpenCode ids
embed a monotonic timestamp), so `id` is a safe deterministic tie-breaker.

The **pre-merge verification** must assert exact equivalence to live SDK order for
a multi-part / tool-heavy session, specifically: (a) message order, (b) each
message's `parts[]` order, (c) multi-part assistant messages, (d) terminal tool
parts, and (e) either an observed equal-`time_created` tie resolved correctly by
the `id` tie-breaker, **or** an explicit recorded statement that no tie occurred
in the corpus checked. One tie-free sample does not by itself prove the
tie-breaker.

**Avoid the N+1 and unbounded memory:** do not issue one part query per message
and do not hold a whole session in memory. **Order by the driving message's
chronology, not by `message_id` lexically** — `ORDER BY message_id` would group
parts by lexical id, which is *not* guaranteed to equal message chronology. Use a
join ordered by `m.time_created, m.id, p.time_created, p.id`:

```sql
SELECT m.id AS msg_id, m.data AS msg_data, p.id AS part_id, p.data AS part_data
FROM message m JOIN part p ON p.message_id = m.id
WHERE m.session_id = ?
ORDER BY m.time_created, m.id, p.time_created, p.id
```

Group consecutive rows by `msg_id` in a single streaming pass and emit each
`{info, parts}` line into the batch sender as the message's run completes. (A
message with zero parts still needs a row — use a `LEFT JOIN` or a second
message-only cursor so empty messages are not dropped.) Large sessions stream to
batches incrementally.

Streamed via `SessionImporter.SendTranscriptBatches`. Synthesized content is fed
directly rather than reusing the plugin's `~/.cache` files (those only cover
live-seen sessions). If `SendTranscriptBatches` requires a file path, write a
transient file under the scratch dir and clean up in a `finally`.

**Send-failure behavior (strict for OpenCode — revised after plan review).**
`SessionImporter.PostTranscriptBatch` shares behavior across importers that
swallows a failed batch and still counts it as sent, so by default `session-end`
fires even after a partial send. For Pi/Gemini that is repairable on re-run via
`Partial`/resume — **but OpenCode has no resume** (binary New/AlreadyLoaded), so a
partial send that still posted `session-end` would leave a watermark and a re-run
would skip it forever as `AlreadyLoaded`. The "rely on idempotency" assumption
does not hold once resume is removed.

Therefore OpenCode opts into a **strict** transcript send: a `failOnError` flag is
added to `SendTranscriptBatches`/`PostTranscriptBatch` (default `false` → peers
unchanged) that checks `IsSuccessStatusCode` and throws on any rejected batch.
OpenCode passes `failOnError: true` for both the parent transcript and every
child, and **aborts the import (returns `Failed`) before any terminal lifecycle
POST** (`session-end`, `subagent-stop`) if a batch fails.

Critically, a partial send **may still have advanced the server HWM** (earlier
batches were accepted) — withholding `session-end` does *not* undo that. So the
strict sender alone does not make re-runs safe; it works **together with the
completeness-gated classification**: because `session-end` was withheld, the
session stays **not-ended**, so a re-run classifies it `Partial` and **repairs** it
(replay above HWM), rather than skipping it as `AlreadyLoaded`. Strict-send (don't
mark complete on failure) + completeness-gated repair (re-send when not complete)
are the two halves of the same guarantee. This is the one place OpenCode diverges
from "match existing importers" — justified by the no-resume line space. Hardening
the shared sender for the *other* importers remains out of scope.

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
3. For each child with `parent_id` = this root, in deterministic
   `(session.time_created, session.id)` order, apply the **same completeness gate
   as the parent**, keyed by `(rootId, agentId)` via `?agentId=`:
   - child `ended` (SubagentCompleted) → **skip** (already complete);
   - else `POST /hooks/subagent-start` (parent session id, `agent_id` =
     `CanonicalAgentId(childSid)`, `agent_type` from child's `info.agent`, fallback
     `"subagent"`, `transcript_path` = the child's temp file) — **fail-closed**;
   - stream the synthesized child transcript under `agent_id = childSid`, with line
     numbers offset above the child HWM (0 when none — repair otherwise),
     `failOnError: true`;
   - `POST /hooks/subagent-stop`.
4. `POST /hooks/session-end/opencode` (parent).

**Subagent watermark/repair:** children are completeness-gated exactly like the
parent. A child that already has `SubagentCompleted` is skipped; a child with a
watermark but no completion is repaired (replay above its HWM). A child send
failure throws and **aborts the whole import before the parent's `session-end`**
(strict) — so the parent stays not-ended and the next run repairs the unfinished
child. The `agentId`-scoped `last-line` query is the same one the live watcher
uses.

## Lifecycle, idempotency, resume

Mirrors Pi's idempotency model, with the parent/child sequencing from *Subagent
parity*:

- Lifecycle-before-transcript ordering for the **parent**: `session-start/opencode`
  → parent transcript → **child subagent phase** → `session-end/opencode` (the full
  order is enumerated under *Subagent parity*; `session-end` is always last so
  `SessionEnded` follows every `SubagentCompleted`). A transcript that advanced the
  watermark past a failed lifecycle POST would orphan the session; idempotent
  server-side via deterministic event ids, so re-runs are safe.
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
  (WireMock): `New` (no watermark), `AlreadyLoaded` (watermark + `ended:true`),
  and `Partial`/repair (watermark, no `ended` → carries the HWM in
  `ResumeFromLine`).
- Repair path: a not-ended session replays the transcript with line numbers
  **above** the HWM and posts `session-end` (integration test).
- Importable-line counting: a session of purely structural lines
  (`step-start`/`step-finish`, empty user text, non-terminal tools) classifies
  `TooShort`, not `New`.
- Part-ordering: the join query preserves message chronology even when lexical
  `message_id` order would diverge.

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
- **Messages that normalize to no canonical event:** counted as non-importable
  for `MinLines` (see *Classification policy*), but still emitted in the stream so
  the server sees the full message sequence; the normalizer drops them. They must
  not advance the importable-line count nor be silently elided from the transcript.
- **Nested child sessions (grandchildren):** if a child itself has children, the
  query `WHERE parent_id = <root>` finds only direct children. Decide explicitly:
  v1 imports **direct children only** and treats any deeper nesting as flat under
  the root's children, or skips grandchildren with a logged note. Confirm whether
  OpenCode actually nests beyond one level before adding handling (YAGNI — current
  observed data is one level).
- **Multiple children ordering:** imported in `(time_created, id)` order (see
  *Subagent parity*) for deterministic, reproducible runs.
- **Mixed live/historical (partially live-captured) session:** if it ran to
  `session-end` it is `ended` → `AlreadyLoaded` (skip); if it was truncated
  (not `ended`) → `Partial` → repaired by replaying above the HWM (see
  *Classification policy*).

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
- **Line-space incompatibility (live vs import)** — handled by completeness-gated
  classification: no line-by-line resume; repair replays the full transcript above
  the server HWM and dedupes by `prt_` id. Correctness rests on the three server
  behaviors confirmed in plan Task 0 (ended signal, dedup-by-canonical-id, HWM
  filter). If the `ended` signal is unavailable, the fallback (always replay above
  HWM) keeps correctness at the cost of re-sending already-loaded sessions.
