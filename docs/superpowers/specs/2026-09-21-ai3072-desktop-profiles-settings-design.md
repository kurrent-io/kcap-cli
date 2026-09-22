# Desktop profiles settings

GitHub #1093, Linear AI-3072, a sub-issue of AI-1656 (settings surfaces).

## Agreed outcome

The Settings window gets a **Profiles** tab beside Daemon and Notifications. It
lists every profile in `config.json` and lets the user sign in to one, add one,
remove one, and switch to one.

- **Switch moves the app and the daemon together, in one CLI transaction.**
  `active_profile` changes only inside the daemon-replacing transaction, at the
  point where no other profile's daemon can be running, so at every moment the
  config names the profile whose daemon the next start establishes or attaches
  to. The app runs one lane mutation and relaunches on success.
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

**The verify transaction is ordered and marker-journaled, and its rollback is
bounded.** `install --replace --verify` runs viability, leftover-marker
recovery, retire (with `--retire`, under the retired label's own lock, released
when retire returns), the pre-query, the replace matrix that clears or kills the
target label's owner and confirms it gone, then writes the unit, bootstraps and
polls readiness. On a failure after the unit is written it uninstalls that unit
when the plist on disk is still the one it wrote, confirms label and file
absence, and gives up with a coded token when the plist is foreign or
unreadable, when the label stays loaded, or when its budget runs out; a
fingerprint mismatch found after readiness returns `restore_verification`
without uninstalling. A retired unit is never restored; a retire that then
finds the target label contended has already uninstalled the old unit. A crash
leaves a marker the next transaction recovers by content. The app's own
timeout can kill the CLI before its rollback runs. Nothing in the transaction
touches `config.json`.

**A same-id switch works today; a cross-id switch is refused.** The replace
matrix takes over a live validated owner of the target label whatever profile
it was pinned to and does not run the start gate's identity check against the
unit it replaces, so two profiles resolving to one daemon name need nothing
new. When the names differ the old unit must be retired, and `--retire` refuses
any unit whose `KCAP_PROFILE` is not the profile being installed
(`retire_reason=foreign_profile`).

**`--retire` never touches a daemon without a unit.** An absent plist is a
no-op, decided before any probe of the label's owner. The app can itself start
a detached daemon when a service install is not possible, and such a daemon
passes every idle gate, so a cross-id switch from it would leave the old daemon
running beside the new one.

**The unit bakes the invocation's server expectation, and viability does not
check it.** The install path captures `KCAP_EXPECT_SERVER_URL` from the
invoking environment into the unit; `ServiceInstallViability` checks only that
the pinned profile resolves to an http(s) URL. A profile repointed between the
app's read and the install passes viability, the old unit is retired or
cleared, and the new daemon refuses to boot with `server_expectation_mismatch`,
so the transaction rolls back to no daemon at all.

**The next start does not finish a failed switch.** The lifecycle controller
attaches to a connected daemon and reports an incompatible one without running
its startup matrix; the matrix returns at once for a running service, whatever
profile that service is pinned to, installs a unit when none exists, and starts
a stopped one. It converges on `active_profile` only when no daemon of another
profile is running under the bound name.

**The lane already speaks for the target.** Its executor is built per request
from `request.Profile` and `request.CanonicalServer`, so a request naming the
target profile runs `kcap` under that profile's `KCAP_PROFILE` and
`KCAP_EXPECT_SERVER_URL`, and the evidence predicate verifies the target's
server and daemon name.

**The lane latches retired service ids only, and only in its own process.**
After a rename it refuses every request touching the old id, which is also what
closes the lifecycle controller's auto actions (`requiresAppRestart`). A same-id
switch retires nothing. Admission checks only that set, and a finished request
admits the next queued one, so a lifecycle action that passed its restart check
before the switch began is dispatched after it. A `Refused` outcome is not
proof that nothing was spawned: a boot refusal after bootstrap is reported as
`Refused` too. The lane keeps no raw CLI output on an outcome, and a timeout is
classified before any output is read.

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
`ConfigMutator` mutation. `kcap login` calls it with `adoptServer: false`, and
a profile that does not name the server then gets a credential for that server
with neither `server_url` nor stamp written: a supported path. Tenant discovery
is different: `MergeProfiles` reassigns `active_profile`, which is why Add does
not offer it.

