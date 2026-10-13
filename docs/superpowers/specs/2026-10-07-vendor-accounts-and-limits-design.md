# Vendor accounts and usage limits

Builds on the account-switching feasibility study (session "Analyze automatic account switching for
desktop and hosted agents", 2026-09-24). This is the first of two specs: it covers knowing every
Claude and Codex account on a host, recording all of them, and showing their usage limits. Choosing
an account when launching a hosted agent is spec 2.

Implemented as two plans and two PRs from this one spec: **Part A — recording every account**
(Sections 3–4), which fixes silent session loss on its own; then **Part B — limits** (Sections 5–6).
Part A installs nothing that only Part B's binary understands (Section 4.6).

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
| Recording | Every listed account is wired (plugin/hooks, MCP; status line in Part B) through one shared wiring service. |
| Claude readings | A `kcap statusline` wrapper in each account's `settings.json`, wrapping the user's own status line. |
| Codex readings | `rate_limits` in rollouts (PTY sessions), `account/rateLimits/updated` (app-server sessions), and `account/rateLimits/read` on refresh. |
| Credentials | Never read. No token or credential file is opened, copied or sent. Vendor processes make their own authenticated calls. |
| Transport | Every daemon publishes the registry's public view and readings: to the server (owner-only) and to the local app. |
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
changes. The directory is owner-only (0700, files 0600). It holds:

- `host.json` — a generated host id. This, not the per-config-root machine id, is the host key for
  account data on the server, so daemons under different config roots publish under one host.
- `accounts.json` — a `revision` counter and the list. Each entry: id (stable, generated), vendor,
  directory (resolved full path), label, identity and its `generation` (Section 3.2), and local-only
  wiring bookkeeping (Section 5.2). Every mutation increments `revision`.
- `readings/<id>.json` — the latest reading per account (Section 5.1).
- `accounts.lock`, `readings.lock` — permanent lock files, never replaced or deleted while kcap is
  installed, acquired with `ConfigFileLock` on their fixed paths. Locking `accounts.json` itself would
  not work: its atomic replacement hands the next caller a different file.

**Mutations** — add, remove, rename, wire, unwire, identity update — take `accounts.lock`, re-read
`accounts.json`, apply, increment `revision`, and replace it atomically. Any kcap process may mutate:
`kcap accounts`, `kcap setup`, `kcap plugin`, `kcap uninstall`, the npm refresh, or a daemon acting
for the desktop app. No daemon has to be running. A daemon publishes, and mutates only on behalf of the desktop app
(Section 6.1).

**Lock order** is always `accounts.lock` before `readings.lock`. Removal and identity retirement hold
both: update `accounts.json`, then delete the account's reading. Reading writers take only
`readings.lock` (Section 5.1).

**Adoption.** A fresh or pre-registry install has an empty list. Every wiring entry point — setup,
`kcap plugin install`, `plugin --if-installed` from the npm refresh — first registers the
environment-derived default directory of each vendor it is about to wire (`~/.claude`, `~/.codex`, or
`CLAUDE_CONFIG_DIR` / `CODEX_HOME` in effect), if absent and not skipped by the user's per-vendor
setup choices. An upgrade therefore becomes a list of one with no behavior change, with or without
re-running setup.

### 3.2 What an account shows

| Field | Source |
|---|---|
| Label | User-editable; defaults to the email, else the directory name |
| Email, org | Claude: `claude auth status --json` (`email`, `orgId`, `orgName`) with `CLAUDE_CONFIG_DIR=<dir>`. Codex: app-server `account/read` (`email`, `chatgptAccountId`) with `CODEX_HOME=<dir>` |
| Plan | Claude: `subscriptionType`. Codex: `planType` from `account/read` or readings |
| Sign-in | `signed-in` / `signed-out` only from an authoritative vendor answer; `unknown` otherwise |
| Recording state | Section 4.3 |
| Latest reading | Section 5 |

`claude auth status --json` prints no secret. kcap never opens Codex's `auth.json`; the app-server
reports the account itself.

**Refresh** runs on add, daemon start, explicit refresh, and after a limit hit — not on a timer,
since each refresh starts a vendor process. Its steps are independent:

1. **Identity probe.** An authoritative answer updates sign-in and identity. A failed probe (CLI
   missing, crash, timeout, unsupported method) sets nothing but a `lastRefreshError`, leaving the
   recorded identity and readings alone.
2. **Limits** (Codex only, `account/rateLimits/read`). Success merges a reading; failure records a
   limits error and touches neither identity nor sign-in.

The result is written to the account's registry entry under `accounts.lock`, so every daemon
publishes the same state. Every refresh stamps `refreshedAt` and bumps `revision` even when nothing
else changed, giving the app evidence that a refresh happened (Section 6.3).

**Overlapping refreshes.** A refresh records the account's `generation` and a per-account
`probeSeq` (incremented under `accounts.lock` when the probe starts) before calling the vendor. At
commit it applies its identity result only if no probe with a higher `probeSeq` has committed since;
otherwise it records only `refreshedAt` and bumps `revision`. Reversed completion order therefore
never restores an older login.

A refresh's limits belong to the identity its own probe saw, so both commit together: holding
`accounts.lock` and then `readings.lock`, the refresh applies its identity (retiring the old
generation and deleting its reading if the login changed) and then merges its limits tagged with the
generation that commit produced. A refresh whose identity was superseded by a higher `probeSeq` drops
its limits; a refresh whose probe failed merges its limits only if the generation is still the one it
started under. A single refresh that discovers login B therefore leaves B's identity and B's fresh
limits.

**Identity change.** When an authoritative probe reports a different identity than the one recorded
(another login in the same directory), the account's `generation` increments, its reading is deleted
under both locks, and the session ids already seen in that reading are kept as retired for the old
generation. Readings from a retired session are dropped (Section 5.1); app-server producers carry the
generation they launched under and are dropped when it no longer matches. A session that started
under the old login but had produced no reading before the change cannot be told apart and may
contribute until it ends — a stated limitation.

Retirement reaches every reader, not just local writers. The reading file records its `generation`;
a daemon building a snapshot omits a reading whose generation differs from the registry entry's, so
the interval between the registry update and the reading's deletion cannot publish the old login's
numbers under the new identity. The public account view carries `generation` independently of the
reading, and the server discards its stored reading and blocked marker for an account whose
generation changed before merging anything (Section 6.2).

### 3.3 Same login in two directories

Two directories of one vendor reporting the same identity (Claude: `orgId` + email; Codex:
`chatgptAccountId`) share limits. The screen shows them as one row listing both directories; readings
from either update it. Accounts with unknown identity never group. Per-directory actions — re-wire,
remove — act on a chosen directory, and the row's menu lists them by path.

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
`PluginCommand.InstallCodexHooks`, Codex MCP setup); `ClaudePluginInstaller` / `CodexHooksInstaller`
are detection and marker helpers, and `CodexHookTrust` lives in the daemon assembly. The daemon cannot
call the CLI's code. Part A moves install, uninstall and status logic into a Core service,
`AccountWiring`, taking an account's layout (`ClaudeHarness.Over(paths)` / `CodexHarness.Over(paths)`)
and returning a per-step result. Setup, `kcap plugin`, `kcap accounts`, uninstall, the npm refresh
path (`npm/kcap/bin/refresh.js` and the `plugin --if-installed` commands it runs) and daemons all call
it. Each wiring run holds `accounts.lock`, so concurrent setup, refresh and app actions serialize.

