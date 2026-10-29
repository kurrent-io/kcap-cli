# Vendor accounts: usage bars

GitHub #1957 (Linear AI-4416), slice 5 of the vendor accounts work (#1806). It builds on two approved
specs and does not restate them:

- `2026-10-07-vendor-accounts-and-limits-design.md` — Part B readings (5.1) and the Accounts screen (6.4,
  6.5, 6.6).
- `2026-10-21-vendor-accounts-launch-choice-design.md` — the launcher rows (6.1), the Settings tab (7)
  and the composer strip (8).

Mockups: https://kurrent.kcap.ai/pages/572543741e05450f8f6510f4109f2731

## 1. Intent

Slice 4 records each account's usage reading and delivers it to the app (`AccountViewDto.Reading`, the
identity fields beside it, locally over `IAccountsService.Snapshot` and remotely through
`DaemonAccountsMapping.ToSnapshot`). Nothing draws it. This slice draws it in the three places, so a
person can tell before starting a long run which account has room, and see in a running session how
close its account is to a limit.

**Success:** one glance at the launcher's Account section answers "which account should this run use";
the composer strip answers "is this session about to hit a limit"; the Settings tab answers "which
accounts need me".

**Anti-goals:** a bar that reads as 0% when nothing is known; a bar drawn from a reading of a host that is
not connected; identity (labels, Default, plan) in a status colour; two surfaces disagreeing about the
same account.

## 2. One presentation model

Every surface reads one pure projection of an `AccountViewDto` and a clock, so the three can never
disagree and all of it is unit-testable without a view. Its rules:

- **Window label** from the window's length in minutes: 300 → "5h", 10080 → "Week"; other whole days →
  "Nd", other whole hours → "Nh", anything else → "Nm".
- **Primary windows and "+N per model".** Windows whose limit id is the account's main limit get bars;
  the count of distinct other limit ids shows as "+N per model". The main limit is the account-wide one:
  `ClaudeStatusLineReading.LimitId` ("claude") and `CodexRateLimits.DefaultLimitId` ("codex").
- **Ordering:** bars sort by window length, shortest first.
- **Thresholds:** below 75% neutral; from 75% warning (`KcapWarning*`); danger (`KcapDanger*`) when the
  account is blocked on that window, or the window reads 100%.
- **Expired window:** once `ResetsAt` has passed, the window shows "reset · usage unknown" with an
  unknown (hatched, not empty) bar and no percentage, until a new observation replaces it.
- **Reset text:** a countdown under 24 h ("resets in 2h 14m", "resets in 12m"), otherwise a day and
  local time ("resets Thu 09:00"). A window without `ResetsAt` shows no reset text.
- **Next reset** (launcher rows, strip, summary card): the earliest future `ResetsAt` among the primary
  windows.
- **Reading age:** "updated 3m ago" from the latest `ConfirmedAt` across windows; a reading with windows
  but no `ConfirmedAt` reads "freshness unknown".
- **Usage line, by precedence.** The first case that matches decides what replaces or carries the bars.
  The Limit hit pill comes from the blocked rule below and is independent of the line. The first four
  rows, where usage is missing or unknown, are never ready and never draw a bar. In the last row, Ready
  is decided by the Ready predicate below.

  | Reading | Usage line | Pill |
  |---|---|---|
  | null | "no reading yet" | none |
  | no windows and not blocked (empty, or metadata only) | "no reading yet" | none |
  | no windows and blocked (a limit hit before any window) | "usage unknown" | Limit hit |
  | windows, none of them primary (per-model only) | "usage unknown · +N per model" | Limit hit if blocked |
  | at least one primary window | the bars, then "+N per model" when there are others | Limit hit if blocked |

  Missing or unknown usage is never drawn as 0%. A window the vendor actually reports at 0% is drawn as
  0%.
- **Blocked** (user decision): only a live constraint on the main limit id, or one with no limit id,
  blocks the account. A live constraint on a per-model limit id does not block; it shows beside the
  per-model count ("+2 per model · 1 hit") and does not affect Ready. "Live" keeps
  `ReadingMerge.IsBlocked`'s rules (a marker blocked after its last clear; a constraint with a null
  `ResetsAt` never lapses by time); write the account-level predicate as that rule restricted to
  main-or-null constraints, sharing its lapse logic rather than restating it. The blocked windows are
  those matching a live main-limit constraint's window length; a constraint with no window length blocks
  the account without colouring a particular bar.