**The commit boundary is ordered, not atomic, and decides its target before
authentication.** `LoginTarget.PointsAtServer` is computed from a config read
before the browser opens; the config mutation and stamp publish under the
config lock, the lock is released, then the tokens are saved without any lock,
and a token save that fails after the config landed still returns `Committed`.
`Stamp` recreates a missing profile from defaults, and a target whose
same-server profile disappeared after the read is not `Adopting`, so it is
recreated without a server. Nothing in the boundary checks that the profile is
still absent (for a create) or still names the same server (for a re-auth) at
the moment it commits or at the moment it saves the token.

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

**The token store's writes are unlocked, and one of them deletes another
profile's credential.** The refresh path loads the token before taking the
profile's cross-process lock; under the lock it falls back to that pre-lock
copy when the file is absent, refreshes it and saves it again through
`SaveAsync`. `SaveAsync` and `Delete` take no lock. The legacy `tokens.json`
backs exactly one profile, the on-disk active one, while its per-profile file
is absent; `SaveAsync` for any profile deletes it, so saving a token for
profile B removes active profile A's only credential. The owner check
`IsLegacyOwnerAsync` reads config through `AppConfig.LoadProfileConfig`, which
can persist a v1 migration through `ConfigMutator`.

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
disjoint from the daemon section's. Both sections read the lane's restart
state before every action, not once at construction.

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
Commit guards): a profile removed or repointed while the browser was open is
refused at commit, and no token is written. The dialog reports success only
when the result says the credential was saved.

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

**Seeding.** `LoginAsync` gains an optional `seedFrom` profile name. Under
`ExpectAbsent` the commit mutation always creates the target, whatever
`LoginTarget` computed before authentication, from
`Profile.SeedFrom(config.Profiles[seedFrom])` when the seed profile exists at
that moment and from `new Profile()` otherwise. `SeedFrom` copies the profile
with `server_url`, `remotes`, `import_org` and `auth_provider` cleared, so
visibility, allow and exclude lists and daemon settings carry over as saved at
commit time, not as captured at startup. `remotes` must not be copied: two
profiles matching one git remote make `RemoteMatcher` throw. `daemon` is copied,
so the new profile resolves to the same daemon name and a later switch takes the
same-id path. Add passes the bound profile's name.

**Outcome.** A sign-in that stops before the commit boundary writes nothing.
Past it, the profile and stamp may exist without a token. `AuthResult.Committed`
gains `CredentialSaved`: true when the boundary saved a token or the server
needs none. The Add dialog reports success only on `CredentialSaved`; otherwise
it says the profile was added and sign-in must be repeated from its row, which
then shows signed out with Sign in. The row sign-in dialog reports the same
way, so a re-authentication whose new token failed to save over a rejected old
one is not called a success.

### Commit guards

`CommitRequest` gains an optional precondition, evaluated inside the config
mutation on the config as it is at commit:

- `ExpectAbsent`: no profile whose name equals the target's ignoring case
  exists.
- `ExpectServer(url)`: the target profile exists and `ServerIdentity.SameServer`
  holds against `url`.

A failing precondition throws inside the mutation, so `ConfigMutator` publishes
nothing, and `CommitAsync` returns `Failed` naming the reason before any token
is published. The precondition is what makes `LoginTarget`'s pre-authentication
read safe: a profile repointed during authentication can neither be stamped for
the old server nor repointed back by the Paste path's `adoptServer`.

A request carrying a precondition also publishes its token under the profile's
token lock with the guard "a profile of this exact name exists and names the
token's server", evaluated under the lock; when the guard fails the token is
not written and `CredentialSaved` is false. This closes the interval between
the config commit and the token save against a concurrent removal. A request
without a precondition — the wizard, `kcap login`, including its foreign-profile
save — commits and saves exactly as today.

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

After the mutation, the token is deleted under the profile's token lock with
the guard "no profile whose name equals this one ignoring case exists in
config": a profile recreated under the same name since the mutation, or a
case-alias sharing the file, keeps the file, and the result is `Removed`
because the file now belongs to a remaining profile. A deletion that throws
returns `RemovedTokenRetained` with the file path as detail; a retry finds the
profile gone, so the caller shows the path.

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

### Token lock

The profile's cross-process refresh lock becomes the lock for every write to
`tokens/<profile>.json`, through one pair of seams:

- `SaveAsync(profile, tokens, guard?)` and `DeleteAsync(profile, guard)` take
  the lock, evaluate the optional guard under it against a pure config read
  (`ConfigMutator.TryLoadPure`, never the migrating loader), and write only when
  it passes. A null guard writes unconditionally, as today.
