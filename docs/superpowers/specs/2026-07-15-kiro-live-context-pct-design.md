# Kiro live context-% (AI-1286 Phase 7) — CLI design

**Status:** draft for review
**Linear:** AI-1286 (Phase 7). Server side shipped in Phases 1–5; this is the deferred CLI piece.

## Problem

Kiro reports a per-turn **context-fill percentage** (no token counts). The server already derives
`SessionStats.ContextUsagePercent` from a `_kcap_usage.context_usage_percentage` field on Kiro
assistant transcript lines — and does so identically for live and import (shared
`KiroTranscriptNormalizer.StampUsage`). But that field is injected **only by the import path**
(`KiroImportSource` → `KiroUsage.EnrichLine`); the **live watcher** tails the raw `~/.kiro/sessions/cli/{id}.jsonl`, which never contains it. So a Kiro session's context-% appears only after a post-hoc `kcap import --kiro`, never during a live session. Phase 7 closes that gap.

## Why not the "per-turn stop hook" the roadmap sketched

The AI-1286 design bullets propose keying the live % off "Kiro's per-turn `stop` hook." That path is
**blocked by existing, deliberate constraints** (`KiroHooksParser.cs:16-24`, `KiroHookCommand.cs:50`):

- Kiro's `stop` STDIN payload carries **no `session_id`**, so a stop hook can't target the right watcher.
- Any stdout from a Kiro `stop` hook is **re-injected into the agent**, looping it — kcap deliberately subscribes to `agentSpawn` only.

So the literal stop-hook plan is a dead end. Where the context-% actually lives is the **sibling
`{id}.json`** (`user_turn_metadatas[].context_usage_percentage`) — the same file the CLI already
opens at `agentSpawn` to read the model (`KiroHookCommand.ReadKiroModel`). The proven in-repo
pattern for "usage lives in a sibling file, not the transcript" is the Antigravity live poll
(`WatchCommand.AppendAntigravityUsageLines`).

## Design: live in-place enrichment of Kiro assistant lines

Antigravity emits *synthetic* `USAGE` transcript lines because its usage is in a DB and the server
maps `USAGE` → a dedicated event. **Kiro is different**: the server expects the percentage on the
**AssistantMessage line itself** (`StampUsage` reads `data._kcap_usage.context_usage_percentage`).
So the live fix mirrors *import*, done at flush: **enrich Kiro AssistantMessage lines in place**
just before sending, reusing the exact functions import already uses.

Enrichment runs **at flush, immediately before `SendTranscriptBatch2`** — NOT at drain time. Kiro
session watchers buffer initial lines until the 10-line threshold (`WatchCommand.cs:933-951`);
enriching at drain would stamp an early AssistantMessage raw (its `.json` turn-metadata not yet
written) and then flush it raw even though the metadata arrived before the flush. So the step runs on
the batch about to be sent (buffered or live), for a `kiro` watcher:

1. Derive the sibling `.json` path **from the transcript path** — `Path.ChangeExtension(transcriptPath, ".json")`.
   It must NOT be derived from the watcher's `sessionId`: `KiroHookCommand` passes the **dashless
   canonical** id to the watcher, while Kiro's on-disk files use the **dashed** id, so a `sessionId`-derived
   `KiroPaths.SessionJson(...)` would miss the file.
2. If the outgoing batch contains any Kiro **AssistantMessage** line, read that sibling `.json` once and
   compute `KiroUsage.AnchorMap(metadataJson)`. Replace each AssistantMessage line with
   `KiroUsage.EnrichLine(line, anchors)` — which stamps `data._kcap_usage.context_usage_percentage`
   (and the dormant credits/token hedge) exactly as import does.
3. **Best-effort** (AI-728): the whole step is wrapped; any read/parse/missing-anchor failure logs and
   falls back to the raw line. Never break the flush.

No new transcript-line band, no watermark, no new server event: the enriched line rides the existing
batch, and the shared `KiroTranscriptNormalizer` already stamps `ContextUsagePercent` off it — no
server change.

### Live-or-never: captured at flush, NOT backfillable later