**Scope dispatch is preserved.** User-scope wiring iterates the registry. Project-scope operations
(`kcap plugin … --project <path>`) act on that project only and never touch registered accounts.
Per-vendor skip choices recorded by setup still apply to adoption and iteration.

Per account it does:

- **Claude:** register the marketplace and enable the kcap plugin in `<dir>/settings.json`. The status
  line wrapper is added in Part B (Section 5.2).
- **Codex:** the kcap hook in `<dir>/hooks.json`; MCP registration in `<dir>/config.toml` tracked in
  that directory's own `mcp-ownership-v1.json`, so "owns only what it created" holds per account.

**Skills.** Team skills materialize into each repository's own `.claude/skills` and `.agents/skills`,
which every account's sessions read; nothing changes per account. kcap's packaged skills reach Claude
inside the plugin, and reach Codex through the existing shared install into `~/.agents/skills`
(`AgentsSkillsInstaller`). That shared install stays as it is: adding a Codex account ensures it is
present, and removing an account never removes it — only uninstall does.

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
(after Part B) status line — before it stops daemons and removes kcap's config, and deletes the
registry last. A failure on one account is reported and leaves that account's entry, including its
status line backup, so a later uninstall can finish it.

### 4.5 `kcap accounts`

`kcap accounts` lists accounts with recording state; `add <vendor> <dir>`, `remove <id>`,
`rename <id> <label>`, `rewire [<id>]` and `refresh [<id>]` perform the actions of Section 6.3
directly against the registry. README and `help-*.txt` document it in the same PR.

