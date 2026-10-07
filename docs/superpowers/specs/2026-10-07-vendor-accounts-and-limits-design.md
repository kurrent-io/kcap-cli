# Vendor accounts and usage limits

Builds on the account-switching feasibility study (session "Analyze automatic account switching for
desktop and hosted agents", 2026-09-24). This is the first of two specs: it covers knowing every
Claude and Codex account on a host, recording all of them, and showing their usage limits. Choosing
an account when launching a hosted agent is spec 2.

## 1. Problem

People run several Claude or Codex subscriptions side by side by pointing the vendor CLI at a
separate config directory — `CLAUDE_CONFIG_DIR=~/.claude-work claude`, `CODEX_HOME=~/.codex-b codex`,
or a tool such as claude-swap that does the same. kcap knows exactly one directory per vendor: the one
in effect when `kcap setup` ran.

- **Sessions in any other directory are silently lost.** Recording depends on the kcap plugin being
  enabled in that directory's `settings.json` (Claude) or the kcap hook in its `hooks.json` (Codex).
  Setup wires one directory; a session under another fires no kcap hook and never reaches the server.
  Nothing tells the user.
- **There is no view of usage limits.** A user with several accounts cannot see which one has room
  before starting work, and a hosted agent stopping on a limit is the first sign of trouble.

## 2. Decisions

| Decision | Choice |
|---|---|
| Unit | An **account** is one vendor + one config directory on one host. The directory is the key. |
| Discovery | Detect candidates and let the user confirm each; explicit add for anything missed. |
| Recording | Every listed account is wired (plugin/hooks, MCP, skills, status line). |
| Claude readings | A `kcap statusline` wrapper installed in each account's `settings.json`, wrapping the user's own status line. |
| Codex readings | The `rate_limits` object already present in Codex session logs. |
| Credentials | Never read. No token is opened, copied or sent; no usage endpoint is polled. |
| Ownership | The daemon on each host owns the account list and the readings. |
| Transport | Daemon → server (owner-only) → app for remote hosts; daemon → app over the local socket for this host. |
| UI | An Accounts screen per host. No header chips in this spec. |
| Out of scope | Account picker at launch, per-agent config directories, automatic switching (spec 2). |

"Profile" already means a Capacitor server profile (`KCAP_PROFILE`); this feature says **account**
everywhere — CLI, UI, code — to keep the two apart.

## 3. Accounts

### 3.1 What an account carries

| Field | Source |
|---|---|
| Vendor, directory | The list itself |
| Label | User-editable; defaults to the email (Claude) or the directory name (Codex) |
| Email, org id, org name | Claude: `claude auth status --json` with `CLAUDE_CONFIG_DIR=<dir>` |
| Plan | Claude: `subscriptionType` from the same command. Codex: `plan_type` from its readings |
| Signed in | Claude: `loggedIn`. Codex: exit status / output of `codex login status` with `CODEX_HOME=<dir>` |
| Wired | The existing wiring checks, run against this account's layout |
| Latest reading | Section 5 |

`claude auth status --json` prints `loggedIn`, `authMethod`, `email`, `orgId`, `orgName`,
`subscriptionType` and `configDirectory`; it prints no secret. `codex login status` prints only the
sign-in method. kcap does not open Codex's `auth.json` to learn the email: that is reading a
credential file.

The default directory (`~/.claude`, `~/.codex`, or the override in the daemon's environment) is
always an account when the vendor CLI is installed, so an existing install behaves as before with a
list of one.

### 3.2 Same login in two directories

Two Claude directories reporting the same `orgId` and email are one subscription and share limits.
The screen groups them as one account with two directories, and readings from either update it.
Codex exposes no identity, so two Codex directories with one login show as two accounts with the
same numbers — a known gap.

### 3.3 Discovery

Setup and the app's Add flow offer candidates; nothing is wired until the user confirms it.

- Directories under the user home matching `.claude*` / `.codex*` that contain the vendor's own
  files (`settings.json` or `projects/` for Claude; `config.toml`, `auth.json` presence or
  `sessions/` for Codex — presence only, never contents).
- claude-swap's profile directories.
- `CLAUDE_CONFIG_DIR` / `CODEX_HOME` in the current environment.

`kcap accounts add <vendor> <dir>` and the app's "Choose a directory" cover the rest. `kcap accounts
remove` unwires the account and forgets its readings; it never deletes the directory or signs out.

### 3.4 Ownership

The list lives in the daemon's state on that host and changes only through the daemon — from the app
or from `kcap accounts` talking to it over the local socket — following the one-lane rule for daemon
mutations.

## 4. Recording every account

`ClaudeHarness.Over(paths)` and `CodexHarness.Over(paths)` already build a harness over an arbitrary
layout (reviewers' isolated homes use them). Wiring an account runs the existing installers against
that account's layout instead of the daemon-wide one:

- **Claude:** enable the kcap plugin in `<dir>/settings.json`; install the status line wrapper there
  (Section 5.1).
- **Codex:** the kcap hook in `<dir>/hooks.json`; MCP registration in `<dir>/config.toml`, tracked in
  that directory's own `mcp-ownership-v1.json`, so "owns only what it created" holds per account.
- **Skills:** team skills are materialized into each Claude account's `<dir>/skills` as well as
  `~/.claude/skills` (see 4.1).

Wiring happens when an account is added or confirmed, and `kcap setup` and the app's re-wire action
iterate every listed account. `kcap status` and the Accounts screen report wiring per account,
replacing the single default-directory check.

`kcap import` and any other `projects/` scan take each account's layout. Live sessions need no
change: hooks hand over the transcript path.

An account nobody listed stays unrecorded. Discovery and the not-wired warning narrow that gap; they
do not close it.

### 4.1 Verify first: do user skills follow `CLAUDE_CONFIG_DIR`?

The vendor docs say `CLAUDE_CONFIG_DIR` relocates "all settings, session history, and plugins" and
list personal skills only as `~/.claude/skills`. This spec assumes skills follow the config
directory. The first implementation step is a live check — a skill placed only in `<dir>/skills`,
then only in `~/.claude/skills`, listed from a session under `CLAUDE_CONFIG_DIR=<dir>` — and the
skills bullet above is dropped if `~/.claude/skills` turns out to load regardless.

## 5. Readings

A reading is the latest known usage for one account: each window's used percentage and reset time,
the plan where known, when it was taken, and the session it came from. One small JSON file per
account in kcap's local state directory, replaced atomically. Readers tolerate a missing or
unparseable file by keeping the previous value.

### 5.1 Claude: `kcap statusline`

Claude Code passes the status line command a JSON document on stdin that, for Pro and Max
subscribers, includes `rate_limits.five_hour` and `rate_limits.seven_day`, each with
`used_percentage` and `resets_at` (epoch seconds). It is present only after the session's first API
response, and a window is dropped once its reset passes.

**Install.** Wiring sets `statusLine` in `<dir>/settings.json` to
`kcap statusline --account <id>`. The account is named in the command because each account has its
own settings file; the wrapper cannot rely on `CLAUDE_CONFIG_DIR` in its environment, which
`CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` removes. An existing `statusLine` (command, `padding`,
`refreshInterval`, `hideVimModeIndicator`) moves into kcap's per-account state as the original;
`padding`, `refreshInterval` and `hideVimModeIndicator` stay in `settings.json` because they
configure the bar, not the command.

**Re-wire.** A `statusLine.command` that is not ours means the user changed it: adopt it as the new
original and reinstall ours. Never overwrite the user's change without keeping it.

**Unwire.** Restore the original exactly, or remove `statusLine` if there was none.

**Each run.**

1. Read stdin.
2. If `rate_limits` is present, write the account's reading. No network call and no daemon IPC on
   this path: Claude cancels a status line still running when the next update arrives.
3. Run the original command with the same stdin and environment and pass its stdout and exit code
   through unchanged. With no original, print nothing and exit 0.

A failure in step 2 never affects step 3. A project's own `statusLine` overrides the account's;
those projects give no readings.

### 5.2 Codex: session logs

The Codex watcher already tails each rollout. Codex writes a `rate_limits` object on token-count
events, for example:

```json
{"limit_id":"codex","primary":{"used_percent":4.0,"window_minutes":10080,"resets_at":1791622733},
 "secondary":null,"credits":{"has_credits":true,"unlimited":false,"balance":"62500"},
 "plan_type":"pro","rate_limit_reached_type":null}
```

The watcher writes a reading for the rollout's `CODEX_HOME`: each window with its length in minutes,
`plan_type`, the credits balance, and readings for other `limit_id`s kept separately ("+N per
model"). This is a side output of the watcher. The transcript projection does not change, so
canonical event ids are untouched.

### 5.3 Limit hits

The existing PTY usage-limit detector stays as it is and additionally marks the account's reading
"blocked until <reset>", covering a single long turn that crosses every threshold between readings.

## 6. Daemon, server and app

### 6.1 Daemon

The daemon keeps one snapshot per account: the list entry, wiring state, sign-in state, and latest
reading.

- Reading files are watched and debounced (a few seconds).
- Sign-in state is refreshed on account add, daemon start, explicit refresh, and after a limit hit —
  not on a timer: each run starts the vendor CLI.
- Wiring state is refreshed on setup, re-wire and daemon start.

### 6.2 Server

- `DaemonConnect` gains an `Accounts` member, appended last so older servers keep binding.
- A new `AccountsChanged` hub call carries the full snapshot on change, at most once per 30 seconds.
- The server keeps the latest snapshot per daemon and account and serves it **only to the daemon's
  owner**. Email and org name are personal data, and no other user needs them.
- Requires a matching kcap-server change.

### 6.3 Local app

A new local-socket frame type carries the snapshot, appended to `FrameType` and advertised in
`LocalControlCapabilities.Current` beside its handler. The app reads its own host over the socket
and other hosts from the server, as it does for agents today.

### 6.4 Accounts screen

- **Accounts** page with a host switcher.
- Summary cards: accounts ready for a long run (every window under 90%), the next reset, and how many
  need a fix (not signed in or not wired).
- Rows grouped by vendor. Each row: label and email, plan badge, a 5-hour and a weekly bar with the
  reset countdown, "+N per model" where applicable, reading age, wired and signed-in state, and a
  menu with refresh, re-wire, rename and remove. "No reading yet" when none exists; a window whose
  reset has passed shows as reset with unknown usage.
- **Add account:** detected candidates first, then "Choose a directory". Adding wires the account
  and, if it is not signed in, tells the user to run the vendor's sign-in with that directory
  (`CLAUDE_CONFIG_DIR=<dir> claude`, then `/login`; `CODEX_HOME=<dir> codex login`). kcap never
  performs the sign-in itself.
- **Colors:** bars are neutral until a threshold and then use `KcapWarning*`; a blocked account uses
  `KcapDanger*`. The Default badge and plan badges use neutral chip styling — identity never takes
  a status color.
- Built with the Kcap control classes (`kcapPanel`, `kcapGhost`, `kcapChip`, `kcapField`).

### 6.5 Failures

| Condition | Shown as |
|---|---|
| Vendor CLI not installed | Sign-in "unknown" |
| Status command fails or reports signed out | "Needs sign-in" |
| Reading file unreadable | Previous reading kept |
| Host offline | Server's last snapshot, "host offline · last seen …" |
| Account directory deleted | "Directory missing", with remove in the menu |

## 7. Testing

- **Status line wrapper:** reading written from stdin, including `rate_limits` absent and one window
  only; original run with identical stdin, stdout and exit code passed through; no original prints
  nothing; a reading write failure does not change output.
- **Status line install:** install, adopt, restore round-trip `settings.json` exactly, including a
  user edit between re-wires and the bar-only keys staying in place.
- **Codex readings:** parsed from rollout fixtures — primary only, both windows, several `limit_id`s,
  credits, `rate_limit_reached_type` set.
- **Discovery:** a `TempDir` with Claude- and Codex-shaped directories, claude-swap profiles and
  look-alike directories without vendor files.
- **Multi-account wiring:** each directory gets plugin/hooks, MCP ownership tracked per account,
  unwire removes only what we added.
- **Daemon snapshot:** a reading change appears within the debounce; changes to the server are
  throttled; a corrupt file keeps the previous reading; same-login Claude directories group.
- **Local IPC:** the new frame type is advertised only with its handler.
- **App:** headless view-model tests for thresholds, reading age, reset windows, host offline,
  missing directory, and the summary cards.

## 8. Not doing

- Reading, copying or brokering any vendor credential.
- Polling a vendor usage endpoint.
- Recording an account directory nobody listed.
- Choosing an account at launch, per-agent config directories, or switching accounts on a limit —
  spec 2.
