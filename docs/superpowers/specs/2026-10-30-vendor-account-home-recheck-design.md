# Re-check each account home's login in the background

## Intent

A session's account label and its usage bars must describe the same login. Today they can disagree for a
day or more. The bars come from what the running sessions report, which is the login they are actually
using. The label is the identity the daemon recorded when it last probed the home, copied onto the session
at launch.

What happened: `~/.claude` was probed at daemon start as `azimarev@gmail.com`. The user then ran `claude
auth login` as `alexey@ubiquitous.no`. A day later every session, including ones started after the switch,
still read `azimarev@gmail.com`, and its bars showed the new login's 3% / 8% instead of the old login's 80%
weekly. Nothing re-probes the home until the next daemon start or a manual `kcap accounts refresh`, and
the session keeps its launch-time copy of the label.

The account home is the unit. A harness is started with a home (`CLAUDE_CONFIG_DIR` / `CODEX_HOME`, or the
vendor default). The login belongs to that home, and every session started with it shares that login. The
account id stays the handle the app looks things up by. The checks run against the home.

## Goals

1. The daemon re-probes every registered account home on a slow timer, in the background, through the
   existing `AccountRefresher` lane.
2. When a probe finds a new login in a home, that home's registry row takes the new identity in place:
   same account id, new email/org/plan, and a label that follows the email unless the user set one.
3. Every session started with that home shows the home's current label: local and remote sessions in the
   desktop app, and the daemon's own status and heartbeat reports.
4. A detected switch starts a fresh reading for the new login. Every live session in the home keeps
   recording into it. No session is fenced off.

## Non-goals

- Fast detection. The user asked for a slow probe. File watching on `.claude.json`, `.credentials.json`
  or `auth.json` is out.
