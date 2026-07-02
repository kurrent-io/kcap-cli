# ACP probe findings (`cursor-agent acp`)

**Status:** de-risking spike (AI-684 Task 5), not TDD. This document is the source of truth for the exact wire shapes used by Tasks 6–10 of the ACP protocol foundation plan, and feeds AI-687/688/689.

## Environment

- `cursor-agent` version: `2026.07.01-41b2de7`
- `cursor-agent --version` → `2026.07.01-41b2de7`
- Probed on macOS (Darwin 25.5.0), via `which cursor-agent` → `/Users/tony/.local/bin/cursor-agent`
- Auth status at probe time: **already logged in** (`cursor-agent status` / `cursor-agent whoami` → `✓ Logged in as tony.young@kurrent.io`)
- No env vars were required beyond the ambient shell environment (no `CURSOR_API_KEY` set/needed; auth comes from `cursor-agent login`'s stored credentials).

## Launch command

```
cursor-agent acp
```

Confirmed via `cursor-agent acp --help`:

```
Usage: agent acp [options]

Start the Cursor Agent as an ACP (Agent Client Protocol) server

Options:
  -h, --help  Display help for command
```

- No required flags. No env vars required for launch (auth is read from the CLI's existing stored login, not passed explicitly).
- The subcommand is **hidden** (does not appear in `cursor-agent --help`'s command list) but is fully documented via `cursor-agent acp --help`.
- Speaks JSON-RPC 2.0, **newline-delimited**, one JSON object per line, over stdin/stdout. Process stderr was empty in both probe runs (no diagnostic noise on stderr for a clean session).
- Process was spawned with `stdio: ['pipe', 'pipe', 'pipe']` and no special env beyond inheriting the parent's environment.

## `initialize`

### Request sent

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": 1,
    "clientCapabilities": {
      "fs": { "readTextFile": false, "writeTextFile": false },
      "terminal": false
    }
  }
}
```

We deliberately advertised **minimal** client capabilities (no fs, no terminal) per the probe's safety constraints.

### Response received

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": 1,
    "agentCapabilities": {
      "loadSession": true,
      "mcpCapabilities": { "http": true, "sse": true },
      "promptCapabilities": { "audio": false, "embeddedContext": false, "image": true },
      "sessionCapabilities": { "list": {} }
    },
    "authMethods": [
      {
        "id": "cursor_login",
        "name": "Cursor Login",
        "description": "Authenticate using existing Cursor login credentials. Run 'agent login' first if not logged in."
      }
    ]
  }
}
```

Round-trip time: initialize responded in well under 1s (~700ms including process cold start).

### Key observations

- **`protocolVersion`**: agent echoes back `1` (integer), matching what we requested. Not negotiated to a different value in this exchange.
- **`agentCapabilities`**:
  - `loadSession: true` — agent supports resuming/loading a prior session (relevant to `session/load`, not exercised in this probe).
  - `mcpCapabilities: { http: true, sse: true }` — agent can itself connect to MCP servers over HTTP/SSE (we passed `mcpServers: []` in `session/new`, so this wasn't exercised).
  - `promptCapabilities: { audio: false, embeddedContext: false, image: true }` — agent accepts image content blocks in prompts but not audio or embedded-context blocks.
  - `sessionCapabilities: { list: {} }` — agent supports listing sessions.
- **Does the agent request/require client `fs` or `terminal` capabilities?** No — nothing in the `initialize` response demands client fs/terminal support. The response is purely the agent's own capabilities plus `authMethods`; it does not echo back or negatively acknowledge our (empty) client capabilities. Whether the agent *actually needs* fs/terminal only shows up later, if/when it tries to call `fs/read_text_file`, `fs/write_text_file`, or a `terminal/*` method against the client — see "Permission / elicitation / client-method requests" below. **In this probe, the agent never attempted any such call** (the turn ended immediately on the plan-limit response, before any tool use was attempted) — so the question "does Cursor make client fs/terminal requests for a plain-text prompt" remains formally unconfirmed by this probe and should be revisited once a paid/tool-capable model is used (see "Known limitation" below).
- **`authMethods`** lists exactly one method, `cursor_login`, confirming CLI-stored-login is the (only) supported auth path for the ACP server — there is no separate API-key-over-ACP flow surfaced here.

## `session/new`

### Request sent

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "session/new",
  "params": {
    "cwd": "/var/folders/d_/28hknwjx0vj_97mffp9h6mcc0000gn/T/acp-probe-session-GbEezW",
    "mcpServers": []
  }
}
```

`cwd` is an absolute path to a throwaway temp dir created via `mkdtemp` (never the repo). `mcpServers: []` disables MCP server wiring for the session.

### Response received (abbreviated — full model/config lists elided for length; see raw transcript excerpt below for the complete list)

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "sessionId": "fc2e09cf-f4b0-4463-9dc1-bda11268896b",
    "modes": {
      "currentModeId": "agent",
      "availableModes": [
        { "id": "agent", "name": "Agent", "description": "Full agent capabilities with tool access" },
        { "id": "plan", "name": "Plan", "description": "Read-only mode for planning and designing before implementation" },
        { "id": "ask", "name": "Ask", "description": "Q&A mode - no edits or command execution" }
      ]
    },
    "models": {
      "currentModelId": "composer-2.5[fast=true]",
      "availableModels": [
        { "modelId": "default[]", "name": "Auto" },
        { "modelId": "composer-2.5[fast=true]", "name": "composer-2.5" },
        { "modelId": "claude-opus-4-8[thinking=true,context=300k,effort=high,fast=false]", "name": "claude-opus-4-8" }
        /* ... 28 more entries, one per model exposed by `cursor-agent --list-models` ... */
      ]
    },
    "configOptions": [
      {
        "id": "mode",
        "name": "Mode",
        "description": "Controls how the agent executes tasks",
        "category": "mode",
        "type": "select",
        "currentValue": "agent",
        "options": [ /* same 3 entries as availableModes, shape { value, name, description } */ ]
      },
      {
        "id": "model",
        "name": "Model",
        "description": "Controls which model variant is used for responses",
        "category": "model",
        "type": "select",
        "currentValue": "composer-2.5[fast=true]",
        "options": [ /* same entries as availableModels, shape { value, name } */ ]
      }
    ]
  }
}
```

### Key observations

- `result.sessionId` is a UUID string (`fc2e09cf-f4b0-4463-9dc1-bda11268896b` in run 1, `dcce265b-f35c-44f0-828c-871560ec1c7b` in run 2).
- `result.modes` and `result.configOptions[id=mode]` carry duplicate information (three execution modes: `agent` / `plan` / `ask`); `configOptions` is the generic settings-surface representation, `modes` is the ACP-standard ClientCapabilities-adjacent shape.
- `result.models.currentModelId` defaults to `composer-2.5[fast=true]` — **this is significant**: this default model is plan-gated (see `session/prompt` below) and is NOT the same as whatever model the interactive `cursor-agent` REPL might default to.
- `configOptions` is a superset/generalization mechanism: `id: "mode"` and `id: "model"` are both exposed as generic `{id, name, description, category, type: "select", currentValue, options}` structures. This strongly suggests a `session/set_config_option` method exists to mutate these (see below).
- Round-trip time: ~1.8–1.9s (slower than `initialize`; likely a real network round-trip to Cursor's backend to fetch models/config).

## `session/set_config_option` (undocumented, discovered incidentally — NOT part of the required probe steps but recorded because it's a real wire shape)

While trying to work around the plan-limit response (see below), we attempted to switch models mid-session:

### Request sent

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "session/set_config_option",
  "params": {
    "sessionId": "dcce265b-f35c-44f0-828c-871560ec1c7b",
    "id": "model",
    "value": "claude-sonnet-4-5[thinking=true,context=200k]"
  }
}
```

### Response received (error)

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "error": {
    "code": -32603,
    "message": "Internal error",
    "data": [
      { "expected": "string", "code": "invalid_type", "path": ["configId"], "message": "Invalid input" }
    ]
  }
}
```

**Finding:** the method exists (it didn't come back as "method not found") and expects the field to be named `configId`, not `id` (our guess based on the `configOptions[].id` field name was wrong — the request param uses `configId`). We did not retry with the corrected field name since (a) this method is out of scope for AI-684 Task 5 and (b) our goal was just to probe capability/session/prompt/update/cancel shapes, not to fix model selection. **This is a candidate follow-up for whoever implements model selection later** — the correct request shape is presumably `{ sessionId, configId: "model", value: "<modelId>" }`.

## `session/prompt`

### Request sent

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "session/prompt",
  "params": {
    "sessionId": "fc2e09cf-f4b0-4463-9dc1-bda11268896b",
    "prompt": [
      { "type": "text", "text": "Reply with exactly the word: hello" }
    ]
  }
}
```

`prompt` is an array of content blocks; we sent a single `{ type: "text", text: "..." }` block (matches the `promptCapabilities.image: true` hint that this is a content-block array, not a bare string).

### `session/update` notification received (before the response)

```json
{
  "jsonrpc": "2.0",
  "method": "session/update",
  "params": {
    "sessionId": "fc2e09cf-f4b0-4463-9dc1-bda11268896b",
    "update": {
      "sessionUpdate": "agent_message_chunk",
      "content": { "type": "text", "text": "\n\nUpgrade your plan to continue" }
    }
  }
}
```

### Response received

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "result": { "stopReason": "end_turn" }
}
```

### Key observations (see "Known limitation: plan/billing gate" below for the important caveat)

- The response to `session/prompt` is `{ stopReason: "end_turn" }` — the ACP-spec-shaped turn-completion result. `stopReason` is a string enum; `end_turn` is the only value observed in this probe.
- The `session/update` notification for `agent_message_chunk` carries `update.content` as a content block (`{ type: "text", text: "..." }`), mirroring the prompt's own content-block-array shape.
- Notifications precede the matching response for the same logical turn (the `agent_message_chunk` update arrived before the `id:3` response), confirming updates and the request/response pair are correlated only by `sessionId`, not by request `id`.

### `available_commands_update` — the other `session/update` variant observed

Immediately after sending `session/prompt` (before the `agent_message_chunk` update above), the agent also emitted:

```json
{
  "jsonrpc": "2.0",
  "method": "session/update",
  "params": {
    "sessionId": "fc2e09cf-f4b0-4463-9dc1-bda11268896b",
    "update": {
      "sessionUpdate": "available_commands_update",
      "availableCommands": [
        { "name": "copy-request-id", "description": "Copy the last request ID to clipboard" },
        { "name": "multi-model-review", "description": "Pick models via ask_question (multi-select), then parallel Task reviewers (global)" },
        { "name": "simplify", "description": "Find low-info comments, one-off helpers, perf issues, and reuse opportunities. (global)" },
        { "name": "babysit", "description": "Keep a PR merge-ready by triaging comments, resolving clear conflicts, and fixing CI in a loop. (builtin skill)" }
        /* ... full list includes all built-in Cursor CLI skills plus every kcap-* user skill discovered on this machine (kcap-disable, kcap-errors, kcap-hide, kcap-recap, kcap-review-flows, kcap-validate-plan) ... */
      ]
    }
  }
}
```

Each entry in `availableCommands` is `{ name: string, description: string }`. This mirrors the local Cursor CLI's skills/commands surface (including this machine's project-level and user-level skills), exposed over ACP so a client could offer slash-command completion. **Note:** in run 2, this `available_commands_update` notification arrived *after* the turn had already ended (i.e., asynchronously relative to the prompt/response cycle) — its timing relative to the turn is not fixed.

