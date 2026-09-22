# Desktop profiles settings

GitHub #1093, Linear AI-3072, a sub-issue of AI-1656 (settings surfaces).

## Agreed outcome

The Settings window gets a **Profiles** tab beside Daemon and Notifications. It
lists every profile in `config.json` and lets the user sign in to one, add one,
remove one, and switch to one.

- **Switch moves the app and the daemon together.** One daemon and one identity
  at a time: the service unit is replaced for the target profile through the
  mutation lane, then the app relaunches. A switch is durable: one that does
  not finish is resumed by the next start, never left half done.
- **Add takes a workspace name or a server URL, then signs in.** It never
  changes `active_profile`; switching to the new profile is a separate action.
- **Remove deletes the profile, its bindings and its token**, through one
  operation shared with `kcap profile remove`. The active profile cannot be
  removed.
- **Rows show credential status and offer Sign in.** There is no sign-out.

## What the code imposes

**The app binds one profile for its process lifetime.** `BuildDaemonGraph` runs
once from one `ProfileContext`, and the daemon client, server connection,
work-context and pull-request sources, agent directory and HTTP client capture
it. There is no rebuild path, so a switch ends in a relaunch, the way a daemon
rename does.

**The daemon is pinned, and the pin is enforced.** A service unit carries
`KCAP_PROFILE` and `KCAP_EXPECT_SERVER_URL`, the start gate refuses an identity
mismatch, and `DaemonConfig.Profiles` is never re-resolved. A switch is a lane
mutation, not a config write.

**A same-id switch works today; a cross-id switch is refused.**
`install --replace --verify` takes over a live validated owner of the target
label whatever profile it was pinned to and does not run the start gate's
identity check against the unit it replaces, so two profiles resolving to one
daemon name need nothing new. When the names differ the old unit must be
retired, and `--retire` refuses any unit whose `KCAP_PROFILE` is not the
profile being installed (`retire_reason=foreign_profile`).

**`--retire` never touches a daemon without a unit.** An absent plist is a
no-op, decided before any probe of the label's owner. The app can itself start
a detached daemon when a service install is not possible, and such a daemon
passes every idle gate, so a cross-id switch from it would leave the old daemon
running beside the new one.

**The unit bakes the profile's URL as resolved at install time.**
`ServiceInstallViability` checks only that the pinned profile resolves to an
http(s) URL, and `EnsureUnitEnv` bakes that resolved URL, not the invocation's
`KCAP_EXPECT_SERVER_URL`. A profile repointed between the app's read and the
install produces a unit for the new server that boots cleanly; the lane's
evidence predicate then reports skew, after the replaced unit is gone.

**The lane already speaks for the target.** Its executor is built per request
from `request.Profile` and `request.CanonicalServer`, so a request naming the
target profile runs `kcap` under that profile's `KCAP_PROFILE` and
`KCAP_EXPECT_SERVER_URL`, and the evidence predicate verifies the target's
server and daemon name.

**The lane latches retired service ids only, and only in its own process.**
After a rename it refuses every request touching the old id, which is also what
closes the lifecycle controller's auto actions (`requiresAppRestart`). A same-id
switch retires nothing, so without a second latch the old process, still bound
to the previous profile, could `ensure` that profile's unit back over the new
one. Neither latch survives a restart.

**The next start does not finish a failed switch.** The lifecycle controller's
startup matrix returns at once for a running service, whatever profile that
service is pinned to, and installs a unit for the resolved profile without
retiring any other. After a failed switch that left the old daemon running, a
relaunch resolves the target profile and attaches to the old daemon; after one
that retired the old unit but did not install the new, a relaunch installs the
target's unit and, with different names, a surviving old daemon stays.

**Credential status has a refresh-free evaluator.**
`OnboardingGate.EvaluateResolvedAsync(name, profile)` reads the token raw and
never refreshes. WorkOS refresh tokens are single-use and shared with the CLI
and the daemon, so a status column must not spend one. The verdict is
`Complete` for an expired token that carries refresh material, whether or not
the server will still accept it: a rejected refresh returns null and leaves the
file as it was, so a row can read as signed in while every request fails. The
evaluator also throws on an unreadable token file; only `EvaluateAsync` wraps
that.

**A known-server sign-in can create a profile and leaves `active_profile`
alone.** The wizard's Paste path calls `LoginAsync(..., adoptServer: true)`;
an absent profile is created by `PointProfileAtServer` and stamped in the same
`ConfigMutator` mutation. Tenant discovery is different: `MergeProfiles`
reassigns `active_profile`, which is why Add does not offer it.