- `SaveLockedAsync` is the lock-held primitive the refresh path calls, since it
  already holds the lock; the public methods wrap it. Nothing takes the lock
  twice.
- The refresh path, once it holds the lock, reloads from disk with the same
  legacy fallback its pre-lock read used, the owner check made pure (the
  active name from `TryLoadPure`), instead of using the pre-lock copy. Nothing
  on disk means signed out: it returns null and writes nothing. A legacy
  `tokens.json` still on disk is found by the reload, refreshed and migrated.
- The legacy `tokens.json` is deleted after a save only when the saved profile
  is its owner by that same pure check. A save for another profile leaves it.

The config lock is never held while the token lock is taken, and under the
token lock config is only read.

## Switch

Switch is enabled on a row other than the bound one when all hold, and
otherwise the row says why:

- the target's verdict is `Complete` (sign in first);
- `KCAP_PROFILE` is not set (it would override `active_profile` on relaunch);
- the platform supports `--verify` (macOS);
- the startup phase has settled and the last snapshot reports zero active
  agents, an unreachable daemon counting as idle;
- the lane holds no restart latch.

Then, in order:

1. Resolve the target's daemon name with `DaemonNameResolver.Resolve([], target.Daemon?.Name)`.
   When it differs from the bound name: probe the target name's socket with the
   one-shot hello the rename uses, a reply being a collision that stops here;
   and classify the source by `ServiceStatusAsync` for the bound name plus a
   one-shot hello of it. A unit that is present, running or stopped, and an
   absent unit with no reply proceed; an absent unit whose name replies is a
   detached daemon `--retire` cannot touch, refused with "Stop the daemon
   <name> first (`kcap daemon stop --name <name>`); it is not running as a
   service, so switching cannot retire it"; an unreadable status is refused.
   This is a courtesy check for a clear message; the transaction re-proves it.
2. Confirm a lifecycle prompt: the daemon restarts for the target profile and
   the app relaunches.
3. Recheck settled and idle. Probe the lane-selected CLI for `--activate` in
   `daemon --help`, refusing with "update kcap" when absent; this is the one
   capability probe, same-id and cross-id alike, and a CLI that has it has
   every check below. Build the request with
   `MutationRequestFactory.TryBuild(Replace, target.Name, target.ServerUrl, targetDaemonName, out request, retireServiceId, retireProfile, activate: true)`,
   where `retireServiceId` is the bound name and `retireProfile` the bound
   profile when the names differ, and both are null otherwise.
4. Run the request through the lane.
5. `Succeeded` relaunches the app through `RelaunchForSettingsAsync`;
   unbundled, the form says to restart. Every other outcome is reported through
   the lane's outcome consumer and the form, with the transaction's own token
   and the outcome's activation evidence (below): "The switch to <target> did
   not finish", then which profile `config.json` names now, from a fresh read,
   without claiming the transaction did or did not change it when the evidence
   is unknown.

The app writes nothing to `config.json` in this flow. What the next start does
follows from where the transaction stopped:

- Before activation: `active_profile` still names the previous active
  selection. The old daemon may still be running, or may already be retired or
  cleared (a retire that then finds the target label contended has uninstalled
  the old unit). The next start attaches to the previous profile's daemon if it
  runs, else reinstalls or restarts it.
- After activation: no daemon of another profile is running, because the
  transaction proved the old owner gone immediately before writing, and only
  this transaction writes a unit under the target name. Any daemon found under
  that name afterwards is the target's. The next start attaches to it when the
  transaction or a late boot left it running, installs one when the rollback
  left nothing, and otherwise surfaces the same attention states a failed
  rename does: a foreign or unreadable plist, a label that stayed loaded, a
  rollback that ran out of budget, or a marker left by a killed CLI, which the
  next transaction recovers. None of these is a daemon of the wrong profile.

### Lane

`MutationRequest` gains `string? RetireProfile` and `bool Activate`, set only by
the factory and only for `Replace`; `RetireProfile` requires `RetireServiceId`,
and the factory refuses the combination otherwise, as it does today for an
invalid retire target. The `Replace` dispatch forwards `--retire <id>` as
today, adds `--retire-profile <RetireProfile>` when set, and `--activate` when
set. `IKcapCli.ServiceInstallVerifiedAsync` gains both, and rechecks
`daemon --help` on the pinned executor before spawning for each flag it is
about to emit, as it does for `--retire`, returning
`retire_reason=cli_unsupported` when one is missing, so a CLI downgraded or
repointed between the preflight and the dispatch is caught. `CanRetireAsync`
gains the same checks for the preflight.