The % must be on the AssistantMessage line **at first send** — there is no later backfill. The server
dedupes Kiro events by canonical id `(sessionId, messageId, kind)` (`KiroTranscriptNormalizer.cs:297-305`)
and the write path returns on an already-seen id (`SessionWriter.TranscriptPipeline.cs:443-451`); the
live watcher advances its processed position after a successful batch (`WatchCommand.cs:1010-1014`) and
the server filters lines at/below the session high-water mark (`SessionWriter.TranscriptPipeline.cs:155-177`).
So a *later* enriched copy of an already-accepted line — a re-send OR a subsequent `kcap import` — is
**skipped, not merged**; it cannot add the % to an event already written. (Import still fully captures
the % for a session that was *never* live-watched: its enriched line is then the first the server sees.)

Consequence, stated honestly: for a live session a turn's % is captured **iff** its
`context_usage_percentage` is in the sibling `.json` at flush time. Because enrichment runs at flush
(after buffering; the AssistantMessage line is the turn's *last* line, written when the turn — and its
`.json` metadata — complete), the metadata is present in the overwhelmingly common case. A genuine
race where the `.json` write lags the flush leaves that one turn's % absent for the live session — a
best-effort miss, **not** "fixed on import." We deliberately do NOT hold/delay the AssistantMessage
line to wait for the `.json`: holding it while later lines flush would reorder the turn stream, a worse
defect than a rare missing %.

### Acceptance criteria

- A live Kiro session whose sibling `.json` carries a turn's `context_usage_percentage` at flush → the
  server records `ContextUsagePercent` for that session (no import needed).
- A never-live Kiro session imported with `kcap import --kiro` → % captured (import path unchanged).
- Enrichment never reorders, drops, or duplicates lines, and never breaks the flush on a malformed/missing `.json`.

### Why re-read the `.json` at each flush (not a one-shot at agentSpawn)

The `.json` accretes a new `user_turn_metadatas` entry per turn, so the AnchorMap must be recomputed
as turns land — a one-shot read at `agentSpawn` would only ever have the first turn. Re-reading the
small `.json` at each flush (only when a Kiro AssistantMessage line is present) is cheap.

## Scope

- `WatchCommand`: a `kiro`-gated in-place enrichment step at flush (new `EnrichKiroContextUsage`
  helper), calling `KiroUsage.AnchorMap` + `KiroUsage.EnrichLine`, run on the batch about to be sent
  (after the below-threshold buffer flush decision, immediately before `SendTranscriptBatch2`). The
  sibling `.json` is derived from the **transcript path** (`Path.ChangeExtension(transcriptPath, ".json")`),
  never from the dashless `sessionId`.
- No server change (the ingest path is shared and already live).
- README: update the Kiro section (currently says imported Kiro sessions converge with live; note
  live now carries context-% best-effort at flush time).

## Test plan

Mirror the existing Kiro/Antigravity live-usage unit tests (`KiroUsageTests`, `AntigravityWatchExtractorTests`):

- **Enrich path**: a Kiro AssistantMessage line + a sibling-`.json` metadata blob with a
  `context_usage_percentage` → the helper returns the line with `data._kcap_usage.context_usage_percentage`
  set (assert via the same anchor→line mapping `KiroUsageTests` uses).
- **Missing metadata**: no matching turn in the `.json` → line returned unchanged (no `_kcap_usage`).
- **Non-assistant / non-kiro line**: passed through untouched.
- **Malformed / missing `.json`**: helper swallows + returns the raw line (best-effort).
- **Buffer-flush ordering (finding 2)**: a Kiro AssistantMessage line buffered *before* its metadata
  exists, with the metadata present by the time the below-threshold buffer flushes → the flushed line
  is enriched (proves enrichment is at flush, not drain), and line order is preserved.
- **Dashed-file / dashless-session (finding 3)**: a watcher whose `sessionId` is the dashless canonical
  id but whose transcript file is the dashed name → the sibling `.json` is still located (path derived
  from the transcript), and the line is enriched.

## Out of scope

- Token counts for Kiro (none upstream — unchanged).
- Any Kiro `stop`-hook subscription (blocked, see above).
- Historical/import path (already complete).