**The commit boundary is ordered, not atomic, and decides its target before
authentication.** `LoginTarget.PointsAtServer` is computed from a config read
before the browser opens; the config mutation and stamp publish first, then the
tokens, and a token save that fails after the config landed still returns
`Committed`. `Stamp` recreates a missing profile from defaults. Nothing in the
boundary checks that the profile is still absent (for a create) or still names
the same server (for a re-auth) at the moment it commits.

**A new profile starts from defaults.** `PointProfileAtServer` builds an absent
profile from `new Profile()`: `default_visibility` is `org_public` and every
exclusion list is empty. A user who excluded repos on the current profile and
switches to a fresh one would record them, publicly. The bound
`ProfileContext` is a startup snapshot, so settings saved after launch are not
in it.

**Profile names are case-sensitive keys over case-insensitive token files.**
`config.json` keys `acme` and `Acme` are two profiles; on macOS they share
`tokens/acme.json`. `TokenStore.ValidateProfileName` is private.

**`kcap profile remove` leaves the credential.** It drops the profile and its
bindings, not `tokens/<name>.json`, and resets `active_profile` to `default`
even when `default` has no server, which sends the app to the wizard on its next
start.

**`kcap use` decides outside the lock.** It checks that the profile exists on a
plain read, then writes `active_profile` or a binding in a mutation that does
not recheck, so a removal between the two makes a missing profile active.

**A token refresh resurrects a deleted file.** The refresh path loads the token
before taking the profile's cross-process lock; under the lock it falls back to
that pre-lock copy when the file is absent, refreshes it and saves it again.
`TokenStore.Delete` takes no lock. A removal that runs while a refresh waits for
the lock is undone by the refresh.

**`SettingsProfileStore` is bound to the profile the app started on.** It
refuses once that profile is missing or points at another server. It does not
read `active_profile`, so a switch leaves the Daemon tab working; removing the
bound profile would not.

## Profiles tab

A third `TabItem` in `SettingsWindow.axaml` hosts `Views/ProfilesSettingsView.axaml`
with its own `ViewModels/ProfilesSettingsViewModel.cs`, exposed as
`SettingsViewModel.Profiles`. `SettingsViewModel` keeps the Daemon and
Notifications state it has; the profiles section is the first with its own view
model because its dependencies (token store, sign-in composition, removal) are
disjoint from the daemon section's.

Two identities are tracked separately: the **active** profile (what
`config.json` names now) and the **bound** profile (what this process resolved
at startup). They differ under a `KCAP_PROFILE` launch, and after an external
`kcap use --global` while the app runs.

Each row is a card in the Notifications tab's pattern:

- profile name and server URL, in text and muted tokens;
- an **Active** mark on the active profile and a **This app** mark on the bound
  one, both in `KcapPurple*` (location, not outcome);
