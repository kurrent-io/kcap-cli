# ACP restart resume: session/load after a graceful stop (2026-10-19)

Gate for `AcpVendorDescriptor.SupportsRestartResume`. The reconnect probe
(`docs/probes/2026-08-04-acp-reconnect-c0/`) measured `session/load` after a SIGKILL; a daemon restart
stops an agent the other way, so each installed vendor is re-measured with the stop the daemon
actually performs. Design: `docs/superpowers/specs/2026-10-17-daemon-restart-agent-resume-design.md`
§4.4.

## Method

`acp_restart_probe.py --vendor V --stop idle|midturn --kill daemon|sigterm` (reuses the c0 probe's
`AcpClient`; the vendor runs in its own process group):

1. `initialize`, `session/new {cwd, mcpServers: []}` in a fresh git repo.
2. Turn A plants a random codeword ("Remember this codeword for later: PELICAN-xxxxxx"); turn C runs a
   shell tool.
3. `--stop midturn` starts a `sleep 20` shell turn and stops 3 s after its `tool_call`;
   `--stop idle` stops between turns.
4. The stop, as `AgentOrchestrator.PrepareForResumeAsync` and
   `AcpHostedAgentRuntime.RequestGracefulStopAsync` do it: `session/cancel` when a turn is in flight,
   stdin EOF, up to 5 s for the exit, then SIGKILL to the whole group (`--kill daemon`).
   `--kill sigterm` puts a SIGTERM and 2 s before the kill, the variant the issue names; the daemon
   itself never sends SIGTERM (`ProcessTree.Kill` is SIGKILL only).
5. A fresh process: `initialize`, `session/load {sessionId, cwd, mcpServers: []}`; every update before
   the load response counts as replay, conversation updates in a 3 s window after it count as barrier
   violations. The reader switches to the after-response window the moment it records the response,
   so an update dispatched before the caller resumes is not counted as replay. A refused load is
   retried once after 15 s.
6. One prompt on the loaded session asks for the codeword. A load that answers but restores nothing
   fails here.

The committed `*-summary.json` files are the per-run results; frames and stderr stayed in the
originating session's scratchpad.

## Results

| Vendor (version) | Stop | Exit on | Load | Replayed updates | Updates after response | Codeword recalled |
|---|---|---|---|---|---|---|
| Copilot (1.0.98) | idle / daemon | stdin EOF, 0.76 s, exit 0 | ok | 7 | 0 | yes |
| | idle / sigterm | stdin EOF, 0.58 s | ok | 7 | 0 | yes |
| | mid-turn / daemon | stdin EOF, 0.97 s | ok | 9 | 0 | yes |
| | mid-turn / sigterm | stdin EOF, 1.30 s | ok | 9 | 0 | yes |
| Cursor (2026.09.15-d2fe57e) | idle / daemon | **SIGKILL** after 5 s | ok | 17 | 0 | yes |
| | idle / sigterm | SIGTERM (143), group outlived the leader | ok | 17 | 0 | yes |
| | mid-turn / daemon | **SIGKILL** after 5 s | ok | 19 | 0 | yes |
| | mid-turn / sigterm | SIGTERM (143), group outlived the leader | ok | 19 | 0 | yes |
| OpenCode (1.18.31, `OPENCODE_PURE=1`) | idle / daemon | stdin EOF, 0.30 s, exit 0 | ok | 9 | 0 | yes |
| | idle / sigterm | stdin EOF, 0.25 s | ok | 9 | 0 | yes |
| | mid-turn / daemon | stdin EOF, 0.27 s | ok | 13 | 0 | yes |
| | mid-turn / sigterm | stdin EOF, 0.27 s | ok | 10 | 0 | yes |
| Gemini (0.60.0) | all four | — | not reached | — | — | — |
| Grok | — | not probed | | | | |
| Kiro | — | not probed | | | | |

The only update after any load response was `available_commands_update`, which is not conversation.

### Per vendor

- **Copilot** exits on stdin EOF in under 1.5 s, mid-turn included, so it saves its session itself.
  Passes.
- **Cursor** ignores stdin EOF: it was still alive at 5 s in every run, so under the daemon's stop it
  is SIGKILLed, the same death the reconnect probe measured, and `session/load` works across it as it
  did then. A SIGTERM ends the leader but leaves children in its group, which the daemon's tree kill
  reaps. Passes. Unrelated to resume: Cursor appends `\n\nError: RetriableError: WritableIterable is
  closed` to the first turn of every process, fresh or loaded.
- **OpenCode** exits on stdin EOF in under 0.5 s. Never probed before; passes.
- **Gemini** stays on kcap continue. `session/new` never succeeded on this machine: with
  `GOOGLE_CLOUD_PROJECT` set the account has no Code Assist licence, and without it Google answers
  "This client is no longer supported for Gemini Code Assist for individuals". Whether a clean exit
  persists the session (it did not after a SIGKILL) is still unmeasured.
- **Grok** stays on kcap continue. The `grok` on this machine is an unrelated npm package
  (`@vibe-kit/grok-cli` 1.0.1, which rejects `agent --no-leader stdio`); xAI's CLI is not installed.
  Its own probe (`docs/probes/2026-10-13-grok-acp/`) saw `session/load` work after a SIGKILL while
  idle, but that is not the gate.
- **Kiro** stays on kcap continue. `kiro-cli` is not installed here, so whether its stale-owner lock
  clears after a clean exit is still unmeasured.

## Conclusions

1. `SupportsRestartResume` is true for Cursor, Copilot and OpenCode, false for Gemini, Grok and Kiro.
2. For every passing vendor the `session/load` response is a closed replay barrier, so suppressing
   notifications until the response drops exactly the replay.
3. A loaded session restores the conversation, not just an id: each one recalled a codeword it was
   only given before the stop.
4. Whether a vendor exits on EOF does not decide eligibility (Cursor never does and passes), but it
   does decide what a restart costs it: Cursor's stop always takes the full 5 s grace.
