# AI-688 Cursor hosted-agent prototype — probe findings

Extends [`acp-probe-findings.md`](acp-probe-findings.md) (AI-684) with the AI-688-specific facts. Source
of truth for the Cursor CLI version, command, capability response, and protocol deviations
(AI-688 acceptance: "Record the exact Cursor CLI version, command, capability response, and any
protocol deviations").

## Environment (2026-07-07)

- `cursor-agent` version: **`2026.07.01-41b2de7`** (same build as the AI-684 probe).
- Launch command: **`cursor-agent acp`** (hidden subcommand; newline-delimited JSON-RPC 2.0 over stdio; clean stderr).
- Auth: `cursor-agent status` → `✓ Logged in as tony.young@kurrent.io`.
- **Subscription tier matters:** `cursor-agent about` → `Subscription Tier`.
  - On **Free**: EVERY model (default `composer-2.5[fast=true]`, `claude-sonnet-4-5`, …) returns a single
    `agent_message_chunk` = `"Upgrade your plan to continue"` then `stopReason: end_turn`. No real turn runs.
    The plan-limit is **account-level, not model-level** — model selection does NOT unblock it.
  - On **Team**: real turns run (confirmed below). **A paid tier is a hard prerequisite for any live E2E**
    that does actual agent work / tool use / permission requests.

## `initialize` (unchanged from AI-684, re-confirmed live)

Response: `protocolVersion: 1`, `agentCapabilities` = `{loadSession:true, mcpCapabilities:{http:true,sse:true},
promptCapabilities:{audio:false, embeddedContext:false, image:true}, sessionCapabilities:{list:{}}}`,
`authMethods: [{id:"cursor_login", ...}]`. The agent does NOT demand client `fs`/`terminal` capabilities at
initialize; whether it needs them only shows up if it calls `fs/*` or `terminal/*` mid-turn.

## `session/set_config_option` — model selection (gap 1) — CONFIRMED

Wire shape (the AI-684 doc listed this as "presumed-correct, never implemented" — now verified working):

```json
{"jsonrpc":"2.0","id":3,"method":"session/set_config_option",
 "params":{"sessionId":"<sid>","configId":"model","value":"claude-sonnet-4-5[thinking=true,context=200k]"}}
```

- **`configId`** is the correct field (NOT `id` — an earlier attempt with `id` got a Zod `invalid_type` error at path `configId`).
- **`value` must be the exact `modelId`** from `session/new`'s `result.models.availableModels[].modelId`
  (parameterized, e.g. `claude-sonnet-4-5[thinking=true,context=200k]`) — a bare family name is not the wire value.
- Response echoes the full `configOptions` array with `id:"model"` `currentValue` = the newly-selected modelId
  (confirms the switch took effect). Also `id:"mode"` (agent/plan/ask) is a sibling config option.
- Available models on this Team account (32): `default[]`, `composer-2.5[fast=true]`, `claude-opus-4-8[...]`,
  `claude-sonnet-5[...]`, `claude-sonnet-4-6[...]`, `claude-sonnet-4-5[thinking=true,context=200k]`,
  `claude-sonnet-4[...]`, `claude-opus-4-*`, `gpt-5.*`, `gemini-3*`, `grok-*`, `kimi-*`, `glm-*`, etc.

## Real turn (Team tier) — `session/prompt` "Respond with only HELLO"

`session/update` notifications observed (24 total), in order:
1. `available_commands_update` (slash commands: copy-request-id, multi-model-review, …)
2. `session_info_update` `{title:"Hello Only"}` (agent auto-titles the session)
3. `agent_thought_chunk` × many — streamed model reasoning (`{content:{type:"text", text:"..."}}`)
4. `agent_message_chunk` `{content:{type:"text", text:"HELLO"}}` — the actual answer (streamed in chunks)
5. `session/prompt` response: `{result:{stopReason:"end_turn"}}`

**These are the Option-B mapping inputs.** Chunk shape is uniform: `update.sessionUpdate` discriminator +
`update.content:{type,text}` for message/thought chunks. (tool_call / tool_call_update / plan updates were NOT
exercised by this no-tool prompt — needs a tool-using task to capture; the daemon's `AcpSessionUpdate` reducer
already enumerates those kinds.)

## Deviations / notes

- `agent_thought_chunk` (reasoning) is a distinct kind from `agent_message_chunk` (final answer) — Option B
  must decide whether to surface thoughts (probably as a separate/collapsed canonical block).
- Client-side `fs`/`terminal` requests: still NOT observed (the HELLO prompt used no tools). Needs a
  file-editing / shell task to confirm whether Cursor calls `fs/*`/`terminal/*` against the client — feeds AI-687.
- Probe artifact: killing the ACP process immediately after `end_turn` yields a trailing
  `agent_message_chunk` `"Error: RetriableError: WritableIterable is closed"`. The real daemon keeps the
  connection open, so this won't occur in production.

## Tool-using turn (AI-688 task 5) — 2026-07-09

De-risks the previously-unconfirmed `tool_call`/`tool_call_update`/`agent_thought_chunk` shapes and the
AI-686 permission path against a REAL tool-using turn. Probe: newline-delimited JSON-RPC over stdio
(python harness, extending the gap-1 pattern above), minimal `clientCapabilities` (`fs: {read:false,
write:false}`, `terminal: false` — same as `AcpHostedAgentRuntime.StartAsync`'s real `InitializeParams`),
model forced to `claude-sonnet-4-5[thinking=true,context=200k]` via `session/set_config_option`
(same wire shape as gap 1, re-confirmed), prompt: *"Use your shell/command tool to run exactly `echo
kcap-e2e-marker` and report the output."* Session title auto-generated: `"Shell Reporter"`. 43
`session/update` notifications total: `available_commands_update`×1, `session_info_update`×1 (a THIRD
`sessionUpdate` variant, not previously observed and not in the daemon's `Reduce()` switch — see below),
`agent_thought_chunk`×30, `agent_message_chunk`×8, `tool_call`×1, `tool_call_update`×2.

### `tool_call` — CONFIRMED, matches task 1's assumption exactly

```json
{"sessionUpdate":"tool_call","toolCallId":"toolu_bdrk_01WHvLzppLFXQQdgguTnpoVs","title":"`echo kcap-e2e-marker`","kind":"execute","status":"pending","rawInput":{"command":"echo kcap-e2e-marker"}}
```

- Tool INPUT args are under **`rawInput`** (a structured object, `{"command": "..."}`) — exactly the field
  `AcpSessionUpdate.Reduce()` already reads via `GetRawTextOrNull(update, "rawInput")`.
- `title` is a human-readable, backtick-wrapped rendering of the command (`` `echo kcap-e2e-marker` ``), not
  a machine tool name — confirms the existing doc comment on `AcpEventTranslator.Translate`'s `ToolCall`
  case ("ACP's `tool_call` has no separate machine 'name' field, only `title`/`kind`").
- `kind: "execute"` (shell/command tool) and `status: "pending"` (initial, non-terminal) both match the
  enumerated assumptions.

### `tool_call_update` — CONFIRMED (mid-flight status-only, then terminal with `rawOutput`)

Mid-flight, non-terminal, no result content (correctly dropped — `IsTerminalToolStatus("in_progress")` is
`false`, so `AcpEventTranslator.Translate` returns `null`, no `ToolResult` envelope emitted):

```json
{"sessionUpdate":"tool_call_update","toolCallId":"toolu_bdrk_01WHvLzppLFXQQdgguTnpoVs","status":"in_progress"}
```

Terminal (the ONLY terminal status observed in this probe — `failed` was not exercised):

```json
{"sessionUpdate":"tool_call_update","toolCallId":"toolu_bdrk_01WHvLzppLFXQQdgguTnpoVs","status":"completed","rawOutput":{"exitCode":0,"stdout":"kcap-e2e-marker\n","stderr":""}}
```

- The result is under **`rawOutput`**, a STRUCTURED object (`{exitCode, stdout, stderr}`), NOT the
  ACP-spec `content` array of text blocks task 1's `ExtractToolResultText` tries FIRST. This is exactly
  the fallback path that method's doc comment already anticipated ("Falls back to the verbatim `rawOutput`
  JSON text when no text content block is present") — `ExtractToolResultText` returns the raw JSON text of
  the whole `rawOutput` object (`{"exitCode":0,"stdout":"kcap-e2e-marker\n","stderr":""}`), which becomes
  `ToolResultText`/`AcpEventEnvelope.ToolResult` verbatim (including `exitCode`, not just `stdout`).
  **This mechanically works — terminal status + non-null `ToolResultText` → a `ToolResult` envelope IS
  emitted — but the displayed text is the whole raw-output JSON blob, not clean stdout.** Judged NOT a
  deviation requiring a fix (the fallback was designed for exactly this shape, and the task 5 brief's HARD
  RULE is "do NOT redesign" — see "Step 2 conclusion" below for the explicit decision and a flagged
  future-polish idea).
- `status: "completed"` is the only terminal value observed; `"failed"` remains spec-assumed/unconfirmed.

### `agent_thought_chunk` — CONFIRMED, matches `content.text` exactly

```json
{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"The user wants me to run a specific shell command:"}}
```

Streamed across 30 chunks for this turn (reasoning before AND after the tool call — Cursor keeps
reasoning through the tool-call/result cycle, not just once at the start). `ExtractContentText`'s
`content.text` read matches every chunk observed.

### `session/request_permission` — FIRED, and matches AI-686's spec-derived shape exactly

The agent DID request permission before running `echo` (not on any allowlist):

```json
{"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"103cecd2-c849-4b6e-885b-fa4aef569253","toolCall":{"toolCallId":"toolu_bdrk_01WHvLzppLFXQQdgguTnpoVs","title":"`echo kcap-e2e-marker`","kind":"execute","status":"pending","content":[{"type":"content","content":{"type":"text","text":"Not in allowlist: echo"}}]},"options":[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"allow-always","name":"Allow always","kind":"allow_always"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]}}
```

- **This is a REQUEST, not a notification — it carries an `id` (here `0`, a valid JSON-RPC id; confirmed
  `AcpConnection.DispatchLineAsync`'s `TryGetProperty("id", ...)` presence-check handles `id: 0` correctly,
  not falsy-checked).**
- `params.sessionId`/`params.toolCall`/`params.options[].{optionId,name,kind}` — **matches
  `SessionRequestPermissionParams`/`PermissionOptionDto` field-for-field**, including the extra
  `toolCall.content` annotation (`"Not in allowlist: echo"`) that stays opaque inside the `JsonElement
  ToolCall` the daemon never inspects beyond `title`/`toolCallId` (`AcpInteractionBridge.TryGetToolTitle`/
  `TryGetToolCallId`).
- We answered `{"outcome":{"outcome":"selected","optionId":"allow-once"}}` — **matches
  `PermissionOutcomeResult`/`PermissionOutcomeDto` exactly** — and the turn proceeded to run the command
  and report its output. This CONFIRMS (against a real agent, not just `FakeAcpAgent`) that AI-686's
  `AcpInteractionBridge` request/response shapes are correct as already implemented — no changes needed.
- **AI-686 permission path: FIRED, and the auto-approve round-trip worked end-to-end.**

### `fs/*` / `terminal/*` client-method calls — NOT observed (AI-687 signal)

Across the whole tool-using turn, exactly ONE agent→client request arrived: the `session/request_permission`
above. **No `fs/read_text_file`, `fs/write_text_file`, or `terminal/*` request was made** — the shell
command ran and returned `{exitCode, stdout, stderr}` via `rawOutput` without ever calling back into the
client for filesystem or terminal access. **Signal for AI-687: at least for a plain shell/`execute`-kind
tool call, Cursor executes the command itself (server/agent-side) and does not need the client to expose
`fs`/`terminal` capabilities at all** — consistent with `initialize`'s response never demanding them. This
probe only exercised `kind: "execute"`; whether a file-EDIT tool (`kind: "edit"`/`"read"`) calls `fs/*`
against the client instead of executing server-side remains unconfirmed and would need a file-editing
prompt to check — out of scope for this task's single probe.

### New `sessionUpdate` variant observed: `session_info_update`

```json
{"sessionUpdate":"session_info_update","title":"Shell Reporter"}
```

A third `sessionUpdate` kind (session auto-titling, same as `session_info_update` mentioned informally in
this doc's "Real turn (Team tier)" section above but not previously captured verbatim). Not in
`AcpSessionUpdate.Reduce()`'s switch — falls through to `AcpUpdateKind.Unknown`, which is exactly the
designed safety net (never throws; `AcpEventTranslator.Translate` logs it at debug level and returns
`null`, no envelope). Confirmed safe, no fix needed.

### Step 2 conclusion: NO translation changes needed

Every observed real shape matches an assumption `AcpSessionUpdate.Reduce()`/`AcpEventTranslator` already
encode — including the FALLBACK paths (`rawOutput` for tool results, `Unknown` for `session_info_update`).
**No changes made to `Reduce()` or `AcpEventTranslator` for this task.** One polish idea is flagged for a
future task (not this one, per the "do NOT redesign" hard rule): `ExtractToolResultText`'s `rawOutput`
fallback currently surfaces the WHOLE raw-output JSON object (`{exitCode,stdout,stderr}`) as
`ToolResultText` rather than just `stdout`/`stderr` text — functionally correct (non-null, terminal-gated),
but a rendered transcript would show a JSON blob instead of clean command output for an `execute`-kind
tool. Left unchanged here since it already matches the shape task 1 anticipated and isn't a broken/crashing
path.