- **Clearing stays as `ReadingMerge` does it** (my call). The marker has one shared `ClearedAt`, and
  `Clears` needs every observed window below 100%, per-model ones included. So a live main or null-id
  constraint stays live while a per-model window is exhausted, even if the main windows look healthy.
  That is conservative: a null-id hit may well be that per-model limit, and the account is shown blocked
  until a complete response clears it or the constraints lapse. No merge or wire change.
- **Blocked until** (the pill's reset): the latest `ResetsAt` across the live main-or-null constraints,
  with an unknown reset dominating, like `ReadingMerge.BlockedUntil` restricted to those constraints. With
  a finite deadline the pill reads "Limit hit · resets in …"; with an unknown one it reads "Limit hit"
  alone. Signal order does not matter: a week constraint and a later five-hour one block until the week
  resets. This is separate from **Next reset**, which stays the earliest window reset.
- **Ready for a long run** (the first spec's 6.4): Recording, signed in, not blocked, at least one
  primary window, and every primary window confirmed within the last hour, not expired and under 90%.
  Anything unknown is not ready.
- **Need a fix:** signed out, Needs trust, Broken or Not wired. "Directory missing" is not published by
  any host today and is left out of this slice (my call).
- **Summary cards count registered accounts only** (my call): the unregistered default row has no
  reading and would otherwise put "Need a fix: 1" in front of most people.
- **Ticking:** one app-wide timer, about every 30 s, re-projects so countdowns, ages, expiry and the ready
  predicate move between snapshots. The projection takes the time as an argument; only the view-model
  layer reads the clock, through the app's existing time abstraction (`TimeProvider` if the app already
  injects one).

## 3. The three surfaces

As in the mockups.

**Settings, Accounts tab.** Summary cards above the vendor groups: Ready for a long run ("1 of 4" and
the ready labels), Next reset (countdown and which account and window), Need a fix (count and the first
account with its reason; the count takes the warning colour when non-zero). Each row adds, beside the
label and Default badge, a neutral plan chip and the email in muted text; then one bar per primary
window (label, bar, percentage, reset); then a footer with "+N per model", the reading age, and the last
refresh or limits error as a warning hint. A blocked row shows the danger pill with its reset beside the
label. The row menu gains **Refresh**, which runs `kcap accounts refresh <id>` through the mutation lane
like the other verbs (a new `MutationVerb`; `kcap accounts refresh <id> --json` already emits the
steps-and-revision result the lane confirms against). The unregistered default
row has no reading and no Refresh.

**Launcher, Account section.** Each row gains a third line: one mini bar per primary window with its
percentage, then the next reset. A blocked row shows a compact "Limit hit" pill beside the label. A row
without a reading shows "no reading yet" on that line. Search still does not match accounts, and
rebuilding the open panel stays tied to a rendered-state change (a tick that changes no text redraws
nothing).

**Composer strip.** Glyph and label, then one mini bar per primary window with its percentage, then the
next reset. Before any reading it keeps the directory and adds "no reading yet". Blocked: the exhausted
window's bar is danger-coloured and a danger pill reads "Limit hit · resets in …". The existing
usage-limit notice inside the composer is unchanged. The strip finds its reading by the session's
`AccountId` in its host's snapshot: the local snapshot for a local session, the server's daemon
accounts read for a remote one. An unregistered default (no `AccountId`) has no reading and shows the
directory only, without "no reading yet".

## 4. Only a connected host's reading is drawn

Remote launch targets are already only connected daemons (`GetConnectedDaemonsAsync`), and the server's
accounts read only covers connected daemons. The app, though, keeps what it fetched:
`RemoteAgentsService` retains its daemon accounts across lane loss and failed fetches (a test pins
that, and the launcher's account choice relies on it), and accounts refresh on `DaemonAccountsChanged`
while membership refreshes separately on `DaemonsChanged`. So a server that only answers about
connected daemons does not make the app's copy connected-only. The gate is applied where readings are
drawn, not by dropping the retained copy:

- **A remote host's reading is drawable** only while all of these hold:
  - the server lane is Connected;
  - the daemon is in the current `DaemonListing` (non-null, fetched after the latest connect);
  - its `DaemonAccountsDto` was fetched successfully **after the daemon's current incarnation was first
    seen**.

  A daemon's incarnation is its listing entry's identity: its owner and name (names are unique only per owner) together with `ConnectedAt` and
  `MachineId` from `DaemonInfo`. Each of these starts a new incarnation and closes that daemon's gate:
  a server reconnect, the daemon leaving the listing, and a listing entry whose `ConnectedAt` or
  `MachineId` changed (a restart, or another host taking the name). The gate reopens only on an accounts
  fetch that both started and succeeded after that transition. **Observing a new incarnation schedules
  such a fetch.** Listing and accounts requests run independently, and accounts otherwise refresh only
  on `DaemonAccountsChanged`. So, without that trigger, an accounts fetch that started before the
  listing returned, or a rejoin push handled before the listing update, would leave an idle daemon
  without bars for good. When a fetch is already in flight, one more is coalesced to run after it, and a
  failed one retries on the existing listing backoff. Concretely: per daemon name,
  `RemoteAgentsService` records the incarnation and a sequence number of the last successful accounts
  fetch, and the gate compares them. The retained accounts themselves stay as they are, so the
  launcher's account choice keeps working. Lane loss, a listing without the daemon, a reconnect before
  a fresh accounts fetch, a leave and rejoin within one server connection, and a host replacing another
  under the same name with a failed refetch all make the reading not drawable at once, without waiting
  for the next fetch.
- **The local host's reading is drawable** only while the local daemon is attached and the accounts
  subscription has delivered a snapshot since that attach. `AccountsService` already clears its snapshot
  on detach. Pin that in a test.
- **Launcher:** bars appear only on rows of a drawable host. When the gate closes while the panel is
  open, the bars line goes away. The section itself keeps its existing pending or offline behaviour.
- **Settings:** this machine only, as today. With the daemon down, the tab keeps its "listed once the
  daemon is connected" notice and draws no bars.
- **Composer strip, session whose host is not drawable:** glyph, label and directory only. No bars, and
  no "no reading yet", which would claim something unknown.
- Tests drive the transitions through the services' real caches, not an account list that already
  omits the host: lane loss with accounts retained, daemon leaving the listing with accounts retained,
  reconnect before the accounts refetch, a failed accounts fetch after the daemon left, a leave and
  rejoin within one server connection with a failed refetch, and another host taking the daemon's name
  (changed `MachineId`) with a failed refetch; and the gate reopening after a successful fetch, with the accounts fetch finishing before and after the listing, on first connect and after a leave and rejoin. Each
  transition is tested for both the launcher rows and the composer strip.

## 5. Codex accounts can read Recording

Today `AccountWiring.State` can only say a Codex account is `Installed` (kcap's hook is in `hooks.json`)
or `NotWired`, and the app labels `Installed` "Needs trust". Whether Codex trusts the hook is known only
to `codex app-server`'s `hooks/list`, which today only the hosted launch path asks. So every wired Codex
account reads "Needs trust", would never be ready and would always need a fix. User decision: publish a
real Recording for Codex in this slice.

- **Asking.** `CodexAccountProbe` already starts `codex app-server` under the account's `CODEX_HOME` on
  every refresh. After the identity and limits steps, it also asks `hooks/list` and classifies the
  answer with `CodexHookTrust.Classify`. Move the classifier and `CodexHookEntry` from the daemon
  assembly into `Capacitor.Cli.Core.Harness.Codex` so both the probe and the hosted runtime use it.
  `Proceed` is **trusted**. `SeedAndRestart`, `Unseedable` and `MissingRequiredHooks` are **untrusted**.
  A `hooks/list` that errors, is not supported, times out or does not parse yields **no answer**.
- **What trust depends on.** Codex trusts a hook by `[hooks.state]` `trusted_hash` entries in its
  `config.toml`, separately from the hook definitions in `hooks.json`. The fingerprint is therefore a
  content hash over both files in `CODEX_HOME`, an absent file hashing as absent. The probe takes it
  **before** starting `codex app-server` and again after `hooks/list` answers. If the two differ, the
  answer is discarded as no answer, because it may describe either version of the files.
- **Storing.** A trusted or untrusted answer is stored on the registry entry with its fingerprint, in
  the same commit as the probe's other results and under the same probe-sequence rule. No answer leaves
  the stored pair as it is. The fingerprint still guards it, so a kept "trusted" only counts while the
  files are unchanged. A registry written before these fields reads as no stored answer.
- **The state.** `AccountWiring.State` gains an account-aware overload, which `AccountSnapshotBuilder`
  uses. It returns `Recording` for a Codex account whose kcap hook is present and whose stored answer is
  trusted for the fingerprint of the files as they are now. Anything else that today reads `Installed`
  stays `Installed` (Needs trust). Editing `hooks.json` or `config.toml` (a rewire, revoking trust,
  trusting it in Codex) changes the fingerprint, so a trusted answer stops counting.
- **Propagation.** `AccountsPublisher.PollAsync` rebuilds only when the registry or reading files change
  their stat. Its change check also covers each Codex account's `hooks.json` and `config.toml` stats.
  An external edit therefore republishes within one poll (2 s) with no registry mutation, refresh or
  usage change. On such a change the publisher also calls `AccountRefresher.Request` for that account.
  Trusting the hook in Codex then turns into Recording once the refresher's cooldown allows a probe
  (at most 10 minutes later), instead of waiting for a manual Refresh.
- **Refresh results are unchanged.** Trust is not a mutation step. `AccountRefresh.Steps` keeps its
  identity and limits steps, so `kcap accounts refresh` output, its exit code, the App lane's evidence
  rule and the README's description of the two steps do not change. An older Codex without `hooks/list`
  refreshes exactly as today and simply stays Needs trust. Trust is diagnostic state behind
  `recording_state`, nothing more.
- Nothing changes on the wire: the state travels in the existing `recording_state` token, so kcap-server
  and older apps need no change.
- Persisting trust is not this slice's job: a hosted launch still seeds trust per launch as it does
  today, and kcap never writes Codex's `[hooks.state]`.
- Tests:
  - the probe with trusted, untrusted, missing-hook, unsupported, timed-out and malformed `hooks/list`,
    through the existing fake app-server;
  - a file edited while a probe is in flight;
  - the state across fingerprint match and mismatch;
  - trust revoked by an edit to `config.toml` alone;
  - a registry without the fields;
  - the published snapshot falling back to Needs trust after an external edit, with no refresh, registry
    mutation or usage update;
  - refresh steps and exit code unchanged whatever the trust outcome.

## 6. Constraints

- App status colour rules (CLAUDE.md): `KcapWarning*` and `KcapDanger*` only on bars, percentages and
  pills; Default and plan are neutral chips; labels use text/muted tokens.
- Kcap control classes for every new control; Lucide via `AppIcon` for any icon.
- Filter `Connect()`/snapshot streams before `ObserveOn` the UI thread; no disk or git I/O on the UI
  thread; the tick must not rebuild Settings rows under an open menu or rename (keep the existing
  equality guard in `AccountsSettingsViewModel.ApplyGroups` working with the richer rows, e.g. by keeping
  identity rows stable and projecting reading text separately).
- No CLI flag, step or exit-code change, so no help text change. The README paragraph describing the
  Settings Accounts tab, which lists its rows, states and menu, gains the summary cards, bars, reading age
  and Refresh. Its Needs trust wording ("kcap hooks wait to be trusted") stays true and now applies
  only until a probe sees the trust.

## 7. Testing

Headless view-model tests per the first spec's 6.6: window labels; the 75% threshold and danger at 100%
and on a blocked window; reset text and next reset; reading age and freshness unknown; expired windows;
"+N per model"; the ready predicate (no reading, one stale window beside a fresh one, blocked, expired
windows, per-model exhaustion not blocking the account-wide limit, and a positive case: Recording, signed in, unblocked, fresh primary windows under 90%); a confirmed 0% window drawn as 0%; per-model-only and
blocked-before-any-window readings; blocked-until across both signal orders and mixed known and unknown
resets; a main constraint kept live by an exhausted per-model window (§2's clearing rule); Need a fix;
the drawable-host transitions of §4; Settings rows not rebuilt by a tick that changes nothing. Smoke
tests only for real interactions (Refresh in the row menu). A live look at the three surfaces against a
daemon publishing readings is owed to the user after merge.

## 8. Left to the builder

Type names and file layout, where the projection lives (App, not Core, unless the CLI needs it), how the
tick is shared, exact spacing and font sizes within the mockups' proportions, and the mini-bar width.
