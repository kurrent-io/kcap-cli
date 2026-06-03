# Cursor hook probe

A throwaway capture tool that records exactly what Cursor passes to a hook
script (argv, env, cwd, stdin) per event. Used once to resolve two open
questions for [AI-669](../../docs/superpowers/specs/2026-06-01-ai-669-cursor-hooks-ingest-design.md):

1. Does Cursor pass the event name as argv, env var, or somewhere else?
2. Does `sessionStart.session_id` equal the dashless composer ID that
   `kcap import --cursor` uses?

## Install

```sh
# 1. Back up any existing user-scope hooks config
cp -n ~/.cursor/hooks.json ~/.cursor/hooks.json.bak 2>/dev/null || true

# 2. Place the probe in ~/.cursor/ (Cursor docs: user hooks run from there,
#    so relative `./kcap-hook-probe.sh` resolves correctly)
mkdir -p ~/.cursor
cp scripts/cursor-hook-probe/cursor-hook-probe.sh ~/.cursor/kcap-hook-probe.sh
chmod +x ~/.cursor/kcap-hook-probe.sh

# 3. Install the hooks config (overwrites — back up first if you have one)
cp scripts/cursor-hook-probe/hooks.json.example ~/.cursor/hooks.json
```

Cursor watches `hooks.json` and reloads on save — no Cursor restart needed.

## Capture

Open Cursor and do one short Agent-mode conversation in a workspace you don't
mind probing:

1. Start a new Agent chat → `sessionStart` fires.
2. Send one prompt that triggers a tool (e.g. "list files in this directory") →
   `beforeSubmitPrompt`, `preToolUse`, `postToolUse`, `afterAgentResponse` fire.
3. Optional: ask a question that produces visible reasoning →
   `afterAgentThought` fires.
4. End / close the conversation → `sessionEnd` fires.

All captures land in `~/kcap-cursor-hook-probe/`:

```
20260601T143012Z-sessionStart-12345.stdin.json
20260601T143012Z-sessionStart-12345.meta.txt
20260601T143015Z-beforeSubmitPrompt-12347.stdin.json
20260601T143015Z-beforeSubmitPrompt-12347.meta.txt
...
```

## Cross-check `session_id` ↔ composer ID

While the probe captures are still on disk, in the same workspace:

```sh
kcap import --cursor --cwd "$(pwd)"
```

Compare `sessionStart.stdin.json` → `session_id` against the session IDs the
import emits (look for log lines from `CursorImportSource`). If they match
after `NormalizeCursorSessionId` (dashless), we're done. If they don't, the
spec needs a translation table.

## Send back

Paste or attach:

- One `*.stdin.json` per distinct event you saw (sessionStart, sessionEnd,
  beforeSubmitPrompt, afterAgentResponse, preToolUse, postToolUse,
  afterAgentThought if you triggered one).
- The matching `.meta.txt` for `sessionStart` (the most useful one for the
  argv/env contract question).
- The `composer_id` you observed in the `kcap import --cursor` output
  for the same session.

Five to ten files total — enough to close both open questions.

## Uninstall

```sh
mv ~/.cursor/hooks.json.bak ~/.cursor/hooks.json 2>/dev/null \
  || rm ~/.cursor/hooks.json
rm ~/.cursor/kcap-hook-probe.sh
rm -rf ~/kcap-cursor-hook-probe
```
