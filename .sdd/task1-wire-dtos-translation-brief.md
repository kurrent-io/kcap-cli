# AI-688 Option B — Task 1: wire DTOs + per-update translation (implementer brief)

The pure, foundational layer for Option B. **Only** the DTOs + translation + `Reduce()` extension — NO aggregation,
NO `ServerConnection`/forwarding, NO orchestrator wiring (those are tasks 2–4; do not touch them).

Work ONLY in this worktree: `/Users/tony/Documents/kcap-cli-wt/ai-688-cursor-hosted-agent-prototype`, branch
`ai-688-cursor-hosted-agent-prototype`. This is kcap-cli (the daemon). Commit; **do NOT push**; do NOT delegate to
any subagent/Agent tool. Spec: `docs/ai688-option-b-canonical-surfacing-design.md` (§2.2 mapping, §2.4 wire DTOs).

## A. Daemon-local wire DTOs (field-for-field mirror of the server contract)

The server side already defines the wire contract. **Read it and mirror it EXACTLY** — these DTOs are serialized
daemon-side and deserialized server-side over SignalR, so any field-name/type/casing mismatch silently breaks the
wire. Server contract to mirror (read-only reference; do NOT edit the server):
`/Users/tony/Documents/kcap-server-wt/ai-686-acp-permission-bridge/src/Capacitor.Server.Core/Acp/AcpEventEnvelope.cs`
(+ `AcpEventKind`, and wherever `AcpBatchAck` is defined — it's the return of `CapacitorHub.AcpSessionEvents`; grep
the server worktree). Mirror `AcpEventEnvelope`, `AcpEventKind`, and `AcpBatchAck` (incl. `AcceptedSeq`,
`ExpectedNextSeq`) as daemon-local records, **matching every `[JsonPropertyName]` / enum wire value / nullability
exactly**. Place them with the existing daemon↔server mirror DTOs in `src/Capacitor.Cli.Core/Models.cs` (alongside
`AcpInteractionRequest`/`AcpInteractionDecision`) and **register each in `CapacitorJsonContext`** (source-gen; the
same pattern those use — NativeAOT-safe, no reflection serialization). If the server `AcpEventEnvelope` references
nested types (e.g. a tool-result shape), mirror those too.

## B. Extend `AcpSessionUpdate` + `Reduce()` to capture tool args/result

`src/Capacitor.Cli.Daemon/Acp/AcpSessionUpdate.cs` currently captures only id/title/kind/status for tool updates.
The translation (C) needs the tool INPUT args (for `ToolCall` → `ToolInputJson`) and any result payload (for
`ToolResult`). Extend the `AcpSessionUpdate` record + `AcpHostedAgentRuntime.Reduce()` to pull those from the
update's `Raw` JsonElement (add fields e.g. `ToolInputJson`/`ToolResultText`+`IsError` — name them to match what
the translation emits). Keep `Raw` populated for every kind. Do not change the existing kinds' current behavior
beyond adding the new captured fields.

## C. Pure `AcpEventTranslator` (single update → envelope)

A pure, static, fully-unit-testable translator (place in `src/Capacitor.Cli.Daemon/Acp/`, it references both
`AcpSessionUpdate` (Daemon) and `AcpEventEnvelope` (Core)). Given ONE `AcpSessionUpdate` + a caller-supplied `seq`
(long) + `timestampIso` (string) + (for message/thought) an optional aggregated-text override, return
`AcpEventEnvelope?` per §2.2:
- `AgentMessageChunk` → `AssistantText` envelope (text = the aggregated-text override if provided, else the
  update's own text). `AgentThoughtChunk` → `AssistantThinking` envelope, same text rule.
- `ToolCall` → `ToolCall` envelope carrying `ToolCallId` + tool name/title + `ToolInputJson` (from B).
- `ToolCallUpdate` → `ToolResult` envelope **ONLY** if the update is terminal/completed AND has extractable result
  content; a **status-only** update (no result content) → return **null** (no empty `ToolResultReceived`).
- `Plan`, `AvailableCommands`, `Unknown` → return **null** (dropped). For `Unknown`, log the `Raw` at debug/info
  (never silently swallow).
- `ContractVersion` = 1, `TimestampIso` = the param, `Seq` = the param on every emitted envelope.
Also provide pure builder methods for the two synthesized lifecycle envelopes the later tasks need (they are NOT
derived from an `AcpSessionUpdate`): `BuildSessionStarted(seq, timestampIso, ...cwd/model/acpSessionId as needed to
match the AcpEventKind.SessionStarted envelope shape)` and `BuildUserMessage(seq, timestampIso, text)`. Do NOT build
`SessionEnded` (the server's `EndAgentSession` owns it — see spec §2.3). Seq assignment strategy, aggregation, and
the actual sending are tasks 2–4 — the translator only maps/builds given the inputs.

## TDD (test/Capacitor.Cli.Tests.Unit/)

Failing-first, genuine red→green for the core cases. Cover: each kind → expected envelope kind + fields;
message/thought use the aggregated-text override when given; `ToolCall` carries `ToolInputJson` from `Raw`;
status-only `ToolCallUpdate` → null; terminal `ToolCallUpdate` with content → `ToolResult` (+ `IsError`); plan/
available_commands/unknown → null; `BuildSessionStarted`/`BuildUserMessage` shapes; ContractVersion/Seq/TimestampIso
stamped. Add a focused test that a daemon-local DTO round-trips through `CapacitorJsonContext` to the SAME wire JSON
the server contract expects (serialize a daemon `AcpEventEnvelope`, assert the JSON property names match the server's
`[JsonPropertyName]`s) — this is the wire-compat guard. Mirror `Reduce()` tests for the new captured fields.

## Definition of done

- `dotnet build` clean (daemon + core). AOT gate empty:
  `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.
- `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (report pass/total; note any
  pre-existing flaky failures like the known `HttpClientExtensionsRetryTests` retry-timeout test).
- Genuine red→green confirmed for at least the translator's core mapping + the wire-compat round-trip test.

## Report contract

Write detail to `.sdd/task1-wire-dtos-translation-report.md`. Return only STATUS, commit sha, one-line test summary
(files + command + pass count), AOT-gate result, and concerns (esp. any field where the server contract was
ambiguous to mirror). Commit as `AI-688 Option B task 1: wire DTOs + per-update translation + Reduce() tool fields`.

## HARD RULES
- Read/Edit/Bash yourself only; NO Agent/Task delegation; NO git push.
- Worktree-only (daemon). Read the server contract read-only from the ai-686 server worktree; never edit it.
- AOT/trim-safe: source-gen JSON only, no reflection `Serialize<T>`, no `[JsonDerivedType]`.
- `await` every TUnit assertion. Do NOT implement aggregation / ServerConnection / orchestrator wiring (tasks 2–4).