### `sessionUpdate` variants observed (full enumeration for this probe)

Only **two** `sessionUpdate` discriminator values were observed across both runs:

1. `available_commands_update` — `{ sessionUpdate: "available_commands_update", availableCommands: [{name, description}, ...] }`
2. `agent_message_chunk` — `{ sessionUpdate: "agent_message_chunk", content: {type: "text", text: string} }`

**No other variants were observed** — specifically, none of `agent_thought_chunk`, `tool_call`, `tool_call_update`, or `plan` (all mentioned as spec candidates in the AI-684 plan) appeared in this probe. This is because the turn ended immediately with a plan/billing gate message before the agent attempted any tool use or extended reasoning (see below). **Tasks 6–10 should treat the plan's mentioned variants (`agent_thought_chunk`, `tool_call`, `tool_call_update`, `plan`) as spec-documented but NOT yet empirically confirmed against this specific `cursor-agent` build** — confirming them requires a prompt that completes on a non-gated model and actually exercises a tool call, which this probe's account/plan could not do (see "Known limitation" and "Recommended follow-up" below).

## Known limitation: plan/billing gate (not an auth failure)

**The default model (`composer-2.5[fast=true]`) is gated by a Cursor plan/billing restriction on this account**, not by missing authentication. Every `session/prompt` sent against the default session model — including a second attempt after changing the prompt to explicitly request a shell command (`ls`) — returned the same immediate response:

```
agent_message_chunk: "\n\nUpgrade your plan to continue"
```

followed immediately by `{ stopReason: "end_turn" }`. This happened **before any tool call, thinking chunk, or plan update was ever emitted** — i.e., the gate fires before the agent does any work, not mid-turn. This was reproduced identically across 2 independent runs (fresh session, fresh temp cwd each time), so it is a consistent finding, not a fluke.

- **Auth status: the account IS authenticated** (`cursor-agent whoami` confirms `tony.young@kurrent.io`, and ACP `initialize`/`session/new` succeed normally, including the backend round-trip to fetch the model catalog). The gate is specifically a plan/entitlement restriction on generating a completion with the current default model — not a credential problem, and not something this probe should (or was authorized to) work around by logging in differently or supplying different credentials.
- An attempt to route around this by switching models via `session/set_config_option` failed with a wire-shape mismatch (see above), not a plan/billing error — we did not pursue a corrected retry since fixing model selection is out of scope for this spike.
- **Practical implication for AI-684 Tasks 6–10:** the `initialize` → `session/new` → `session/prompt` → `session/update`(s) → response → `session/cancel` sequence and JSON shapes captured here are real and load-bearing. The **content** of a fully-completed, tool-using turn (i.e., `tool_call`/`tool_call_update`/`plan`/`agent_thought_chunk` payload shapes, and whether/how `session/request_permission` or `fs/*`/`terminal/*` client-method calls are actually issued) could **not** be captured in this probe because no prompt reached that stage on this account/model. The fake agent (Task 8) should synthesize plausible payloads for those variants based on the published ACP spec, clearly marked as spec-derived-but-unverified-by-probe, rather than probe-derived.