### 4.6 Intermediate release

Part A ships without the status line wrapper or readings. Part B's wiring adds the wrapper; the npm
refresh's `plugin --if-installed` run after upgrading to Part B installs it in every registered Claude
account. A Part A uninstall has no status line state to restore.

### 4.7 Testing

- Each directory in a two-account `TempDir` gets plugin/hooks; MCP ownership is tracked per account;
  unwire removes only what we added.
- A new session under a non-default account records end to end, for Claude and for Codex.
- Plan capture under a non-default Claude account with `CLAUDE_CONFIG_DIR` absent from the hook's
  environment; Codex titles across two homes; import across two accounts.
- Recording state: missing Claude payload reports Broken; untrusted Codex hook reports Needs trust.
- Registry: a second writer entering between replacement and lock release is excluded; two named
  daemons and two `KCAP_CONFIG_DIR` roots see one list and one host id; uninstall with one account
  failing.
- Adoption: a pre-registry install upgraded through `plugin --if-installed` without setup; plugin
  install on an empty registry; `plugin remove --project` leaves every registered account wired;
  a skipped vendor is not adopted.
- Codex packaged skills: first-time Codex account; removing one of two Codex accounts keeps
  `~/.agents/skills`.

## 5. Part B — readings

### 5.1 Reading model

A reading holds windows keyed by `(limitId, windowLength)` — Claude has one limit with a 5-hour and
a 7-day window — each with used percentage, reset time and `confirmedAt`; plus plan and credits where
known, the account `generation`, and the session ids that contributed.

**Values are ordered by the data.** Usage within one window only grows until it resets:

- Incoming window with a **later** reset time than stored: a new window — replace.
- **Same** reset time: keep the higher used percentage.
- **Earlier** reset time: stale — ignore.

**Freshness is a separate question:** was this observation produced by a new vendor response, or is
it a replay of one already seen? Every producer classifies its observation before merging, and only a
fresh observation sets a window's `confirmedAt` — to the producer's time for that response, never to
a later receipt time.

| Producer | Fresh when | `confirmedAt` |
|---|---|---|
| Claude status line | The session's `cost.total_api_duration_ms` is greater than a baseline already recorded for that `session_id` — a new API response arrived since | Wrapper's clock at that run |
| Codex rollout | Every token-count event is one response | The rollout line's own `timestamp` |
| Codex app-server notification | Every `account/rateLimits/updated` | Receipt time in the runtime |
| Codex refresh | Every `account/rateLimits/read` response | Receipt time in the refresh |

**Claude baseline.** The first run seen for a session — after install, or after the counter history
is lost — only records `total_api_duration_ms` as the baseline. Its values are merged, but they are
not fresh: the session may be replaying a response from hours ago. A later run with a higher counter
confirms. A payload without the counter is never fresh. A window that has values but no
`confirmedAt` reads as "freshness unknown", which the Ready predicate treats as stale.

A replayed observation may still raise a percentage (ordering above), but never moves `confirmedAt`.
The reading keeps the last `total_api_duration_ms` per contributing Claude session for this check.
A fresh observation with an equal value moves `confirmedAt` forward; a replay with an equal value
changes nothing.

**Merging** is the same function everywhere — producer, daemon and server — and keeps the maximum
`confirmedAt` per window. Publishing, duplicate snapshots and reconnects carry producer times through
and never restamp them.

**Writing.** A producer takes `readings.lock` with a non-blocking attempt and a budget of at most
50 ms; on contention it skips the observation. Holding the lock it reads `accounts.json` (atomically
replaced, so readable without `accounts.lock`) and writes nothing if the account is gone, its
generation differs from the producer's, or the session is retired for the current generation (Section
3.2). Otherwise it merges and replaces the reading file atomically. Because removal and retirement
delete the reading while holding `readings.lock` after updating `accounts.json`, a writer either
commits before them (and is deleted) or sees their result (and skips); it cannot recreate a removed
or retired reading.

