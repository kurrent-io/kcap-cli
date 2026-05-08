# AI-73 — Codex historical session import (`kapacitor history --codex`)

## Problem

Server PR #576 added `CodexNormalizer` and a `vendor` discriminator on `TranscriptBatch`. The CLI side has no path that walks `~/.codex/sessions/` and POSTs the rollouts with `vendor: "codex"`, so users can't backfill their Codex history. This spec closes that gap.

## Scope

Extend `kapacitor history` with a `--codex` flag that mirrors the existing (default) Claude mode but targets the Codex rollout layout. Reuse the existing classify → plan → import → titles/summaries pipeline; the only vendor-specific pieces are discovery, metadata extraction, the title context extractor, and the new `vendor` field on outgoing transcript batches.

In scope:

- `--codex` flag — switches the discovery + metadata stage to the Codex rollout layout.
- `--since YYYY-MM-DD` — filters by date (cheap directory pruning for Codex; falls back to per-file metadata for Claude).
- `Vendor` field on `TranscriptBatch` (CLI-side mirror of the server record).
- Codex-shaped session-start hook payload (cwd, model from `model_provider`, repo from `git` block, `started_at` from session_meta `timestamp`).
- Codex-shaped title context extractor (first user `input_text` + first assistant `message.content[].text`).
- Windows path resolution via `%USERPROFILE%\.codex\sessions\...` (same `Environment.SpecialFolder.UserProfile` mechanism `ClaudePaths` uses).

Out of scope (per ticket):

- Live SignalR streaming / hooks — AI-70.
- Daemon-spawned hosted Codex agents — AI-68 / AI-72.
- Subagent transcript walk — Codex has no analog.
- Continuation chains — Codex rollouts have no on-disk slug; every rollout is a single-element chain (the existing `BuildImportChains` already handles slug-less sessions as length-1 chains, so no special-case needed).

## Discovery

Codex layout (per machine inspection):

```
~/.codex/sessions/YYYY/MM/DD/rollout-<ISO-ts>-<uuid>.jsonl
```

`<uuid>` is the session id and is also present in the first JSONL line's `payload.id`. The CLI extracts it from the filename (cheap, no I/O) and validates against the metadata at metadata-extraction time.

`--since YYYY-MM-DD` is applied at the directory level: enumerate the year/month/day directories sorted lexicographically (which equals chronological for ISO-shaped names) and skip anything strictly less than the cutoff. For Claude mode, `--since` is best-effort: applied after metadata extraction by comparing `meta.FirstTimestamp ?? File.GetLastWriteTimeUtc(path)`.

## Codex session_meta shape (real example, abbreviated)

```json
{
  "timestamp": "2026-05-07T15:51:46.684Z",
  "type": "session_meta",
  "payload": {
    "id": "019e0322-05fc-7570-be65-75719c3ea861",
    "timestamp": "2026-05-07T15:50:21.989Z",
    "cwd": "/Users/alexey/dev/temp/Kurrent.Capacitor",
    "originator": "codex-tui",
    "cli_version": "0.128.0",
    "source": "cli",
    "model_provider": "openai",
    "git": {
      "commit_hash": "...",
      "branch": "main",
      "repository_url": "https://github.com/owner/repo"
    }
  }
}
```

Mapping to the session-start hook:

| Hook field            | Source                                                    |
| --------------------- | --------------------------------------------------------- |
| `session_id`          | UUID from filename (validated against `payload.id`)       |
| `transcript_path`     | full rollout path                                         |
| `cwd`                 | `payload.cwd`                                             |
| `started_at`          | `payload.timestamp` (the inner one — when codex started)  |
| `model`               | `payload.model_provider` (provider name, no model name in session_meta — fine; the server's CodexNormalizer pulls the actual model from `event_msg`/response-item lines) |
| `source`              | `"Startup"` (matches Claude mode literal)                 |
| `hook_event_name`     | `"session_start"`                                         |
| `repository.remote_url` / `branch` | `payload.git.repository_url` / `payload.git.branch` (when present) — git owner/repo derived via existing `GitUrlParser.ParseRemoteUrl` |
| `vendor` (batch)      | `"codex"` on every `TranscriptBatch` POST                 |

`previous_session_id` is omitted (no chain detection for Codex).

## Wire change: `TranscriptBatch.Vendor`

Add an optional `vendor` field to `src/Kapacitor.Core/Models.cs`:

```csharp
record TranscriptBatch {
    [JsonPropertyName("session_id")]   public required string  SessionId   { get; init; }
    [JsonPropertyName("agent_id")]     public string?          AgentId     { get; init; }
    [JsonPropertyName("lines")]        public required string[] Lines      { get; init; }
    [JsonPropertyName("line_numbers")] public int[]?           LineNumbers { get; init; }
    [JsonPropertyName("repository")]   public RepositoryPayload? Repository { get; init; }
    [JsonPropertyName("vendor")]       public string?          Vendor      { get; init; }
}
```

Defaults to null → omitted by the serializer → server treats as `"claude"` (its existing default). Watcher path is unchanged (it goes via SignalR, not HTTP — already passes `"claude"` as a positional arg).

## Code surface

- `src/Kapacitor.Core/CodexPaths.cs` (new) — `Sessions` directory + `--since` directory walk.
- `src/Kapacitor.Core/Models.cs` — add `Vendor` field to `TranscriptBatch`.
- `src/kapacitor/Commands/HistoryCommand.cs`:
  - `HandleHistory(...)` gains `bool codex` and `DateOnly? since`.
  - `DiscoverCodexRollouts(string sessionsDir, DateOnly? since)` (sibling to `DiscoverTranscripts`) — returns `(SessionId, FilePath, EncodedCwd)` tuples like the Claude path. `EncodedCwd` is left empty for Codex: the day folder name isn't a Claude-style hyphen-encoded path, and an empty string makes `DecodeCwdFromDirName` return null so callers skip cwd-dependent work cleanly when `session_meta` parsing fails.
  - `ExtractCodexSessionMetadata(filePath)` — pulls cwd, model_provider, first timestamp, git block from the first `session_meta` line.
  - Vendor threads through `ImportSingleSessionAsync` (only impacts the session-start hook and the batch POSTs).
- `src/kapacitor/Commands/SessionImporter.cs`:
  - `ImportSessionAsync(..., string vendor)` — passes vendor into batch posts, skips subagent walk when `vendor == "codex"`.
  - `SendTranscriptBatches(..., string vendor)` and `PostTranscriptBatch(..., string vendor)` — vendor lands on the JSON payload.
- `src/kapacitor/Commands/TitleGenerator.cs`:
  - `ExtractCodexTitleContext(filePath)` — first `response_item` with `role:"user"` containing `input_text`, skipping `<environment_context>` blocks; first `response_item` with `role:"assistant"` containing `output_text`. Same return shape and 200-line scan budget.
- `src/kapacitor/Program.cs` — parse `--codex` and `--since`, route to `HandleHistory`.
- `src/Kapacitor.Core/Resources/help-history.txt` — document new flags.

## Tests

`test/kapacitor.Tests.Unit/`:

- `CodexPathsTests` — fixture tree of `YYYY/MM/DD/rollout-*.jsonl`, asserts `--since` cutoff prunes correctly.
- `HistoryCommandTests` (extend) — `ExtractCodexSessionMetadata` parses cwd/model_provider/git from a fixture line; missing-git tolerated.
- `TitleGeneratorTests` — `ExtractCodexTitleContext` returns first user input_text + first assistant output_text; tolerates missing assistant; truncates user at 500 chars (matching Claude path).
- `TranscriptBatch` JSON shape — when `Vendor` is null the field is absent; when `"codex"` it serializes as `"vendor":"codex"`.
- Existing `HistoryCommandTests` and `SessionImporterScanTests` keep passing (no shape changes to Claude transcripts).

## Verification

- `dotnet build src/kapacitor/kapacitor.csproj` — no warnings.
- `dotnet publish src/kapacitor/kapacitor.csproj -c Release` — no IL3050/IL2026 (we're adding a property and a literal vendor string; no new reflection or dynamic JSON paths).
- `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj` — all green.
- Manual smoke (post-merge, against staging): `kapacitor history --codex --since 2026-05-01` imports the user's recent rollouts and they render in the dashboard with chat / events / trace.
