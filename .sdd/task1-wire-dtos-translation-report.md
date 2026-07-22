# AI-688 Option B — Task 1: wire DTOs + per-update translation (report)

**Status:** DONE.

## What was built

### A. Daemon-local wire DTOs (`src/Capacitor.Cli.Core/Models.cs`)

Read the server contract read-only from
`/Users/tony/Documents/kcap-server-wt/ai-686-acp-permission-bridge/src/Capacitor.Server.Core/Acp/AcpEventEnvelope.cs`
(the only file there — `AcpBatchAck` lives in the same file, returned from `CapacitorHub.AcpSessionEvents`).
Mirrored, field-for-field, alongside `AcpInteractionRequest`/`AcpInteractionDecision`:

- `AcpEventKind` — static class of the same 7 string constants (`session_started`, `user_message`,
  `assistant_text`, `assistant_thinking`, `tool_call`, `tool_result`, `session_ended`). Not
  registered in `CapacitorJsonContext` (it's a plain string-constants holder, nothing to serialize).
- `AcpEventEnvelope` — `readonly record struct` (16 properties, same names/defaults as the server's
  `sealed record`, same "flat, Kind-discriminated, exactly one per-kind group populated" shape).
- `AcpBatchAck` — `readonly record struct(long AcceptedSeq, long PersistedSeq, long? ExpectedNextSeq = null)`,
  identical to the server's positional record.

**Key finding:** neither server type has an explicit `[JsonPropertyName]` — both ride the wire under
the server's SignalR `AddJsonProtocol` config (`JsonNamingPolicy.SnakeCaseLower`, confirmed at
`Program.cs:953`), which is exactly `CapacitorJsonContext`'s own
`JsonSourceGenerationOptions(PropertyNamingPolicy = SnakeCaseLower)`. So "field-for-field" reduces to
keeping the C# property *names* identical — no per-property attribute needed, consistent with the
existing `AcpInteractionRequest`/`HostedPermissionRequest` mirror-DTO convention in this file. `Kind`
is a plain `string` on both sides (not an enum), so `UseStringEnumConverter` doesn't apply here.

Both new types registered via `[JsonSerializable(typeof(AcpEventEnvelope))]` /
`[JsonSerializable(typeof(AcpBatchAck))]` in `CapacitorJsonContext`.

### B. `AcpSessionUpdate` + `Reduce()` tool fields

`src/Capacitor.Cli.Daemon/Acp/AcpSessionUpdate.cs`: added `ToolInputJson`, `ToolResultText`,
`ToolIsError` (bool, default false) to the record.

`src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs` `Reduce()`:
- `tool_call` → `ToolInputJson` = verbatim JSON text of the update's `rawInput` (if present/non-null).
- `tool_call_update` → `ToolResultText` extracted via a new `ExtractToolResultText` helper (prefers
  the ACP-spec `content` array's `{type:"content", content:{type:"text", text:"..."}}` blocks,
  newline-joined; falls back to verbatim `rawOutput` JSON text; `null` if neither present) and
  `ToolIsError = status == "failed"`. Extraction is unconditional (regardless of status) — Reduce()
  stays a mechanical extractor; the terminal-status gate lives in the translator (C), per the brief.

### C. `AcpEventTranslator` (`src/Capacitor.Cli.Daemon/Acp/AcpEventTranslator.cs`)

Pure `internal static class`, one method `Translate(AcpSessionUpdate, long seq, string timestampIso,
string? aggregatedText = null, ILogger? logger = null) : AcpEventEnvelope?` plus two builders,
`BuildSessionStarted(...)` and `BuildUserMessage(seq, timestampIso, text)`. Implements §2.2 exactly:
message/thought → `AssistantText`/`AssistantThinking` (aggregatedText override else the update's own
text); `ToolCall` → `ToolCall` envelope (`ToolName` = the update's `ToolTitle` — ACP's `tool_call` has
no separate machine "name" field, only `title`/`kind`; `title` is the closest analogue and is what the
server's `AssistantToolCallsGenerated.ToolCallInfo.ToolName` expects for display — see "Concerns"
below); `ToolCallUpdate` → `ToolResult` only when status is terminal (`completed`/`failed`) AND
`ToolResultText` is non-null, else `null`; `Plan`/`AvailableCommands`/`Unknown` → `null` (`Unknown`
logs `Raw` via the optional `ILogger` at debug). Every emitted envelope relies on the record's own
`ContractVersion = 1` default — the translator never overrides it.

## Tests (TDD, genuine red → green)

Confirmed red first: `dotnet build` on the test project failed with ~55 compile errors (missing
types/members) before any implementation code was written — see the transcript for the pre-fix error
list, all resolved by A/B/C above.

