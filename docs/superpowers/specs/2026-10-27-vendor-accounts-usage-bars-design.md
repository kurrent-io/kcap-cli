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
- **No reading:** a null `Reading`, or one with no windows, is "no reading yet". Never zero, never ready.
- **Blocked** (user decision): only a live constraint on the main limit id, or one with no limit id,
  blocks the account. A live constraint on a per-model limit id does not block; it shows beside the
  per-model count ("+2 per model · 1 hit") and does not affect Ready. "Live" keeps
  `ReadingMerge.IsBlocked`'s rules (a marker blocked after its last clear; a constraint with a null
  `ResetsAt` never lapses by time); write the account-level predicate as that rule restricted to the
  main limit, sharing its lapse logic rather than restating it. The blocked windows are those matching a
  live main-limit constraint's window length; a constraint with no window length blocks the account
  without colouring a particular bar. The pill reads "Limit hit" plus the latest live main-limit
  constraint's reset where the surface has room.
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
accounts read only covers connected daemons; this stays the rule everywhere, and no surface keeps a last
reading after its host goes away:

- **Launcher:** bars appear only on a connected target's rows. A connection lost while the panel is open
  returns the section to its existing pending or offline state.
- **Settings:** this machine only, as today; with the daemon down the tab keeps its "listed once the
  daemon is connected" notice and draws no bars.
- **Composer strip, remote session whose host is not in the server's accounts read:** glyph, label and
  directory only — no bars and no "no reading yet", which would claim something unknown.

## 5. Codex accounts can read Recording

Today `AccountWiring.State` can only say a Codex account is `Installed` (kcap's hook is in `hooks.json`)
or `NotWired`, and the app labels `Installed` "Needs trust". Whether Codex trusts the hook is known only
to `codex app-server`'s `hooks/list`, which today only the hosted launch path asks. So every wired Codex
account reads "Needs trust", would never be ready and would always need a fix. User decision: publish a
real Recording for Codex in this slice.

- `CodexAccountProbe` already starts `codex app-server` under the account's `CODEX_HOME` on every
  refresh. It also asks `hooks/list` and classifies the answer with `CodexHookTrust.Classify` (move the
  classifier and `CodexHookEntry` from the daemon assembly into `Capacitor.Cli.Core.Harness.Codex` so
  both the probe and the hosted runtime use it). `Proceed` means trusted. A failed or unsupported
  `hooks/list` is unknown and changes nothing. The trust step fails independently of identity and limits,
  like those two already do.
- The outcome is stored on the registry entry next to the probe's other results, together with a
  fingerprint of the `hooks.json` it was observed against (its content hash is enough).
  `AccountWiring.State` returns `Recording` for a Codex account whose hook is present and whose stored
  trust is "trusted" for the current fingerprint. A rewire or any other edit to `hooks.json` changes the
  fingerprint, so the account falls back to Needs trust until the next probe says otherwise. A stale
  "trusted" is never shown.
- Nothing changes on the wire: the state travels in the existing `recording_state` token, so kcap-server
  and older apps need no change. A registry written before this field reads as unknown trust.
- Persisting trust is not this slice's job: a hosted launch still seeds trust per launch as it does
  today, and kcap never writes Codex's `[hooks.state]`.
- Tests: the probe with a trusted, untrusted, missing-hook and failing `hooks/list` (through the
  existing fake app-server); the state function across fingerprint match and mismatch; a registry
  without the field.

## 6. Constraints

- App status colour rules (CLAUDE.md): `KcapWarning*` and `KcapDanger*` only on bars, percentages and
  pills; Default and plan are neutral chips; labels use text/muted tokens.
- Kcap control classes for every new control; Lucide via `AppIcon` for any icon.
- Filter `Connect()`/snapshot streams before `ObserveOn` the UI thread; no disk or git I/O on the UI
  thread; the tick must not rebuild Settings rows under an open menu or rename (keep the existing
  equality guard in `AccountsSettingsViewModel.ApplyGroups` working with the richer rows, e.g. by keeping
  identity rows stable and projecting reading text separately).
- No README or help text change: no CLI surface changes (Refresh already exists in the CLI); the
  Codex trust fix changes only what `kcap accounts` reports for a state, not its flags.

## 7. Testing

Headless view-model tests per the first spec's 6.6: window labels; the 75% threshold and danger at 100%
and on a blocked window; reset text and next reset; reading age and freshness unknown; expired windows;
"+N per model"; the ready predicate (no reading, one stale window beside a fresh one, blocked, expired
windows, per-model exhaustion not blocking the account-wide limit); Need a fix; a disconnected host's
session falling back to the directory; Settings rows not rebuilt by a tick that changes nothing. Smoke
tests only for real interactions (Refresh in the row menu). A live look at the three surfaces against a
daemon publishing readings is owed to the user after merge.

## 8. Left to the builder

Type names and file layout, where the projection lives (App, not Core, unless the CLI needs it), how the
tick is shared, exact spacing and font sizes within the mockups' proportions, and the mini-bar width.
