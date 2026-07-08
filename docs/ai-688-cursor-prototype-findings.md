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