New files:
- `test/Capacitor.Cli.Tests.Unit/Acp/AcpEventTranslatorTests.cs` — 15 tests, pure `Translate`/builder
  unit tests (no ACP wire/process): every kind → envelope kind/fields; aggregatedText override for
  both message and thought; `ToolCall` carries `ToolInputJson`; status-only (`pending`/`in_progress`)
  → null; terminal-but-empty → null; terminal `completed`/`failed` with content → `ToolResult`
  (+`IsError`); `Plan`/`AvailableCommands`/`Unknown` → null; `BuildSessionStarted`/`BuildUserMessage`
  shapes; `ContractVersion`/`Seq`/`TimestampIso` stamped.
- `test/Capacitor.Cli.Tests.Unit/Acp/AcpEventEnvelopeWireCompatTests.cs` — 5 tests, the wire-compat
  guard: serializes a daemon `AcpEventEnvelope` via `CapacitorJsonContext` and asserts every one of
  the 16 properties appears under its expected snake_case name (`contract_version`, `seq`, `kind`,
  `text`, `thinking_encrypted`, `tool_call_id`, `tool_name`, `tool_input_json`, `tool_result`,
  `tool_is_error`, `model`, `cwd`, `raw_session_id`, `session_mode`, `end_reason`, `timestamp_iso`),
  round-trips it back, locks in the `AcpEventKind` string constants, and round-trips `AcpBatchAck`
  (including a server-shaped gap-reject JSON literal) under its `accepted_seq`/`persisted_seq`/
  `expected_next_seq` names.
- `test/Capacitor.Cli.Tests.Unit/Acp/FakeAcpAgent.cs` — extended `BuildToolCallUpdate`/
  `BuildToolCallStatusUpdate` with new optional `rawInputJson`/`resultText`/`rawOutputJson` params
  (backward compatible — existing call shape untouched) to script the new wire shapes.
- `test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimeTests.cs` — 7 new `Reduce()`-level tests
  (through the real runtime + `FakeAcpAgent`, mirroring the file's existing pattern, since `Reduce()`
  itself is private): `ToolInputJson` captured from `rawInput` / stays null without it; status-only
  update captures status but no `ToolResultText`; terminal `completed` captures `ToolResultText` from
  the `content` text block; terminal `failed` sets `ToolIsError`; terminal update with no content
  block falls back to `rawOutput`.

## Verification

- `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Debug` → **0 Warning(s), 0
  Error(s)** (builds Core transitively).
- AOT gate: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E
  'IL[23][01][0-9]{2}'` → **no output** (clean).
- `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug
  --no-build` → **2677 passed, 0 failed, 1 skipped** (the pre-existing gated live-ACP E2E test,
  `KCAP_ACP_LIVE=1`-gated). The previously-flaky `HttpClientExtensionsRetryTests` retry-timeout test
  did not flake this run. Scoped runs of just the new/touched files (translator: 11/11, wire-compat:
  5/5, `AcpHostedAgentRuntimeTests`: 16/16) all green individually too.

## Concerns / judgment calls (server contract areas that were ambiguous or spec-derived)

1. **`ToolName` source field.** The server's `AcpSessionMapper.BuildToolCall` feeds
   `AcpEventEnvelope.ToolName` straight into `ToolCallInfo.ToolName` (the canonical tool identifier),
   but ACP's `tool_call` update (per `docs/acp-probe-findings.md`, spec-derived/not probe-confirmed)
   has no separate machine "name" field — only `title` (human-readable) and `kind` (category enum:
   read/edit/execute/etc.). I mapped `ToolName := update.ToolTitle`. This is the best available
   analogue but is a judgment call a later task (or a real tool-using probe run) should revisit if
   Cursor's actual `tool_call` shape turns out to carry something more name-like.
2. **`ToolCallUpdate` "terminal" definition.** The design spec (§2.2 footnote 2) says "terminal/
   completed" without enumerating every status value. I treated both `completed` and `failed` as
   terminal (both are final ACP `ToolCallStatus` values per the general spec) — `pending`/
   `in_progress`/missing are non-terminal. This is what makes the `failed` + `IsError` test case
   meaningful; if the real wire never uses `failed` as a `tool_call_update` status (e.g. failure is
   signaled differently), this decision is easy to revisit in the aggregation task.
3. **Result-content extraction algorithm.** `ExtractToolResultText` implements one reasonable,
   spec-derived reading of ACP's `ToolCallContent` union (only the `{type:"content",
   content:{type:"text", text}}` text-block shape is understood; `diff`/`terminal` content variants
   are not extracted, degrading to "no text from this block" rather than throwing) with a `rawOutput`
   fallback. None of this is probe-confirmed (per `docs/acp-probe-findings.md`, the probe account
   never reached a tool-using turn) — the live tool-using E2E (task 4 / AI-688 acceptance) is the
   actual validation gate per the design spec.
4. Did **not** touch `AgentOrchestrator`, `ServerConnection`, `HandleLaunchAgent`, or any
   aggregation/seq-assignment/forwarding logic — out of scope per the brief (tasks 2-4).

## Commit

`AI-688 Option B task 1: wire DTOs + per-update translation + Reduce() tool fields` — committed on
branch `ai-688-cursor-hosted-agent-prototype`, **not pushed**.