Every outcome of an `Activate` request carries `ActivationEvidence`:
`Activated` when the CLI's stdout carried `activated=<profile>`, `NotActivated`
when the request never reached the CLI (a pre-spawn `Refused`, or the
executor's synthetic `cli_unsupported`), and `Unknown` otherwise, including a
timeout, a kill, or an exit whose output lacks the line. The line is written to
stdout after the config publish, so its absence never proves the publish did
not happen.

The lane latches restart-required once an `Activate` request has been
dispatched to the executor, whatever its outcome, except the synthetic
`cli_unsupported`, which is distinguishable: the executor returns exit
`RetireRefused` with `retire_reason=cli_unsupported`, a reason the CLI itself
never emits. While the latch is set the lane refuses every later request at
admission, queued or new, with `profile_switched_restart_app`, the same place
the retired-id set is checked; a lifecycle action that passed its own restart
check before the switch began is therefore still refused. `requiresAppRestart`
becomes `lane.IsRetired(daemonName) || lane.RestartRequired`. Without the
admission refusal, a queued `Replace` for the previous profile could take over
the target's freshly installed unit, because `Replace` does not run the start
gate's identity check.

The rename's timeout for a request with a retire id applies unchanged.

### CLI

`kcap daemon service install --replace --verify [--retire <id> [--retire-profile <name>]] [--activate]`.

`--retire-profile <name>` names the profile the retired unit must be pinned to.
Rules:

- Requires `--retire`. Absent, the expected profile is the one being installed,
  so a rename behaves as it does now.
- The match stays exact and ordinal against the plist's `KCAP_PROFILE`; a unit
  pinned to any other profile, or to none, is refused with
  `retire_reason=foreign_profile` and nothing is touched.
- Every other retire rule is unchanged: its own lock and budget, absent unit is
  a no-op, unreadable unit refused, a live validated owner under the target
  label is contended.

`--activate` changes the transaction in four places; without it the transaction
is unchanged, so a rename keeps its semantics.

- **Viability** gains two checks, evaluated with the existing ones, before
  leftover-marker recovery and before retire, nothing touched on refusal:
  `expect_server_mismatch` when the invocation carries `KCAP_EXPECT_SERVER_URL`
  and it does not canonicalize equal to the pinned profile's resolved URL, and
  `daemon_name_mismatch` when `DaemonNameResolver` over the pinned profile's
  `daemon.name` does not equal `--name`.
- **Retire** of an absent plist additionally requires no validated live owner
  under the retired label, else `retire_reason=live_owner_without_unit`,
  nothing touched: a detached daemon that appeared after the app's preflight
  is refused here rather than left running beside the target. The retired
  label's transaction lock is taken by the caller and held until activation is
  written, so nothing can reinstall or restart the old id in between.
- **Activation** happens after the replace matrix has confirmed the target
  label's previous owner gone, before the unit is written. Immediately before
  the write it re-probes for a validated live owner under the target label and,
  with `--retire`, under the retired label; either refuses with
  `activation_owner_alive`, nothing written. The write is one `ConfigMutator`
  mutation whose callback rechecks, on the locked config, that the pinned
  profile still exists by exact name, still resolves to the invocation's
  `KCAP_EXPECT_SERVER_URL` and still resolves to `--name`, then sets
  `active_profile`; a recheck that fails leaves the config unchanged and the
  transaction stops with `activation_stale`. A mutation that throws stops it
  with `activation_failed`. In both cases nothing is written for the new unit,
  the old owner is already gone, and `active_profile` still names the previous
  selection, so the next start reinstalls or restarts that profile's daemon.
  On success the transaction prints `activated=<profile>` to stdout and
  continues. The write is never rolled back: from that point no daemon of
  another profile is running, and the next start converges on the activated
  profile as described under Switch.

The lane maps every new token to `Failed` with the token and the Attention
surface.

`README.md`'s daemon service section and `help-daemon.txt` document the flags.

## Delivery

Three PRs, each green on its own, each referencing #1093 and AI-3072; the last
closes it.

1. **List, status, Sign in, Remove.** The tab and `ProfilesSettingsViewModel`;
   `ExpectServer`, `CredentialSaved` and the guarded token save; `ProfileRemoval`
   and the token lock seams, including the owner-aware legacy delete;
   `kcap use` deciding in its mutation; `kcap profile remove`; README and help
   text.
2. **Add.** The dialog, `IsValidProfileName`, `ExpectAbsent`, `SeedFrom`.
3. **Switch.** `--activate` with its four changes and `--retire-profile`;
   `RetireProfile`, `Activate`, `ActivationEvidence` and the restart latch with
   admission refusal; the flow; README and help text.

## Testing

- `ProfileRemovalTests` (Core): token deleted; bindings dropped; each refusal;
  a profile made active between load and mutation is refused; a delete that
  throws returns `RemovedTokenRetained` with the path; a case-alias and a
  same-name recreation keep the file with `Removed`; a refresh waiting on the
  lock while removal runs ends signed out with no file written; a refresh that
  saved before removal took the lock ends deleted.
- `TokenStoreTests`: an expired, refreshable legacy-only token is refreshed and
  migrated on the reactive and proactive paths; a save for profile B leaves
  active profile A's legacy file, also with a concurrent refresh of A; the
  guarded save skips when the profile is absent or names another server; the
  refresh path saves under the lock it already holds; no config write occurs
  under the token lock.
- `UseCommandTests`: a profile removed between read and mutation is refused on
  the global, binding and `--save` arms.
- `ProfileCommandTests` (CLI): remove prints each outcome and leaves no token.
- Facade tests: `ExpectAbsent` (including a case-variant created during
  authentication) and `ExpectServer` refuse at commit with no token written,
  for a change made before and after `LoginTarget` is built, including a
  same-server Paste; under `ExpectAbsent` the profile is created and seeded
  even when `LoginTarget` saw a same-server profile that has since vanished;
  a missing seed falls back to defaults; `CredentialSaved` is false when the
  token save throws or its guard fails, true for a `None` server; the
  foreign-profile `kcap login` save is unchanged; `active_profile` is untouched
  by a known-server sign-in.
- `ServiceVerify`: `--retire-profile` match, mismatch, unit with no
  `KCAP_PROFILE`, absent, unreadable, and the unchanged default; with
  `--activate`: `expect_server_mismatch` and `daemon_name_mismatch` refuse
  before recovery and retire; `live_owner_without_unit` for an absent plist
  with a validated owner, and the unchanged no-op without `--activate`;
  `activation_owner_alive` for an owner appearing under either label after
  the matrix; `activation_stale` for a target removed, repointed or renamed
  between viability and activation, with nothing written for the new unit;
  `active_profile` unchanged after viability, contention and retire refusals,
  written only after the old owner is confirmed gone, and still written after
  a readiness rollback, a `restore_verification` return and a rollback that
  gives up on a foreign plist; `activation_failed` stops before the unit
  write; the retired label's lock is held through activation.
- `KcapCliTests`: exact argv for a same-id request (`--activate`, no retire
  flags) and a cross-id request (all three); the factory refuses
  `retireProfile` without `retireServiceId`; `cli_unsupported` when a flag is
  missing from help at dispatch.
- `DaemonMutationLaneTests`: the restart latch is set by every dispatched
  `Activate` request, including a post-bootstrap `Refused`, not by the
  synthetic `cli_unsupported` nor by a plain rename; once set, a queued
  `Replace`, `Install` and `StartVerified` and a new request are all refused
  at admission; `ActivationEvidence` is `Activated` with the line, `Unknown`
  on a timeout and on an exit without the line, `NotActivated` on a pre-spawn
  refusal.
- `DaemonLifecycleControllerTests`: after a failure past activation the next
  start attaches to a running target daemon, installs the target's unit when
  none exists, and surfaces attention on residue; before activation, with the
  old unit retired, it reinstalls the previous profile's.
- `ProfilesSettingsViewModelTests`, in the `SettingsViewModelTests` pattern
  (`TempConfigRoot`, fake lane, confirm and relaunch): row status for each
  verdict, an empty `default`, an invalid URL, a `None` stamp and one
  unreadable token; Sign in offered on a `Complete` row; Switch offered on the
  active-but-not-bound row after an external `kcap use --global`; each switch
  gate, with the source matrix (running unit, stopped unit, absent with no
  reply, absent with a reply, unreadable); same-id and cross-id requests; the
  form's report for each `ActivationEvidence` value with the fresh active
  name; the Daemon tab's rename refused after a switch; remove gates for
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
- `kcap use --global` moving the daemon: it changes config only, as today; the
  Profiles tab's Switch on the active-but-not-bound row is the repair.
- Renaming the token files to close the case-alias sharing at its root; the
  file name is a persistence contract shared with the CLI and the daemon.
- A foreign writer replacing the target's unit after activation: the same
  exposure a rename has, surfaced as attention, not repaired.
