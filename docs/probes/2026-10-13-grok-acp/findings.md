# Grok Build ACP hosting probe — 2026-10-13

**Subject:** `grok 1.0.52 (0920c8169e27) [stable]`, macOS / arm64, run as `grok agent --no-leader stdio` in an
empty, non-git directory. The user's `~/.grok/config.toml` sets `permission_mode = "always-approve"`.
**Drivers:** `probe_turn_and_load.py`, `probe_permission.py`, `probe_mcp_and_model.py` (each takes a working
directory and an output directory). Each spends one or two short model turns.

Every claim below was measured at effect level, not read off an advertisement.

## 1. Surface

`initialize` advertises `loadSession`, `mcpCapabilities {http, sse}`, and `sessionCapabilities {list, resume, close}`.
`session/new` answers `{sessionId, models, configOptions, _meta}`. `configOptions` holds `model` and
`reasoning_effort`, both `select`s.

## 2. Permissions

- With the user's always-approve config and no `_meta` on `session/new`, a file write ran **without** any
  `session/request_permission`. Grok only raised `_x.ai/session_notification` `pending_interaction` and
  `interaction_resolved` around it.
- With `session/new` `_meta: {"yoloMode": false}`, the same write raised a standard `session/request_permission`.
  The `toolCall` carries `kind: "edit"`, and the options are `allow-edits-session` (`allow_always`), `allow-once`
  (`allow_once`) and `reject-once` (`reject_once`).
- Answering `reject-once` failed the tool call, and the turn ended with `stopReason: "cancelled"`.

So a hosted launch must send `yoloMode: false`. Otherwise the user's config silently takes the decision away from
the daemon.

## 3. MCP servers and model selection

- A stdio server passed in `session/new` `mcpServers` was reachable. The model called it through Grok's
  `use_tool` as `kcap-probe__kcap_probe_echo`, and the nonce it returned reached the reply.
- `session/set_config_option {configId: "model", value: "grok-4.6"}` took effect: the next turn's usage was
  keyed `grok-4.6-build`. `ConfigOptionModelSelector` fits.

## 4. `session/load`

- After the owning process was SIGKILLed while idle, a fresh process's `session/load` replayed the history
  (`user_message_chunk`, `agent_thought_chunk`, `tool_call`, `agent_message_chunk`, then
  `_x.ai/session/update` `turn_completed`) **before** its response, with the same `toolCallId` as the live turn.
  A prompt on the loaded session completed normally.
- The reconnect probe (`2026-08-04-acp-reconnect-c0/acp_c0_probe.py`, with a `grok` row added) did not pass:
  its turns saw no live updates and its `session/load` hit EOF. That probe is the gate for
  `SupportsReconnectResume`, so it stays off.

## 5. Other wire facts

- Live text streams as many small `agent_message_chunk`s. The on-disk `updates.jsonl` keeps one line per text run.
- Live turn usage arrives as `_x.ai/session_notification` `turn_completed`; a replay carries it as
  `_x.ai/session/update` `turn_completed`. The daemon reads the live frame (`GrokTurnUsage`), one usage envelope
  per model, and ignores the replay.
- Grok runs Claude Code's and Cursor's MCP servers and hooks by default (`[compat.claude]` / `[compat.cursor]`,
  or `GROK_CLAUDE_MCPS_ENABLED` and its siblings), including kcap's own `~/.grok/hooks/kcap.json`. There is no
  per-process switch for `~/.grok/hooks`, so the daemon sets `KCAP_HOSTED_GROK` to its own pid on a hosted
  child, and `kcap hook --grok` stands down only when the Grok running it is that daemon's direct child.