## Permission / elicitation requests (`session/request_permission`, `fs/*`, `terminal/*`)

**Not observed in this probe.** No `session/request_permission` request, no `fs/read_text_file`/`fs/write_text_file` request, and no `terminal/*` request was sent by the agent in either run. This is consistent with the turn ending immediately on the plan-limit message before any tool use was attempted — see "Known limitation" above.

The probe driver was fully instrumented to detect and safely deny any such request had one arrived (see "Driver safety behavior" below), so this is a negative result from real execution, not an untested code path. **This must be re-probed** (ideally by whoever has access to a non-plan-gated model on this or another Cursor account) before AI-685/686 finalize the permission-bridge implementation, since the exact `session/request_permission` request/response shape (options, selected-option echo, `outcome` field name/values) is currently only assumed from the general ACP spec, not confirmed against `cursor-agent`.

### Driver safety behavior (for the record, in case a permission/fs/terminal request had arrived)

The probe script's `handleServerRequest` function was wired to:
- `session/request_permission` → respond `{ outcome: { outcome: "cancelled" } }` (deny) and record the exact request JSON.
- `fs/read_text_file` / `fs/write_text_file` → respond with a JSON-RPC error `{ code: -32601, message: "Method not supported by client (denied by probe): <method>" }`.
- any `terminal/*` method → same denial as fs.
- any other unrecognized server→client request → same denial pattern.