A window whose reset time has passed is shown as reset with unknown usage until a new observation
arrives. An account with no observation is "no reading", not zero. The reading file records its
`generation`, which every reader checks (Section 3.2).

### 5.2 Claude: `kcap statusline`

Claude Code passes the status line command JSON on stdin that, for Pro and Max subscribers, includes
`rate_limits.five_hour` and `rate_limits.seven_day`, each with `used_percentage` and `resets_at`
(epoch seconds), present only after the session's first API response, plus `session_id`. Updates are
debounced at 300ms and an in-flight run is cancelled when the next update fires.

**Command.** Wiring writes `statusLine.command` so that a missing kcap still runs the user's original:

```sh
if command -v kcap >/dev/null 2>&1; then exec kcap statusline --account <id>; else exec sh -c '<original>'; fi
```

`<original>` is never spliced into the guard's own syntax. It is passed as one argument to a nested
`sh -c`, quoted with POSIX single quotes (each `'` written as `'\''`), so comments, multiline commands
and heredocs in the original parse inside the nested shell exactly as they did on their own. With no
original the `else` branch is `:`. The wrapper's own step 3 below runs the original the same way:
`sh -c <original>` as an argument vector, no string composition. Claude runs status line commands
through Git Bash or a POSIX shell where those exist; on Windows without Git Bash, where
Claude uses PowerShell, the plain `kcap statusline --account <id>` is written and a missing kcap
blanks the status line — stated, not hidden. The account id is in the command because the wrapper
cannot rely on `CLAUDE_CONFIG_DIR`, which `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` removes.

**What is owned.** kcap owns `statusLine.command` (and `type`) only. `padding`, `refreshInterval`,
`hideVimModeIndicator` and any other property stay the user's: never copied, never restored.

**Backup before replacement.** Before changing `settings.json`, wiring stores the original command
(or "none") in the account's registry entry under `accounts.lock`. Only then is `settings.json`
rewritten. An interrupted install leaves either the original command with a backup (re-wire completes
it) or our command with a backup (done). Backups and the original command are local bookkeeping: they
never appear in a published snapshot (Section 6.1).