- Homes not in the registry (the daemon's unregistered default). They have no recorded identity and show
  the directory name, so nothing about them goes stale. *My call.*
- Sessions started from a terminal (hook-only). They carry no account to the app today, and this change
  does not add one.
- Switching a running session to a different home.
- A server change. That is Part 2, below, built and shipped separately.

## Anti-goals

- A probe more often than the existing 10-minute per-account cooldown, or a check that starts a vendor
  process per session. Several sessions in one home cost one probe.
- Bars that freeze after a re-login. That is what today's retirement rule would do (see below), and it is
  worse than the stale label.
- The label and the bars resolved from different sources again.

## Behaviour

### Periodic probe

- Interval: 10 minutes, the same as `Cooldown`. A home is due once the last refresh, or the start of a
  refresh still running, is at least one interval old, by whichever daemon did it.
- The registry is shared by every named daemon of the user (`AccountStore.Beside(daemons)`), and each
  daemon has its own refresher. The budget is therefore shared through the registry, not through any
  one refresher's memory. *My calls:*
  - A refresh records when it began (`probe_started_at`) in the same registry mutation that takes its
    `ProbeSeq`.
  - A periodic refresh checks that the home is due inside that same mutation, and stands down if it is
    not. Two daemons ticking at the same moment therefore cannot both probe.
  - The timer ticks every 2 minutes and queues every registered account. The registry-side check decides,
    so probes land 10 to 12 minutes apart however many daemons run.
- Explicit, limit-hit and wiring requests keep today's path and per-refresher cooldown. They do not go
  through the due check.
- The probe is the existing one, run against the account's `Directory`:
  - Claude: `ClaudeIdentityProbe` (`claude auth status --json`).
  - Codex: `CodexAccountProbe`, which also refreshes limits for idle accounts, the same as at daemon start.
- An account removed between ticks is skipped (`RefreshAsync` already returns when it is gone). An account
  added between ticks is picked up on the next tick, besides the probe `accounts add` already runs.

### What a detected switch does

`AccountRefresh.Commit` keeps detecting a switch with `AccountIdentity.Differs` and keeps bumping
`Generation`. The reading for the old generation is still deleted, because its numbers belong to the old
login.

It stops retiring sessions. Retirement assumed a session keeps the login it started with. In a shared home
that is false: this design session started after the switch, on the new login, and its id was in the
home's reading alongside about 50 others from before and after. A late commit that retires all of them
blocks the sessions that are already on the new login. Nothing can tell the two groups apart.

- No new ids are added to `RetiredSessions`.
- `ReadingStore.TryRecord` stops refusing a session for being retired.
- The persisted `retired_sessions` field must still parse in registries written by older builds. The
  member is deleted, and the registry's JSON context ignores unknown members. *My call.*
- The switch records `generation_started_at`: when the committing probe began. It is saved in the same
  registry write as the generation bump, so no crash can separate the two.

### Producers record against the home's current generation

Each producer must record into the home's current reading, not stay fenced by a generation captured long
before.

- `ClaudeStatusLineEntry` already reloads the registry on every render and writes with the current
  `Generation`. No change.
- `AccountReadingSink`: hosted Codex app-server limits and the Claude PTY limit-hit detector post with
  `ResolvedAccount.Generation`, frozen at launch. After a switch, that generation is refused forever. The
  write must use the account's current generation.
- `CodexRolloutReadings` captures the `VendorAccount` once per watcher. It must resolve the generation per
  write, or refresh its captured account when the registry revision moves.

Where the generation comes from is left to the builder. Options include `TryRecord` taking "current", the
producer re-reading the registry, or the sink resolving at write time. Write with the generation current
when the observation arrived, not when the session launched.

`ResolvedAccount.Generation` has no reader besides the reading fence. Drop it if nothing else needs it.

### Old-login numbers must not reach the new reading

The fence existed so a value observed under the old login is never written into the new login's reading.
With retirement gone, two paths can still deliver one. Both get their own guard. *My calls.*

- **Claude status-line replay.** A Claude session's status-line payload can replay its last response,
  which may be hours old (`ClaudeStatusLineReading`). A session idle since before the switch still holds
  the old login's `rate_limits`, and its next render sends them. `ReadingMerge.MergeWindow` keeps whichever
  window resets later, so one such replay can stick over the new login's fresh numbers for up to a week.
  - Guard: once a home has switched (`generation_started_at` is set), a status-line payload merges its
    windows only when it is fresh, meaning its `total_api_duration_ms` moved past the baseline the
    current reading holds for that session. Otherwise it only records the baseline.
  - That covers every case: a session with no baseline, a counter that went backwards, a missing counter,
    a baseline evicted by the 64-session cap, and a reading lost to a crash after the registry save. None
    of them counts until the session makes an API call after the switch.
  - A fresh payload follows a real API call made after the switch, so it belongs to the home's current
    login.
  - The cost is one render for a session started after the switch: its first payload records a baseline,
    and its next one, after its first response, counts.
  - A home that never switched keeps today's behaviour, where a session's first payload merges
    unconfirmed.
  - Tests pin:
    - an idle pre-switch session's replay leaves the new reading's windows untouched, and the same
      session's next fresh payload records;
    - a counter reset (1000 → 200 → 200 → 300): only 300 records;
    - an evicted baseline and a missing counter;
    - a producer that commits just before the switch, then replays, then advances, run through the real
      `AccountRefresh` and `TryRecord` interleaving.
- **Codex rollout backlog.** `CodexRolloutReadings` stamps an observation with the rollout line's own
  timestamp. A watcher catching up on lines written before the switch would otherwise record them into
  the new reading.
  - Guard: an observation produced before `generation_started_at` is dropped. This applies to every
    producer that carries a `ProducedAt`, not only the rollout watcher.
- What remains is a race inside the seconds between a fresh observation and its write, across a commit.
  It is accepted.

### Labels follow the home

- **Desktop app.** `ChatTabViewModel` resolves the label from the accounts snapshot row whose `Id` equals
  the session's `AccountId`. That is the same row `StripUsage` already finds, through the same
  `_accountIds` × `accountReadings` stream. It covers local sessions (the `AccountsSubscribe` snapshot)
  and remote ones (`DaemonAccountsDto`), because `AccountReadings` already merges both.
  - Fallback when there is no row: the session's own `AccountLabel`, then the directory's last segment, as
    today. No row means an unregistered home, a snapshot not yet loaded, or a remote host whose accounts
    the app cannot draw (`AccountReadings.Remote` serves only `DrawableDaemonAccounts`).
  - On such a remote host there are no bars either, so the label and the bars cannot disagree. The label
    there comes from the server. Making it current is Part 2.
  - The label and the bars must change together. Derive both from one lookup and swap them as one value
    before raising any notification. A listener must never see the new label with the old bars, or the
    reverse.
- **Daemon reports.** The daemon fills `AccountLabel` from the registry's current label for `AccountId`
  when it builds `AgentStatusDto` (live rows and the resume ledger's `LedgerStatusRow` alike),
  `LiveAgentInfo` (heartbeat) and a re-registration. It falls back to
  `ResolvedAccount.Label` when the id is not in the registry. That keeps `kcap agent status` and the
  server's live view honest without a server change.
  - `ResolvedAccount` itself stays as resolved at launch. The resume ledger keeps the launch label; on
    resume it is resolved again anyway.
  - What the server does with the heartbeat label is Part 2.

### Publishing

No new wire shape. A committed probe already bumps the registry revision. `AccountsPublisher` already
rebuilds the snapshot on that, sends `AccountsSubscribe` locally, and sends `AccountsChanged` to the server
(throttled to 30 s). The app's lookup by id picks up the new row from there.

## Constraints

- The daemon never stops a process in its own group, and the probes already go through the
  `VendorProcess` plan that sets or removes the home variable. Reuse it unchanged.
  - For the default `~/.claude` the variable is removed, so `.claude.json` is read from `~/.claude.json`.
    `AccountLayouts` explains why. The periodic probe inherits that.
- Probes never run concurrently: there is one `AccountRefresher` consumer. Keep it that way.
- `ProbeSeq` ordering stays. A slow older probe must never commit over a newer one.
- Tests use `FakeTimeProvider` for the timer, and the existing fake probes for the identity change.
  Existing tests that assert retirement change meaning and must be rewritten, not deleted silently. Each
  one should now pin "a switch does not fence a live session".
- Comment rules in `CLAUDE.md` apply. In particular, comments must not narrate the old retirement
  behaviour.

## Part 2: the server's session rows (kcap-server, separate)

Everything above is Part 1, in this repo. Part 2 is about remote sessions whose host's accounts the app
cannot draw, and about web surfaces. For those, the label shown is the one the server holds for the agent
row (`AgentInstanceDto.account_label`), set at registration.

- Part 1 makes the daemon send the home's current label in every heartbeat (`LiveAgentInfo.AccountLabel`).
  `DaemonReportExtensions` maps it into the server's `LiveAgent`.
- Part 2 first checks whether the row the app and the web read follows that live value or keeps the
  registration value. If it keeps the registration value, Part 2 makes it follow the heartbeat.
- Part 2 depends on Part 1's heartbeat change. Part 1 does not depend on Part 2.

## Accepted limitation

A vendor process that keeps its old credentials in memory after a re-login would report the old login's
limits under the new label until it restarts. For Claude that does not match what we observed (sessions in
a shared home report the home's current login). Codex has not been observed either way. If it turns out
to hold credentials per process, the fix belongs with Codex, not with re-fencing every session.

## Left to the builder

- How producers obtain the current generation (above).
- What happens to stored `retired_sessions` (above).
- Whether the periodic tick skips accounts with a `RefreshError` that recurs, beyond what the cooldown
  already limits.
- Test layout: refresher timer tests in the daemon suite; commit and reading-store tests in the Core
  suite; app label tests in the ViewModel suite.
