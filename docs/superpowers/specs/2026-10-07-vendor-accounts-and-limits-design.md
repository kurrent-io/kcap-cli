# Vendor accounts and usage limits

Builds on the account-switching feasibility study (session "Analyze automatic account switching for
desktop and hosted agents", 2026-09-24). This is the first of two specs: it covers knowing every
Claude and Codex account on a host, recording all of them, and showing their usage limits. Choosing
an account when launching a hosted agent is spec 2.

Implemented as two plans and two PRs from this one spec: **Part A — recording every account**
(Sections 3–4), which fixes silent session loss on its own; then **Part B — limits** (Sections 5–6).

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
| Registry | A per-user file store at a fixed location, changed under a lock by the CLI or any daemon. |
| Discovery | Detect candidates and let the user confirm each; explicit add for anything missed. |
| Recording | Every listed account is wired (plugin/hooks, MCP, status line) through one shared wiring service. |
| Claude readings | A `kcap statusline` wrapper in each account's `settings.json`, wrapping the user's own status line. |
| Codex readings | `rate_limits` in rollouts (PTY sessions), `account/rateLimits/updated` (app-server sessions), and `account/rateLimits/read` on refresh. |
| Credentials | Never read. No token or credential file is opened, copied or sent. Vendor processes make their own authenticated calls. |
| Transport | Every daemon publishes the registry and readings: to the server (owner-only) and to the local app. |
| Remote hosts | Read-only in the app. Changes happen on the host itself. |
| UI | An Accounts screen per host. No header chips in this spec. |
| Out of scope | Account picker at launch, per-agent config directories, automatic switching (spec 2). |

"Profile" already means a Capacitor server profile (`KCAP_PROFILE`); this feature says **account**
everywhere — CLI, UI, code — to keep the two apart.

## 3. Accounts

### 3.1 The registry

A single per-user store, `accounts/` beside the daemons directory, at a fixed location that ignores
`KCAP_CONFIG_DIR` for the same reason the daemons directory does: two daemons or two config roots on
one machine must see one list, or each would wire the same `settings.json` and undo the other's
changes. It holds:

- `accounts.json` — the list: id (stable, generated), vendor, directory (full path, resolved), label,
  and per-account wiring bookkeeping (Section 4.4).
- `readings/<id>.json` — the latest reading per account (Section 5.1).

Mutations — add, remove, rename, wire, unwire — take an exclusive file lock on `accounts.json`,
re-read it, apply, and replace it atomically. Any kcap process may mutate: `kcap accounts`, `kcap
setup`, `kcap uninstall`, the npm refresh, or a daemon acting for the desktop app. No daemon has to be
running. Daemons are readers and publishers (Section 6.1).

The default directory (`~/.claude`, `~/.codex`, or the `CLAUDE_CONFIG_DIR` / `CODEX_HOME` in effect at
setup) is registered when setup wires it, so an existing install migrates to a list of one with no
behavior change.

### 3.2 What an account shows

| Field | Source |
|---|---|
| Label | User-editable; defaults to the email, else the directory name |
| Email, org | Claude: `claude auth status --json` (`email`, `orgId`, `orgName`) with `CLAUDE_CONFIG_DIR=<dir>`. Codex: app-server `account/read` (`email`, `chatgptAccountId`) with `CODEX_HOME=<dir>` |
| Plan | Claude: `subscriptionType`. Codex: `planType` from `account/read` or readings |
| Signed in | Claude: `loggedIn`. Codex: `account/read` returns an account |
| Recording state | Section 4.3 |
| Latest reading | Section 5 |

`claude auth status --json` prints no secret. kcap never opens Codex's `auth.json`; the app-server
reports the account itself.

Identity is refreshed on add, daemon start, explicit refresh, and after a limit hit — not on a timer,
since each refresh starts a vendor process. When a refresh reports a different identity than the one
recorded (a different login in the same directory), the account's reading and blocked marker are
discarded before the new identity is stored.

### 3.3 Same login in two directories

Two directories of one vendor reporting the same identity (Claude: `orgId` + email; Codex:
`chatgptAccountId`) share limits. The screen shows them as one row listing both directories; readings
from either update it. Accounts with unknown identity (signed out, CLI missing, refresh failed) never
group. Per-directory actions — re-wire, remove — act on a chosen directory, and the row's menu lists
them by path.

### 3.4 Discovery

Setup and the Add flow on this host offer candidates; nothing is registered or wired until the user
confirms it.

- Directories directly under the user home matching `.claude*` / `.codex*` that contain the vendor's
  own files (`settings.json` or `projects/` for Claude; `config.toml` or `sessions/` for Codex —
  presence only, never contents).
- claude-swap's profile directories.
- `CLAUDE_CONFIG_DIR` / `CODEX_HOME` in the current environment.

`kcap accounts add <vendor> <dir>` and the app's "Choose a directory" cover the rest. `kcap accounts
remove` unwires the account and deletes its reading; it never deletes the directory or signs out.

## 4. Part A — recording every account

### 4.1 One wiring service

Today the writes live in the CLI executable (`SetupCommand.InstallPlugin`,
`PluginCommand.InstallCodexHooks`, Codex MCP setup) and `ClaudePluginInstaller` / `CodexHooksInstaller`
are detection and marker helpers; the daemon cannot call the CLI's code. Part A moves the install,
uninstall and status logic into a Core service, `AccountWiring`, taking an account's layout
(`ClaudeHarness.Over(paths)` / `CodexHarness.Over(paths)`) and returning a per-step result. Setup,
`kcap plugin`, `kcap accounts`, uninstall, the npm refresh path (`npm/kcap/bin/refresh.js` and the
`plugin --if-installed` commands it runs) and daemons all call it, and every caller iterates the
registry instead of the one environment-derived layout. Each wiring run holds the registry lock for
that account, so concurrent setup, refresh and app actions serialize.

Per account it does:

- **Claude:** register the marketplace and enable the kcap plugin in `<dir>/settings.json`; install
  the status line wrapper (Section 5.2).
- **Codex:** the kcap hook in `<dir>/hooks.json`; MCP registration in `<dir>/config.toml` tracked in
  that directory's own `mcp-ownership-v1.json`, so "owns only what it created" holds per account.

Skills need nothing per account: team skills materialize into each repository's own `.claude/skills`
and `.agents/skills`, which every account's sessions read, and kcap's packaged skills ship inside the
plugin.

### 4.2 Attribution in recording paths

Hooks are shared plugin hooks with no account argument, and `CLAUDE_CONFIG_DIR` may be scrubbed from
their environment. Every recording path that reads a vendor layout derives the account from data the
vendor hands it, validated against the registry:

- **Claude hooks:** the account whose `projects/` contains `transcript_path`. Plan capture
  (`ClaudeHookCommand`, both sites reading `Paths.Plans`) uses that account's layout.
- **Codex hooks and watcher:** the account whose `sessions/` contains the rollout path. Title lookup
  (`HarnessTitleStores`, the Codex `session_index.jsonl` reader) uses that account's home.
- **Import:** `kcap import` and every other `projects/` / `sessions/` scan iterate all registered
  accounts, each with its own title store.

A path inside no registered account falls back to the environment-derived layout, as today.

### 4.3 Recording state

"Wired" is not enough to know an account records. Each account reports one of:

| State | Meaning |
|---|---|
| Recording | Claude: plugin enabled and the payload effectively installed (`IsEffectivelyInstalled`). Codex: hook present and trusted |
| Needs trust | Codex hook installed but not yet trusted (`CodexHookTrust`); the screen and setup tell the user to trust it in that home |
| Broken | Plugin enabled but payload missing, or hook command present but unusable |
| Not wired | Nothing installed |

The Accounts screen and `kcap status` show this per account, replacing today's single
default-directory check.

### 4.4 Uninstall

`kcap uninstall` unwires every registered account through `AccountWiring` — plugin, hooks, MCP and
status line — before it stops daemons and removes kcap's config, and deletes the registry last. A
failure on one account is reported and leaves that account's registry entry and status line backup
in place, so a later uninstall can finish it.

### 4.5 Testing

- Each directory in a two-account `TempDir` gets plugin/hooks; MCP ownership is tracked per account;
  unwire removes only what we added.
- A new session under a non-default account records end to end, for Claude and for Codex.
- Plan capture under a non-default Claude account with `CLAUDE_CONFIG_DIR` absent from the hook's
  environment; Codex titles across two homes; import across two accounts.
- Recording state: missing Claude payload reports Broken; untrusted Codex hook reports Needs trust.
- Registry: two processes mutating concurrently; two named daemons and two `KCAP_CONFIG_DIR` roots see
  one list; uninstall with one account failing.

## 5. Part B — readings

### 5.1 Reading model

A reading file holds, per window: label, window length, used percentage, reset time, and the time the
vendor reported it (`observedAt`). Windows are keyed by `(limitId, windowLength)`; Claude has one
limit with a 5-hour and a 7-day window. Plan and credits ride along where known.

Writers merge rather than overwrite: under a short per-file lock, read the file, replace a window only
when the incoming `observedAt` is newer, and replace the file atomically. Out-of-order writers
therefore never move a window backwards, and one Codex `limitId` never erases another. A writer whose
account id is no longer in the registry writes nothing, so a session still running after the account
is removed cannot recreate its reading.

A window whose reset time has passed is shown as reset with unknown usage until a new observation
arrives. A reading for an account with no observation yet is "no reading", not zero.

### 5.2 Claude: `kcap statusline`

Claude Code passes the status line command JSON on stdin that, for Pro and Max subscribers, includes
`rate_limits.five_hour` and `rate_limits.seven_day`, each with `used_percentage` and `resets_at`
(epoch seconds), present only after the session's first API response. Updates are debounced at 300ms
and an in-flight run is cancelled when the next update fires.

**Command.** Wiring writes `statusLine.command` as `kcap statusline --account <id>`, resolved and
quoted the same way the plugin's hook commands invoke kcap, so the wrapper has the same PATH contract
as recording itself. The account id is in the command because the wrapper cannot rely on
`CLAUDE_CONFIG_DIR`, which `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` removes.

**Backup before replacement.** Before changing `settings.json`, wiring writes the account's original
`statusLine` (or "none") into the registry entry and flushes it. Only then is `settings.json`
rewritten. `padding`, `refreshInterval` and `hideVimModeIndicator` stay in `settings.json`: they
configure the bar, not the command. An interrupted install leaves either the original setting with a
backup (re-wire completes it) or our command with a backup (already done).

**Re-wire.** If `statusLine.command` is ours, nothing changes. If it is something else, the user
changed it: back it up as the new original, then reinstall ours. Our own command is never adopted as
an original.

**Unwire.** Restore the backup only while `statusLine.command` is still ours. If the user has since
replaced it, leave their setting and discard the backup. A missing or corrupt backup with our command
installed removes `statusLine.command` and reports it, rather than guessing.

**Startup.** `kcap statusline` is dispatched before any of the CLI's ordinary startup: no repository
or profile resolution, no git call, no telemetry, no server-URL gate, no update notice. It reads the
registry and writes a reading file, nothing else.

**Each run.**

1. Read stdin to end.
2. If `rate_limits` is present and the account is registered, merge the reading (Section 5.1). Any
   failure here is swallowed.
3. If the backup holds an original command, run it through the user's shell the way Claude runs a
   status line command, with the same stdin bytes, environment and working directory, and pass its
   stdout, stderr and exit code through. With no original, print nothing and exit 0.
4. If the wrapper itself is cancelled, the original's process tree is killed with it.

A project's own `statusLine` overrides the account's; those projects give no readings.

### 5.3 Codex

- **PTY sessions:** the rollout watcher already tails each rollout. Token-count events carry
  `rate_limits` (`limit_id`, `primary` / `secondary` with `used_percent`, `window_minutes`,
  `resets_at`, `plan_type`, `credits`). The watcher merges them into the reading of the account
  whose `sessions/` holds the rollout.
- **App-server sessions** (hosted reviewers and opted-in interactive launches, which suppress the
  watcher): the runtime handles `account/rateLimits/updated` notifications and merges them into the
  reading of the account whose `CODEX_HOME` launched it — the daemon's default account until spec 2
  adds per-agent homes.
- **Idle accounts:** refresh (Section 3.2) starts `codex app-server` with that account's
  `CODEX_HOME` and calls `account/read` and `account/rateLimits/read`. Codex makes the authenticated
  call; kcap never sees a token.

All three are side outputs. The transcript projection does not change, so canonical event ids are
untouched, and app-server sessions get no second transcript source.

### 5.4 Limit hits

When the hosted-agent usage-limit detector reports a block, the daemon queues — outside the PTY read
loop — a "blocked" marker on the reading of the account that agent runs under (the daemon's default
Claude account until spec 2). The detector yields text only, so the marker has no reset time of its
own: it borrows the earliest reset among that account's windows at or above 100%, else "reset
unknown". It clears when that reset passes, or when a newer observation shows every window below
100%. A sign-in refresh is queued the same way.

### 5.5 Testing

- **Wrapper:** reading merged from stdin, including `rate_limits` absent and one window only; the
  original runs with identical stdin, environment and working directory and its output and exit code
  pass through; no original prints nothing; a reading failure does not change output; a slow original
  is killed with the wrapper on cancellation. Run against the real executable with no server config
  and an unreachable network, and assert no network call.
- **Install / re-wire / unwire:** exact `settings.json` round-trip; user edit then re-wire; user edit
  then unwire keeps the edit; interrupted install at each step; corrupt backup.
- **Readings:** out-of-order writers; several `limit_id`s; removal during a live session; identity
  change discards the old reading.
- **Codex:** rollout fixtures (primary only, both windows, several limit ids, credits, limit reached);
  `account/rateLimits/updated` in an app-server session; refresh of an idle account against a stub
  app-server.
- **Limit hit:** block before any reading; reset known and unknown; clearing on reset and on a lower
  reading.

## 6. Part B — publishing and the screen

### 6.1 Daemons

Every daemon watches the registry and the readings directory (debounced, a few seconds) and publishes
one snapshot per account: registry entry, identity, recording state, reading. Several daemons on one
host publish the same snapshot; the server keys it by host and account, so duplicates are idempotent.
Account mutations requested by the desktop app go through the daemon, which calls `AccountWiring`
under the registry lock.

### 6.2 Server

- The server advertises an `accounts` capability; a daemon sends account data only to a server that
  advertises it, so an older server never receives a method it cannot handle.
- `DaemonConnect` gains an `Accounts` member, appended last so older servers keep binding.
- `AccountsChanged` carries the full snapshot, throttled to one call per 30 seconds with a trailing
  send, so the last change in a burst — including a removal — always goes out. Each snapshot carries
  the daemon's connection epoch; the server drops snapshots from a superseded connection.
- The server persists the latest snapshot per host and account so an offline host still shows its
  last state after a server restart, and serves it **only to the host's owner** — reads and
  subscriptions alike. Email and org name are personal data.
- Matching kcap-server change: storage, owner-scoped read and subscription for the desktop app, and
  the capability.

### 6.3 Local app

Two appended local-socket frame types, each with encode and decode in `FrameCodec` and a handler,
advertised in `LocalControlCapabilities.Current` beside their handlers:

- **AccountsSnapshot** — daemon to app, on subscribe and on change.
- **AccountAction** — app to daemon: `add`, `remove`, `rename`, `rewire`, `refresh`, each with a
  request id, the account id (or vendor + directory for `add`), and a reply carrying the per-step
  wiring result.

In the app, account actions enter the existing app-lifetime mutation lane as a new verb carrying the
action and account id. Two actions on the same account never coalesce; actions on different accounts
queue independently. Success is the next snapshot showing the expected state (account present,
absent, renamed, or recording state after re-wire), never the reply alone. A daemon that does not
advertise the frames gets no account UI; actions are disabled with "update kcap on this host".

### 6.4 Accounts screen

- **Accounts** page with a host switcher. This host is editable; remote hosts are read-only and say
  where to make changes.
- Summary cards:
  - **Ready for a long run:** accounts that are recording, signed in, not blocked, and have a reading
    under an hour old in which every window is under 90%. Accounts with no reading count as unknown,
    not ready.
  - **Next reset:** the earliest reset across all windows.
  - **Need a fix:** not signed in, Needs trust, Broken, or Not wired.
- Rows grouped by vendor. Each row: label and email, plan badge, one bar per window labeled from its
  actual length ("5h", "Week", or the minutes Codex reports), reset countdown, "+N per model" for
  extra limit ids, reading age, recording and sign-in state, and on this host a menu with refresh,
  re-wire, rename and remove.
- **Add account** (this host only): detected candidates first, then "Choose a directory". Adding
  wires the account and, if it is not signed in, shows the vendor's sign-in for that directory
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
| Identity refresh fails or reports signed out | "Needs sign-in" |
| Reading file unreadable | Previous reading kept |
| Host offline | Server's last snapshot, "host offline · last seen …" |
| Account directory deleted | "Directory missing", with remove in the menu |
| Daemon too old for account frames | Read-only, "update kcap on this host" |

### 6.6 Testing

- **Daemon:** a registry or reading change appears in the snapshot within the debounce; trailing
  throttle sends the last change of a burst; two daemons publish one host's accounts idempotently.
- **Server contract:** old daemon / new server and new daemon / old server; another user's read and
  subscription denied; disconnect with a pending update; removal propagation; server restart with an
  offline host.
- **Local IPC:** frames advertised only with handlers; action refused by an old daemon; queued actions
  on one account do not coalesce; partial wiring failure surfaces per step.
- **App:** headless view-model tests for window labels, thresholds, reading age, reset windows, the
  ready predicate (no reading, stale, blocked, expired windows, per-model exhaustion), host offline,
  missing directory, and remote read-only.

## 7. Not doing

- Reading, copying or brokering any vendor credential.
- Calling a vendor usage endpoint ourselves; vendor processes make their own calls.
- Recording an account directory nobody listed.
- Changing accounts on a remote host from the app.
- Choosing an account at launch, per-agent config directories, or switching accounts on a limit —
  spec 2.