**Settings writes are atomic.** Every write to a vendor settings file — this one, and the plugin,
hook and MCP writes of Section 4.1 — reads the existing file, edits only the keys kcap owns, and
replaces it atomically, never with `File.WriteAllText`. The atomic writer is permission-aware: the
temporary file is created with the destination's existing mode, or with the mode the caller requires
(Codex's `config.toml` writer creates 0600 today), before any byte is written, then renamed over the
destination. `AtomicFile.Replace` gains this option; the existing owner-only Codex test
(`RegisterKcapMcpServers_writes_owner_only_files_on_unix`) stays and passes through the refactor. An existing file that does
not parse is never overwritten or reset to an empty object: wiring refuses that account, reports it
Broken with "settings file unreadable", and leaves the file as it was. An interruption at any point
therefore leaves either the old file or the new one, with the user's other settings intact.

**Re-wire.** If `statusLine.command` is ours, nothing changes. Otherwise the user changed it: back it
up as the new original, then reinstall ours. Our own command is never adopted as an original.

**Unwire.** While `statusLine.command` is still ours: put the original command back, or, if there was
none, remove `command` and `type` — and the `statusLine` object if nothing else is left in it. If the
user has replaced our command, leave everything and discard the backup. A missing or corrupt backup
with our command installed removes `command` and `type` and reports it, rather than guessing.

**Startup.** `kcap statusline` is dispatched before any of the CLI's ordinary startup: no repository
or profile resolution, no git call, no telemetry, no server-URL gate, no update notice. It reads the
registry and may write one reading file, nothing else.

**Each run.**

1. Read stdin to end.
2. If `rate_limits` is present and the account is registered, merge the reading (Section 5.1) with
   `session_id` as the contributing session. Any failure or lock contention is swallowed.
3. If the backup holds an original command, run it as `sh -c <original>` (on Windows without Git
   Bash, `powershell -NoProfile -Command <original>`), with the same stdin bytes, environment and
   working directory, and pass its stdout, stderr and exit code through.
   With no original, print nothing and exit 0.
4. If the wrapper itself is cancelled, the original's process tree is killed with it.

A project's own `statusLine` overrides the account's; those projects give no readings.

### 5.3 Codex

- **PTY sessions:** the rollout watcher already tails each rollout. Token-count events carry
  `rate_limits` (`limit_id`, `primary` / `secondary` with `used_percent`, `window_minutes`,
  `resets_at`, `plan_type`, `credits`). The watcher merges them into the reading of the account
  whose `sessions/` holds the rollout, with the session id as contributor.
- **App-server sessions** (hosted reviewers and opted-in interactive launches, which suppress the
  watcher): the runtime handles `account/rateLimits/updated` notifications and merges them into the
  reading of the account whose `CODEX_HOME` launched it — the daemon's default account until spec 2
  adds per-agent homes — carrying the generation it launched under.
- **Idle accounts:** refresh (Section 3.2) starts `codex app-server` with that account's
  `CODEX_HOME` and calls `account/read` and `account/rateLimits/read`. Codex makes the authenticated
  call; kcap never sees a token.

All three are side outputs. The transcript projection does not change, so canonical event ids are
untouched, and app-server sessions get no second transcript source.

### 5.4 Limit hits

A "blocked" marker on an account's reading comes from two sources:

- **Claude:** the hosted-agent usage-limit detector (Claude PTY agents only). The daemon queues —
  outside the PTY read loop — the marker on the reading of the account that agent runs under (the
  daemon's default Claude account until spec 2), plus a refresh. The detector yields text only and
  cannot say which window caused the block.
- **Codex:** a fresh observation (Section 5.1) whose `rate_limit_reached_type` is non-null, from any
  Codex producer. It names the exhausted window when it maps to `primary` or `secondary`.

Rules, for both:

**The marker is a set of constraints, not one deadline.** Each signal adds a constraint: the window
it names with that window's reset; otherwise every window at or above 100% with its reset; otherwise
one "unknown" constraint. Constraints accumulate — a later signal naming the primary window never
replaces a secondary-window constraint already present. **Blocked until** is the latest reset among
the constraints, or "unknown" if any constraint is unknown.

**Evidence is ordered by producer time.** The marker carries `blockedAt` (the newest signal's time:
detection time for Claude, the rollout line's `timestamp` or receipt time for Codex) and `clearedAt`
(the newest clearing evidence's time). The account is blocked while `blockedAt > clearedAt` **and** at
least one constraint is active: an unknown constraint, or a window constraint whose reset is still in
the future. Expiry is evaluated against the clock wherever the predicate is read — producer, daemon,
server, app — so it needs no transition and no new evidence: once the last window constraint's reset
passes with no unknown constraint, the account reads unblocked, and replaying an older snapshot only
re-adds constraints that are already expired.

- A window constraint lapses when its reset time passes. An unknown constraint never lapses by time.
- **Clearing evidence** is a fresh observation (Section 5.1) whose producer time is later than
  `blockedAt`, that covers every window the account has a reading for, shows all of them below 100%,
  and (Codex) has a null `rate_limit_reached_type`. It sets `clearedAt` to that time and empties the
  constraints. An older observation — a replay, or a rewound Codex rollout event with its original
  timestamp — never clears, whatever it shows; neither does a partial observation.
- An explicit signal outranks percentages: a marker is set even when cached percentages read below
  100%.
- `blockedAt`, `clearedAt` and the constraint set are persisted in the reading and merged the same way
  everywhere — producer, daemon, server: maximum `blockedAt`, maximum `clearedAt`, union of
  constraints newer than `clearedAt`. Snapshots delivered in any order therefore converge, and a
  cleared block cannot be reintroduced by an older snapshot, nor a newer block erased.
- **Next reset** on the screen stays the earliest reset of any window; it is not "unblocked at".

### 5.5 Testing

- **Wrapper:** reading merged from stdin, including `rate_limits` absent and one window only; the
  original runs with identical stdin, environment and working directory and its output and exit code
  pass through; no original prints nothing; a held `readings.lock` skips capture and the original
  still prints promptly; a slow original is killed with the wrapper on cancellation; with kcap absent
  from PATH the guarded command still runs the original; the guard passes `sh -n` and runs the
  original correctly when it holds a trailing `#` comment, single quotes, multiple lines or a heredoc.
  Run against the real executable with no server config and an unreachable network, and assert no
  network call.
- **Install / re-wire / unwire:** exact `settings.json` round-trip; command edit then re-wire;
  command edit then unwire keeps the edit; a `padding` edit with our command still installed survives
  unwire; interrupted install at each step, including a kill during the settings file write itself,
  with unrelated settings asserted intact; a malformed existing settings file is left untouched and
  the account reports Broken; Codex `config.toml` stays 0600 after an atomic rewrite (the existing
  owner-only test).
  Claude baseline: installing the wrapper on an idle existing session leaves its windows "freshness
  unknown"; unchanged repeats after the baseline stay unknown; the next API-duration increase
  confirms; a payload without the counter is never fresh.
- **Readings:** a repeated Claude payload with an unchanged `total_api_duration_ms` does not move
  `confirmedAt`, while a new response with an equal value does; a fresh equal-valued Codex response
  moves it; a lower value with the same reset is ignored; a new window replaces; several `limit_id`s
  coexist; duplicate daemon snapshots and a reconnect replay never restamp `confirmedAt`; a writer
  paused after validating membership while removal runs does not recreate the file; a retired
  session's late observation after an identity change is dropped.
- **Identity:** a daemon building a snapshot between the registry update and the reading's deletion
  publishes no reading; a retired account with no reading still publishes its new generation; a new
  login with lower usage at the same reset time replaces the old login's higher value on the server;
  two overlapping refreshes across a login change, completing in reverse order, leave the newer login. A single refresh discovering login B leaves B's identity and B's fresh limits.
- **Codex:** rollout fixtures (primary only, both windows, several limit ids, credits, limit reached
  with and without a matching window, and a limit-reached event while cached percentages are below
  100% — each with its expected blocked state and Ready result);
  `account/rateLimits/updated` in an app-server session; refresh of an idle account against a stub
  app-server, including `account/read` success with limits failure and an unsupported method.
- **Limit hit:** block before any reading; two exhausted windows with different resets; a partial
  newer observation that must not clear the block; an unknown-reset block that survives time passing
  and a replayed all-below observation; clearing on the last reset and on a fresh, complete all-below
  observation; successive signals naming the primary then the secondary window, and the reverse,
  where passing the earlier reset does not unblock; block and clear snapshots delivered to the server
  in reverse order; a rewound older Codex all-below event arriving after a newer block. Time advancing past
  the last known reset with no new vendor response unblocks; an older snapshot replayed afterwards
  keeps it unblocked; an unknown constraint alongside expired window constraints keeps it blocked.

## 6. Part B — publishing and the screen

### 6.1 Daemons

Every daemon watches `accounts.json` and the readings directory (debounced, a few seconds) and
publishes a snapshot built from a **public account view**: id, vendor, directory, label, identity
(email, org, plan), `generation`, sign-in, recording state, `refreshedAt`, refresh errors, reading. Backups, original
status line commands and other wiring bookkeeping are never in it — they are arbitrary user shell text
and stay on the host.

Each snapshot carries the host id, the registry `revision` it was built from, and the full account
list, so a missing account means removed. Readings carry their windows' reset times and
`confirmedAt`, which already order them (Section 5.1).

Account actions requested by the desktop app go through a daemon, which calls `AccountWiring` under
`accounts.lock`.

### 6.2 Server

- The server advertises an `accounts` capability; a daemon sends account data only to a server that
  advertises it, so an older server never receives a method it cannot handle.
- `DaemonConnect` gains an `Accounts` member, appended last so older servers keep binding.
- `AccountsChanged` carries the full snapshot, throttled per daemon to one call per 30 seconds with a
  trailing send, so the last change in a burst — including a removal — always goes out.
- **Identity retirement.** When an account arrives with a higher `generation` than stored, the server
  discards its stored reading and blocked marker before merging the snapshot's reading, and ignores
  readings whose generation is lower than the account's.
- **Ordering across daemons.** The server keeps, per host, the highest registry `revision` applied,
  and rejects a snapshot with a lower revision whichever daemon sends it. Within an accepted snapshot,
  each reading window is merged by the rules of Section 5.1, so an older reading from another daemon
  cannot move a window backwards.
- The server persists the latest state per host and account so an offline host still shows its last
  state after a server restart, and serves it **only to the host's owner** — reads and subscriptions
  alike. Email and org name are personal data.
- Matching kcap-server change: storage, revision gate, owner-scoped read and subscription for the
  desktop app, and the capability.

### 6.3 Local app

Four appended local-socket frame types, each with encode and decode in `FrameCodec`, advertised in
`LocalControlCapabilities.Current` beside their handlers:

- **AccountsSubscribe** — app to daemon; the daemon answers with a snapshot and then pushes changes.
- **AccountsSnapshot** — daemon to app: host id, registry `revision`, public account views.
- **AccountAction** — app to daemon: request id, one of `add` (vendor + directory), `remove`,
  `rename` (label), `rewire`, `refresh`, and the account id.
- **AccountActionResult** — daemon to app: request id, terminal disposition (`succeeded` / `failed`
  / `refused`), per-step results, and the registry `revision` the action committed at (absent when
  nothing was committed).

In the app, account actions enter the existing app-lifetime mutation lane as a new verb carrying the
action and account id. Two actions on the same account never coalesce; actions on different accounts
queue independently. **Success requires both** a `succeeded` disposition with every step succeeded,
**and** a snapshot whose `revision` is at least the committed revision. A failed step is a failure
even when the row already looks right; an unchanged refresh still commits a revision (Section 3.2), so
it has evidence too. A disconnect before the result is unknown, never success: the lane reports it and
the next snapshot shows the true state. A daemon that does not advertise the frames gets no account
UI; actions are disabled with "update kcap on this host".

### 6.4 Accounts screen

- **Accounts** page with a host switcher. This host is editable; remote hosts are read-only and say
  where to make changes.
- Summary cards:
  - **Ready for a long run:** accounts that are Recording, signed in, not blocked, and whose every
    window was confirmed within the last hour and is under 90%. No reading, or any stale window, is
    unknown, not ready.
  - **Next reset:** the earliest reset across all windows.
  - **Need a fix:** signed out, Needs trust, Broken, or Not wired.
- Rows grouped by vendor. Each row: label and email, plan badge, one bar per window labeled from its
  actual length ("5h", "Week", or the minutes Codex reports), reset countdown, "+N per model" for
  extra limit ids, reading age, recording and sign-in state, refresh errors, and on this host a menu
  with refresh, re-wire, rename and remove.
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
| Vendor reports signed out | "Needs sign-in" |
| Identity probe failed | Last known identity, with the refresh error |
| Limits request failed | Last reading, with the refresh error |
| Reading file unreadable | Previous reading kept |
| Host offline | Server's last state, "host offline · last seen …" |
| Account directory deleted | "Directory missing", with remove in the menu |
| Daemon too old for account frames | Read-only, "update kcap on this host" |

### 6.6 Testing

- **Daemon:** a registry or reading change appears in the snapshot within the debounce; trailing
  throttle sends the last change of a burst; the published view contains no original command (a
  secret-like marker in an original never appears in any serialized payload).
- **Server contract:** old daemon / new server and new daemon / old server; another user's read and
  subscription denied; disconnect with a pending update; removal propagation; server restart with an
  offline host; two daemons on one host sending in reverse order, including a removal from one
  followed by the other's older snapshot; two config roots publishing under one host id.
- **Local IPC:** frames advertised only with handlers; action refused by an old daemon; queued actions
  on one account do not coalesce; a failed re-wire on an already-Recording account and a failed rename
  to the current label both report failure; an unchanged refresh succeeds on its revision; an
  unrelated snapshot does not confirm an action; a disconnect before the result reports unknown.
- **App:** headless view-model tests for window labels, thresholds, reading age, reset windows, the
  ready predicate (no reading, one stale window beside a fresh one, blocked, expired windows,
  per-model exhaustion), host offline, missing directory, and remote read-only.

## 7. Not doing

- Reading, copying or brokering any vendor credential.
- Calling a vendor usage endpoint ourselves; vendor processes make their own calls.
- Publishing original status line commands or wiring bookkeeping off the host.
- Recording an account directory nobody listed.
- Changing accounts on a remote host from the app.
- Choosing an account at launch, per-agent config directories, or switching accounts on a limit —
  spec 2.