No file write, shell command, or permission grant was ever issued to the agent process from the client side.

## `session/cancel`

### Request sent (as a notification, per ACP shape — no response expected)

```json
{
  "jsonrpc": "2.0",
  "method": "session/cancel",
  "params": { "sessionId": "fc2e09cf-f4b0-4463-9dc1-bda11268896b" }
}
```

### Observations

- `session/cancel` is a **notification** (no `id`, no response expected/received), consistent with typical ACP fire-and-forget cancellation semantics.
- In both runs, `session/cancel` was sent *after* the turn had already completed naturally (`stopReason: "end_turn"` had already been returned), since the plan-limit gate ended the turn immediately. So this probe does **not** demonstrate mid-turn cancellation behavior (e.g., whether an in-flight `tool_call` gets aborted, whether a distinct `session/update` or error is emitted on cancel-during-active-turn). That remains unconfirmed and should be re-probed against a real, longer-running turn.
- The child process was killed (`SIGKILL`) shortly after `session/cancel` was sent, by the driver's own cleanup — we did not observe any response/notification the agent might have emitted in reaction to the cancel notification before teardown.

## Process lifecycle

- The `cursor-agent acp` child process stayed alive throughout each run and emitted no stderr output in either run.
- The driver terminated the child with `SIGKILL` after finishing its scripted sequence (each run completed in under 3 seconds of wall-clock time against the agent, well within the driver's 60–90s hard timeout).

## Summary table: what's confirmed vs. still open

| Item | Status |
|---|---|
| Launch argv (`cursor-agent acp`) | ✅ Confirmed |
| JSON-RPC 2.0, newline-delimited framing | ✅ Confirmed |
| `initialize` request/response shape | ✅ Confirmed (real request/response captured above) |
| `agentCapabilities` shape (`loadSession`, `mcpCapabilities`, `promptCapabilities`, `sessionCapabilities`) | ✅ Confirmed |
| Whether agent declares it *needs* client fs/terminal in `initialize` | ✅ Confirmed it does NOT declare this in the `initialize` response itself |
| Whether agent *calls* `fs/*`/`terminal/*` against the client for a real turn | ❌ Not observed — no tool-using turn was reached |
| `session/new` request/response shape | ✅ Confirmed |
| `session/prompt` request/response shape (`stopReason: "end_turn"`) | ✅ Confirmed for the immediate-plan-gate case only |
| `session/update` — `available_commands_update` | ✅ Confirmed, full payload captured |
| `session/update` — `agent_message_chunk` | ✅ Confirmed, full payload captured |
| `session/update` — `agent_thought_chunk` | ❌ Not observed |
| `session/update` — `tool_call` / `tool_call_update` | ❌ Not observed |
| `session/update` — `plan` | ❌ Not observed |
| `session/request_permission` shape | ❌ Not observed (driver ready to deny it if it had arrived) |
| `session/cancel` shape | ✅ Confirmed as a notification; mid-turn cancel behavior not exercised |
| `session/set_config_option` (bonus finding) | ⚠️ Partially confirmed — method exists, our param name (`id`) was wrong, correct field is likely `configId` |
| Auth | ✅ Already authenticated (`tony.young@kurrent.io`); no login flow exercised |
| Real prompt completion (actual LLM answer, not plan-gate message) | ❌ Did not complete — blocked by a plan/billing restriction on the default model, reproduced twice |

## Recommended follow-up (out of scope for this spike)

1. Re-run this probe (or extend it) once a non-plan-gated model/account is available, to capture: a completed real answer, `tool_call`/`tool_call_update`/`plan`/`agent_thought_chunk` payload shapes, and whether/how `session/request_permission` is actually triggered for a tool call under minimal client capabilities.
2. Confirm the correct `session/set_config_option` request shape (`configId` vs `id`) if programmatic model switching becomes relevant to the ACP runtime.
3. Probe mid-turn `session/cancel` behavior against a long-running turn (what update/response, if any, follows cancellation of an active turn).
