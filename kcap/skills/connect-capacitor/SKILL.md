---
name: connect-capacitor
description: >-
  This skill should be used on a Grok Bot cloud computer (or a fresh machine
  with no kcap installed) when the user asks to "connect Capacitor", "set up
  Capacitor", "set up kcap", "log in to Capacitor", "start recording my chats",
  or when a kcap tool or skill fails because `kcap` is not installed or not
  signed in. Installs the kcap CLI and signs it in, after which the installed
  plugin's watcher records every chat.
---

# Connect Capacitor

Chats are recorded by `kcap grok-bot watch`, a background process on this cloud
computer that reads every chat from the local Grok Bot gateway. It needs
the `kcap` CLI installed and signed in. Every Bot on the account shares the
computer, so this is a one-time step per account, not per Bot.

## 1. Check what is already there

```bash
command -v kcap && kcap whoami
```

If `whoami` prints a signed-in user, skip to step 4.

## 2. Install the CLI

Requires Node.js 18+ (`node -v`). Then:

```bash
npm install -g @kurrent/kcap
command -v kcap
```

`command -v kcap` must print a path. If it does not, the npm global bin
directory is not on `PATH`; add it (`npm prefix -g` + `/bin`) in the shell
profile, because the hooks run `kcap` by name.

## 3. Sign in

Ask the user for their Capacitor server URL (for example
`https://acme.kcap.ai`) unless they already gave it. Then run, in the
background so its output can be read while it waits:

```bash
kcap setup --server-url <url> --device --no-prompt \
  --skip-cursor-hooks --skip-cursor-mcp --skip-import
```

- `--device` prints a URL and a short code. **Show both to the user** and ask
  them to approve on their own device; the command finishes once they do.
- `--skip-cursor-hooks` and `--skip-cursor-mcp` are required: this plugin
  already supplies the MCP servers, and Grok Bot's hooks carry no conversation
  id, so they cannot record a chat.

## 4. Start the watcher

```bash
pgrep -af "kcap grok-bot watch" || {
  mkdir -p ~/.config/kcap/grok-bot
  nohup kcap grok-bot watch >> ~/.config/kcap/grok-bot/watch.log 2>&1 &
}
sleep 10; tail -5 ~/.config/kcap/grok-bot/watch.log
```

The log should show `watching …` and then `opened session …` / `sent N entries`
lines. A second watcher refuses to start, so running this again is safe — run it
again whenever `pgrep` finds nothing (after a computer restart, for example).

## 5. Tell the user

- Every Bot's chats are recorded, including history from before today: the
  first run backfills each thread.
- A session ends after two hours of quiet; the next message starts a new one.
- Only the conversation is recorded — messages and answered questions, not tool
  calls or files.
- Sessions are private to them by default.