- a status label: signed in, sign-in expired, signed out, signed in to another
  server, no sign-in needed (a `None` stamp for the profile's server), no server
  configured, or could not read sign-in status (the token read threw; the
  exception is logged and no other row is affected);
- row actions as `kcapChip` buttons: **Sign in** on every row with a valid
  server and no `None` stamp, whatever its status, because a locally complete
  verdict can hide a refresh the server now rejects; **Switch** on every row
  other than the bound one; **Remove** on a removable row.

A row with no valid server has no action; its hint says to use Add or
`kcap profile add`.

**Add profile** is a `kcapPrimary` button under the list. One shared `Message`
line reports refusals and confirmations, as the Daemon tab does.

`ProfileRows` is rebuilt from a fresh `ConfigMutator.TryLoadPure` read when the
window opens, after every action, and when the window is activated, so an
external `kcap use` or `kcap profile add` shows up on return to the window.
Every action re-reads before it acts; a row that no longer matches the config
is refused with "This profile changed; the list was refreshed."

## Sign in

Sign in on a row opens the existing sign-in dialog composed for that row:
`ReauthComposition.Build(..., profile: row.Name, serverUrl: row.ServerUrl, ...)`.
`OpenSignInDialog` takes the profile and server as arguments instead of reading
them from the bound `ProfileContext`. The post-success refresh of the app's own
server lane runs only when the row is the bound profile.

The commit carries the precondition `ExpectServer(row.ServerUrl)` (see
Commit preconditions): a profile removed or repointed while the browser was
open is refused at commit, and no token is written.

## Add

A dialog with two fields:

- **Workspace or server**: a workspace name (`acme`) or a full `https://` URL,
  resolved by `WizardSignInOperation.ResolveServer` and accepted by
  `OnboardingGate.ValidServerUrl`, the rule the wizard's Connect step uses.
- **Profile name**: defaulted from the workspace name or the URL's first host
  label, validated by `TokenStore.IsValidProfileName` (the private validator
  made public, with its error text), refused when it equals an existing
  profile's name ignoring case.

Continue runs the sign-in step with a `ConnectIntent.Paste` for the new profile
name and the precondition `ExpectAbsent`. The facade creates the profile, stamps
it and saves the token in its commit boundary.

**Seeding.** `LoginAsync` gains an optional `seedFrom` profile name. Inside the
commit mutation, when the target profile is absent, `PointProfileAtServer`
builds it from `Profile.SeedFrom(config.Profiles[seedFrom])` instead of
`new Profile()`; when the seed profile is missing at that moment, from
`new Profile()`. `SeedFrom` copies the profile with `server_url`, `remotes`,
`import_org` and `auth_provider` cleared, so visibility, allow and exclude lists
and daemon settings carry over as saved at commit time, not as captured at
startup. `remotes` must not be copied: two profiles matching one git remote
make `RemoteMatcher` throw. `daemon` is copied, so the new profile resolves to
the same daemon name and a later switch takes the same-id path. Add passes the
bound profile's name.

**Outcome.** A sign-in that stops before the commit boundary writes nothing.
Past it, the profile and stamp may exist without a token. The dialog reports
success only when the new profile's refresh-free verdict is `Complete` after the
result; otherwise it says the profile was added and sign-in must be repeated
from its row, which then shows signed out with Sign in. The row sign-in dialog
gates its success message the same way.

### Commit preconditions

`CommitRequest` gains an optional precondition, evaluated inside the config
mutation on the config as it is at commit:

- `ExpectAbsent`: the target profile does not exist.
- `ExpectServer(url)`: the target profile exists and `ServerIdentity.SameServer`
  holds against `url`.

A failing precondition throws inside the mutation, so `ConfigMutator` publishes
nothing, and `CommitAsync` returns `Failed` naming the reason before any token
is published. The wizard's own paths pass no precondition and are unchanged.
The precondition is what makes `LoginTarget`'s pre-authentication read safe: a
profile repointed during authentication can neither be stamped for the old
server nor repointed back by the Paste path's `adoptServer`.

## Remove

`Capacitor.Cli.Core/Config/ProfileRemoval.cs`:

```csharp
public enum ProfileRemovalOutcome { Removed, RemovedTokenRetained, NotFound, IsDefault, IsActive }

public sealed record ProfileRemovalResult(ProfileRemovalOutcome Outcome, string? Detail);

public static Task<ProfileRemovalResult> RemoveAsync(ConfigRoot config, TokenStore tokens, string name, CancellationToken ct)
```

One `ConfigMutator` mutation decides and applies: refuse `default`, refuse a
missing profile, refuse the profile `active_profile` names at that moment, else
drop the profile and every binding pointing at it. Deciding inside the mutation
is what stops a concurrent `kcap use --global` from making the removed profile
active between check and write.

After `Removed`, the token is deleted unless another remaining profile's name
equals this one ignoring case, in which case the file is theirs and stays
(`RemovedTokenRetained`, detail naming the alias). A deletion that throws also
returns `RemovedTokenRetained`, with the file path as detail; a retry finds the
profile gone and cannot delete the file, so the caller shows the path.

`TokenStore.Delete` takes the profile's cross-process refresh lock. The refresh
path treats a file absent under that lock as a sign-out: it returns null and
writes nothing, instead of refreshing its pre-lock copy. This is what makes a
removal win over a refresh that was waiting for the lock.

`kcap use` decides inside its mutation: the profile must exist at that moment
for the global, binding and `--save` arms alike, else the command refuses with
the same message it prints today.

`kcap profile remove` calls `RemoveAsync` and prints a line per outcome; for
`IsActive` it names `kcap use <other> --global`, for `RemovedTokenRetained` the
detail. This changes CLI behaviour in two ways, both documented in `README.md`
and `help-profile.txt`: the token is deleted, and the active profile is refused
instead of silently reset to `default`.

The app offers Remove on a row that is neither active, bound, nor `default`,
behind a confirm dialog naming the profile and saying its sign-in is deleted.

## Switch

Switch is enabled on a row other than the bound one when all hold, and
otherwise the row says why:

- the target's verdict is `Complete` (sign in first);
- `KCAP_PROFILE` is not set (it would override `active_profile` on relaunch);
- the platform supports `--verify` (macOS);
- the startup phase has settled and the last snapshot reports zero active
  agents, an unreachable daemon counting as idle;
- the lane holds no restart latch and no switch is pending.

Then, in order:

1. Resolve the target's daemon name with `DaemonNameResolver.Resolve([], target.Daemon?.Name)`.
   When it differs from the running name: probe the target name's socket with
   the one-shot hello the rename uses, a reply being a collision that stops
   here; and require the running daemon to be the installed unit, by the
   classification `ensure` calls already enabled (loaded label owning the
   validated daemon pid). A reachable daemon with no unit is refused: "Stop the
   daemon <name> first (`kcap daemon stop --name <name>`); it is not running
   as a service, so switching cannot retire it."
2. Confirm a lifecycle prompt: the daemon restarts for the target profile and
   the app relaunches.
3. Recheck settled and idle. Build the request with
   `MutationRequestFactory.TryBuild(Replace, target.Name, target.ServerUrl, targetDaemonName, out request, retireServiceId, supersedesProfile: bound.Name)`,
   `retireServiceId` being the running name when the names differ and null
   otherwise. When it is non-null, probe the lane-selected CLI for
   `--retire-profile` support.
4. Activate, in one `ConfigMutator` mutation that first checks the target still
   exists, still names the row's server (`SameServer`) and still resolves to
   `targetDaemonName`, refusing otherwise with the list refreshed. It writes
   the pending-switch record (below) and `active_profile = target.Name`,
   remembering the previous value.
5. Run the request through the lane.
6. An outcome that provably touched nothing — a pre-spawn `Refused`, or
   `Failed` with a token from before the replace matrix (viability,
   `contended`, `retire_refused`, `expect_server_mismatch`) — restores the
   previous `active_profile` only if the config still holds the value this
   attempt wrote, clears the record, and reports the reason; for
   `cli_unsupported` the report says kcap needs updating.
7. Any other outcome sets the window's restart-required state. `Succeeded`
   clears the record and relaunches the app through `RelaunchForSettingsAsync`;
   unbundled, the form says to restart. Every other outcome keeps the record
   and `active_profile` on the target, reports through the lane's outcome
   consumer and the form, and says to restart the app to finish the switch.

### Pending-switch record

`app-state.json` (the existing `AppStateStore`) gains `pending_switch`:
`{ previous, target, daemon_name, retire_service_id, supersedes_profile }`,
written in step 4 and cleared on `Succeeded` or a nothing-touched outcome.

At startup, the lifecycle controller reads it before its startup matrix:

- No record: the matrix runs as today.
- Record whose `target` is not `active_profile`: a selection made elsewhere
  wins; the record is dropped and the matrix runs for the resolved profile.
- Record matching `active_profile`: the controller rebuilds the same request
  and runs it through the lane once. `Succeeded` clears the record and the
  matrix runs. Any other outcome keeps the record, skips the matrix for this
  run and surfaces attention: "Switching to <target> did not finish." The
  Profiles tab shows the pending switch with **Retry** (the same request again)
  and **Cancel** (drops the record; the next start runs the ordinary matrix for
  `active_profile`, treating whatever daemon exists as it does today).

The retry is safe to repeat: `Replace` takes over a live owner of the same id,
`--retire` of an absent unit is a no-op, and the retired unit's pin is still
the superseded profile.

### Lane

`MutationRequest` gains `string? SupersedesProfile`, set only by the factory and
only for `Replace`. When a request carrying it finishes with any outcome except
`Failed { Reason: "cli_unsupported" }`, the lane adds the value to a superseded
set at the same point the retired-id set is updated, before the next queued
request is admitted. `IsSuperseded(profileName)` reads it; a request whose
`Profile` is superseded is refused with `profile_switched_restart_app`, queued
or new. `requiresAppRestart` becomes
`lane.IsRetired(daemonName) || lane.IsSuperseded(boundProfile)`, which closes
the old process's auto actions for the same-id switch that retires nothing,
while a relaunched process, bound to the target, may retry.

The `Replace` dispatch forwards `RetireServiceId` as today and adds
`--retire-profile <SupersedesProfile>` only when both `RetireServiceId` and
`SupersedesProfile` are set; a same-id switch carries `SupersedesProfile` for
the latch alone and emits neither flag. `IKcapCli.ServiceInstallVerifiedAsync`
gains `retireProfile`, and, as it does for `--retire`, rechecks `daemon --help`
for `--retire-profile` on the pinned executor before spawning, returning
`retire_reason=cli_unsupported` when absent, so a CLI downgraded or repointed
between the preflight and the dispatch is caught. `CanRetireAsync` gains the
same check for the preflight.

The rename's timeout for a request with a retire id applies unchanged.

### CLI

`kcap daemon service install --replace --verify --retire <id> --retire-profile <name>`
names the profile the retired unit must be pinned to. Rules:

- Requires `--retire`. Absent, the expected profile is the one being installed,
  so a rename behaves as it does now.
- The match stays exact and ordinal against the plist's `KCAP_PROFILE`; a unit
  pinned to any other profile, or to none, is refused with
  `retire_reason=foreign_profile` and nothing is touched.
- Every other retire rule is unchanged: its own lock and budget, absent unit is
  a no-op, unreadable unit refused, a live validated owner under the target
  label is contended.

`install --verify` gains one viability check: when the invocation carries
`KCAP_EXPECT_SERVER_URL`, it must canonicalize equal to the pinned profile's
resolved URL, else the transaction refuses with `expect_server_mismatch` before
the replace matrix, nothing touched. The lane maps it to `Failed` with that
token and the Attention surface.

`README.md`'s daemon service section and `help-daemon.txt` document both.

## Delivery

Three PRs, each green on its own, each referencing #1093 and AI-3072; the last
closes it.

1. **List, status, Sign in, Remove.** The tab and `ProfilesSettingsViewModel`;
   `ExpectServer`; `ProfileRemoval`, `TokenStore.Delete` under the lock and
   the absent-under-lock refresh rule, `kcap use` deciding in its mutation,
   `kcap profile remove`; README and help text.
2. **Add.** The dialog, `IsValidProfileName`, `ExpectAbsent`, `SeedFrom`, the
   verdict-gated success message.
3. **Switch.** `--retire-profile` and `expect_server_mismatch`, `SupersedesProfile`
   and the superseded set, the pending-switch record and startup recovery, the
   flow; README and help text.

## Testing

- `ProfileRemovalTests` (Core): token deleted; bindings dropped; each refusal;
  a profile made active between load and mutation is refused; a delete that
  throws returns `RemovedTokenRetained` with the path; a case-alias keeps the
  file; a refresh waiting on the lock while removal runs ends signed out with
  no file written.
- `UseCommandTests`: a profile removed between read and mutation is refused on
  the global, binding and `--save` arms.
- `ProfileCommandTests` (CLI): remove prints each outcome and leaves no token.
- Facade tests: `ExpectAbsent` and `ExpectServer` refuse at commit with no
  token written, for a change made before and after `LoginTarget` is built,
  including a same-server Paste; an absent profile is seeded from the seed
  profile's settings as saved at commit, with the four fields cleared; a
  missing seed falls back to defaults; `active_profile` is untouched by a
  known-server sign-in.
- `ServiceVerify`: `--retire-profile` match, mismatch, unit with no
  `KCAP_PROFILE`, absent, unreadable, and the unchanged default;
  `expect_server_mismatch` refuses before the matrix and an equal or unset
  expectation proceeds.
- `KcapCliTests`: exact argv for a same-id request (no retire flags) and a
  cross-id request (both flags); `cli_unsupported` when `--retire-profile` is
  missing from help at dispatch.
- `DaemonMutationLaneTests`: the superseded set is filled by every outcome
  except `cli_unsupported`, refuses a queued and a new request for that
  profile, admits a request for another profile, and is untouched by a plain
  rename.
- `DaemonLifecycleControllerTests`: a pending record is dropped when the
  selection moved; a matching record runs the request before the matrix; a
  failure skips the matrix and surfaces attention; success clears the record
  and runs the matrix.
- `ProfilesSettingsViewModelTests`, in the `SettingsViewModelTests` pattern
  (`TempConfigRoot`, fake lane, confirm and relaunch): row status for each
  verdict, an empty `default`, an invalid URL, a `None` stamp and one
  unreadable token; Sign in offered on a `Complete` row; Switch offered on the
  active-but-not-bound row after an external `kcap use --global`; each switch
  gate including a reachable daemon with no unit; same-id and cross-id
  requests; activation refused when the target was removed or repointed during
  confirmation; restore only when the config still holds this attempt's value;
  the record kept on a post-matrix failure; Retry and Cancel; remove gates for
  active, bound and `default`; re-read on activation.
- `SettingsWindowSmokeTests`: the tab and its named controls, `kcapChip` and
  `kcapPrimary` classes, and the Active and This app marks using a purple
  brush, not a success one.
- `dotnet publish -c Release` for IL warnings on each PR.

## Out of scope

- Tenant discovery from Settings (a merge that does not reassign `active_profile`).
- Per-profile sign-out.
- A config file watcher.
- Settings when no profile resolves (`KCAP_URL` or `--server-url` launch).
- Running daemons for two profiles side by side.
- Renaming the token files to close the case-alias sharing at its root; the
  file name is a persistence contract shared with the CLI and the daemon.
