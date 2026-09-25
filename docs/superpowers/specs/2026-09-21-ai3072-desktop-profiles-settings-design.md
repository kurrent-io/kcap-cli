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
profile is running under the bound name. The status snapshot a daemon returns
on attach carries its `Name` and `ServerUrl` but not the profile it resolved
at boot, and nothing compares them to the profile the app resolved: an
attached daemon serving another profile's server is reported as connected, and
two profiles that share a server and a daemon name while differing in
credentials, visibility or exclusions are indistinguishable from the snapshot.
The lane's evidence predicate compares server and name only, for the same
reason.

**The service transaction locks do not exclude a runtime start.** A daemon
takes its own runtime lock before it publishes its pid file, and
`kcap daemon start`, foreground or detached, takes that lock and a start lock,
never `ServiceTxnLock`. `DaemonPidProbe.ValidatedPid` returns null for absent
and for unusable pid evidence alike, so a null is not proof that no daemon
owns the name; the transaction's stop confirmation is the stronger predicate,
requiring both no validated pid and no hello reply.

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

**`ConfigMutator.MutateAsync` publishes over a file it could not read.** Under
its lock it reads through `LoadPure`, which discards the failure `TryLoadPure`
reports for a malformed file and hands the callback a fresh default; the
callback's result is then published in its place. A `config.json` corrupted
while a browser sign-in is open would be replaced by defaults plus the new
profile, every other profile, binding and setting lost and `active_profile`
reset. Only `TryLoadPure` tells an absent file from an unreadable one.

**The token store's writes are unlocked, and one of them deletes another
profile's credential.** The refresh path loads the token before taking the
profile's cross-process lock; under the lock it falls back to that pre-lock
copy when the file is absent, refreshes it and saves it again through
`SaveAsync`. `SaveAsync` and `Delete` take no lock. The legacy `tokens.json`
backs exactly one profile, the on-disk active one, while its per-profile file
is absent; `SaveAsync` for any profile deletes it, best-effort, so saving a
token for profile B removes active profile A's only credential, and a failed
delete leaves both files side by side. A legacy credential may carry no
`ServerUrl`, so the binding check cannot tell whose it is, and a corrupt
per-profile file deliberately does not fall back to it. Ownership follows
`active_profile` at read time, so a change of selection from A to B strands
A's legacy credential: A can no longer read it, B can claim it as a fallback,
and B's next save deletes it. `active_profile` is written by `kcap use`
(`--global`, and also without it outside a repository), by tenant discovery's
`MergeProfiles` on the GitHub and WorkOS paths, and by nothing else. The owner
check `IsLegacyOwnerAsync` reads config through `AppConfig.LoadProfileConfig`,
which can persist a v1 migration through `ConfigMutator`. `kcap logout` calls
the parameterless `DeleteAsync()`, which deletes the legacy file, every
per-profile file and every temp file with no lock; the synchronous
`Delete(profile)` has no caller. The token lock's wait is bounded at 30
seconds, a WorkOS refresh budget plus margin.

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
  verdict can hide a refresh the server now rejects — labelled **Sign in
  again** on a signed-in or expired row, so it reads as re-authentication
  rather than contradicting the status beside it; **Switch** on every row
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

### Strict config mutation

`ConfigMutator.MutateStrictAsync` takes the same lock and reads with
`TryLoadPure`; when the file exists and cannot be read it invokes no callback,
publishes nothing and throws `ConfigUnreadableException`; an absent file is a
fresh config, as today. Every mutation this spec adds goes through it: a
precondition-bearing commit (`Failed` "config unreadable", no token
published), `ProfileRemoval` (a `ConfigUnreadable` outcome, nothing touched),
`kcap use`'s decision (refused), and the `--activate` write
(`activation_config_unreadable`). Deciding a precondition or a recheck against
a synthesized default would pass it for the wrong reason and then publish the
default. Existing callers are unchanged.

### Commit guards

`CommitRequest` gains an optional precondition, evaluated inside a strict
config mutation on the config as it is at commit:

- `ExpectAbsent`: no profile whose name equals the target's ignoring case
  exists.
- `ExpectServer(url)`: the target profile exists and `ServerIdentity.SameServer`
  holds against `url`.

A failing precondition throws inside the mutation, so `ConfigMutator` publishes
nothing, and `CommitAsync` returns `Failed` naming the reason before any token
is published. The precondition is what makes `LoginTarget`'s pre-authentication
read safe: a profile repointed during authentication can neither be stamped for
the old server nor repointed back by the Paste path's `adoptServer`.

Every commit-boundary token save runs under the profile's token lock with a
guard evaluated there, and a failed guard leaves the token unwritten with
`CredentialSaved` false. The guard has two parts:

- **Existence**: a profile of this exact name exists. It applies whenever the
  profile existed at the boundary's pre-authentication read or this commit's
  config mutation created it, which is every path except a foreign `kcap login`
  for a profile that never existed, where nothing could have been removed and
  the save is unguarded as today. A foreign login for an existing profile
  passes it, since the profile need not name the server. This is what stops a
  `kcap login` that paused between its config commit and its token save from
  reviving a profile removed in between; the CLI reports "profile <name> was
  removed during sign-in; nothing saved" and exits non-zero.
- **Server**: the profile names the token's server. It applies only to a
  request carrying a precondition, the Settings operations.

The wizard's paths pass no precondition and keep their behaviour.

## Remove

`Capacitor.Cli.Core/Config/ProfileRemoval.cs`:

```csharp
public enum ProfileRemovalOutcome { Removed, RemovedTokenRetained, NotFound, IsDefault, IsActive, ConfigUnreadable }

public sealed record ProfileRemovalResult(ProfileRemovalOutcome Outcome, string? Detail);

public static Task<ProfileRemovalResult> RemoveAsync(ConfigRoot config, TokenStore tokens, string name, CancellationToken ct)
```

One strict config mutation decides and applies: refuse `default`, refuse a
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

`kcap use` decides inside a strict mutation: the profile must exist at that
moment for the global, binding and `--save` arms alike, else the command
refuses with the same message it prints today. Before its global arm's mutation, taken with
`--global` and also without it outside a repository, it migrates the legacy
credential to the outgoing active profile (see Token lock), so a selection
change never strands it.

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
  it passes. A null guard writes unconditionally, as today. A read that fails
  is not a passed guard: `TryLoadPure` hands back a fresh default config with
  its false, and deciding on that would migrate a credential to `default`,
  delete one because `default` has a file, or approve a deletion despite a
  surviving alias. The guarded save then saves nothing and `CredentialSaved`
  is false; the guarded delete retains the token and removal reports
  `RemovedTokenRetained` with "config unreadable" as detail.
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
- `MigrateLegacyAsync(owner)` settles the legacy file under the owner's lock:
  when `tokens.json` exists and `tokens/<owner>.json` does not, it moves it
  there; when the per-profile file exists, valid or corrupt, it deletes the
  legacy file, which is redundant or superseded; when no legacy file exists it
  is a no-op. It throws when the move or delete fails. Every writer of
  `active_profile` calls it for the outgoing active name, read immediately
  before its own mutation by `TryLoadPure`, and refuses the selection change
  when that read fails or the migration throws, before any service change:
  `kcap use` on its global arm, with or without `--global`; the GitHub and
  WorkOS discovery commits, before the commit boundary's config mutation; and
  the `--activate` transaction. Two writers racing each migrate the name they
  read; the file lands with the first, the second finds none. The legacy
  file is therefore never readable under a name other than the one that owned
  it when it was last the sole credential, and switching back to the previous
  profile finds its credential in its own file.
- The parameterless `DeleteAsync()` (logout) deletes each per-profile file and
  sweeps its temps under that profile's lock, one profile at a time, over the
  union of config's profile names and the files present; the legacy file goes
  under the active profile's lock, the one a legacy-only refresh holds. A
  refresh holding its lock when logout starts finishes first and its result is
  then deleted; one arriving after finds nothing on disk and writes nothing.
  The synchronous `Delete(profile)` is removed.

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
- After activation: no daemon of another profile that was running when the
  transaction wrote is running, because it confirmed both labels stopped
  immediately before writing, and only this transaction writes a unit under
  the target name. The next start attaches to the target's daemon when the
  transaction or a late boot left it running, installs one when the rollback
  left nothing, and otherwise surfaces the same attention states a failed
  rename does: a foreign or unreadable plist, a label that stayed loaded, a
  rollback that ran out of budget, or a marker left by a killed CLI, which the
  next transaction recovers.

The one thing the transaction cannot exclude is a runtime start racing the
interval between its stop confirmation and its bootstrap: `kcap daemon start`
for the old profile under the same name takes the daemon's runtime lock, not
the service transaction lock, and can win the name so that the target's
bootstrap fails and the intruder survives rollback. The repair is at attach.

### Attach identity check

`DaemonInfoDto` gains `Profile`, an appended nullable field carrying
`Profiles.Resolution.ProfileName` as the daemon resolved it at boot: null from
an older daemon, and null from a current daemon pinned by a URL override,
where the resolver selects no profile. When the lifecycle controller's attach
reaches `Connected`, it compares the snapshot's `Name`, `ServerUrl` and, when
present, `Profile` to the graph's bound resolution: its daemon name, its
canonical server and its profile name. The expected identity is that
resolution, never a re-read of `active_profile`, so an external selection
change reports nothing here and cannot move the daemon out from under the
app. On a mismatch it surfaces attention — "Daemon <name> is running for
<what differs>, not <expected>" — and:

- when the resolution carries a valid profile name, offers a **Replace** repair
  that runs the ordinary `Replace` for it, gated on idle;
- under a `KCAP_URL` or `--server-url` launch, where the resolution has no
  profile name and the factory refuses a request, reports only. The guidance
  is to relaunch without the override, or to run
  `kcap daemon service install --replace --verify --name <name> --profile <profile>`
  in a shell where `KCAP_URL` is unset: the service id comes from the
  invocation's own context, so the name must be explicit, and the unit
  captures the invoking environment, so an inherited override would be baked
  into the replacement and outrank the profile again;
- with auto actions closed after an abandoned wizard, reports only.

Wizard-first mode has no daemon graph and no check. A daemon that reports no
`Profile` is compared on name and server only, and a survivor of the runtime
race that shares both with the target is then not detected. The manual path
depends on what survived: a detached daemon with no unit is stopped with
`kcap daemon stop --name <name>` and the app restarted, because the controller
claims its startup arm on the first attach outcome only and a later stop does
not run the matrix again; a survivor with a unit is replaced with the command
above, since `kcap daemon stop` refuses a loaded service and a retained plist
sends the matrix to a start, not an install. The same check is what turns a
`kcap use --global` made while a daemon runs from a silent mismatch into a
reported one.

The lane's evidence predicate compares `Profile` the same way for an `Activate`
request: a daemon that reports a profile other than the target is not
`Succeeded`, and one that reports none is judged on server and name as today.

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

An `Activate` request gets its own process timeout: the rename's plus the
token lock's 30-second wait. The transaction's own phases already sum to the
rename timeout less its margin (target-lock wait, leftover recovery,
retired-lock wait, retirement, installation, rollback), and the preparation
step waits before any of them, so the rename timeout alone would let the app
kill a legitimately progressing cross-id transaction during its rollback.

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

`--activate` requires `--replace --verify` and a non-empty
`KCAP_EXPECT_SERVER_URL` in the invocation; either missing is an argument
error (`expect_server_required` for the variable) before anything runs. It
changes the transaction in five places; without it the transaction is
unchanged, so a rename keeps its semantics.

- **Viability** gains two checks, evaluated with the existing ones, before
  leftover-marker recovery and before retire, nothing touched on refusal:
  `expect_server_mismatch` when `KCAP_EXPECT_SERVER_URL` does not canonicalize
  equal to the pinned profile's resolved URL, and `daemon_name_mismatch` when
  `DaemonNameResolver` over the pinned profile's `daemon.name` does not equal
  `--name`.
- **Preparation**, after viability and before leftover-marker recovery, still
  with nothing touched: `MigrateLegacyAsync` for the active name from a pure
  read, on the token lock's own 30-second wait, outside the forward budget. A
  read that fails stops the transaction with `activation_config_unreadable`
  and a throw with `activation_migration_failed`. Running it
  here rather than beside the config write keeps a refresh that holds the
  outgoing profile's lock from consuming the forward budget after the old
  daemon is already stopped; the migration is harmless if the transaction
  later fails, since the owner is still the active profile.
- **Retire** of an absent plist additionally requires the retired label
  confirmed stopped by the transaction's own predicate, no validated pid and no
  hello reply, else `retire_reason=live_owner_without_unit`, nothing touched: a
  detached daemon that appeared after the app's preflight is refused here
  rather than left running beside the target. The retired label's transaction
  lock is taken by the caller and held until activation is written, so no
  service operation can reinstall or restart the old id in between.
- **Activation** happens after the replace matrix has confirmed the target
  label's previous owner gone, before the unit is written. Immediately before
  the write it confirms, by the same stop predicate, that no owner answers
  under the target label or, with `--retire`, the retired label; either
  refuses with `activation_owner_alive`, nothing written. The write is one
  strict config mutation, inside the forward budget with its lock wait
  bounded by the config lock's own timeout, whose callback rechecks, on the
  locked config, that the pinned profile still exists by exact name, still
  resolves to `KCAP_EXPECT_SERVER_URL` and still resolves to `--name`, then
  sets `active_profile`; a recheck that fails throws inside the callback so
  nothing is published, and the transaction stops with `activation_stale`; an
  unreadable file stops it with `activation_config_unreadable`. A mutation
  that throws otherwise stops it with `activation_failed`. In every case
  nothing is written for the new unit, the old owner is already gone, and
  `active_profile` still names the previous selection, so the next start
  reinstalls or restarts that profile's daemon. On success the transaction
  prints `activated=<profile>` to stdout and continues. The write is never
  rolled back: the next start converges on the activated profile as described
  under Switch.

The lane maps every new token to `Failed` with the token and the Attention
surface.

`README.md`'s daemon service section and `help-daemon.txt` document the flags.

## Delivery

Three PRs, each green on its own, each referencing #1093 and AI-3072; the last
closes it.

1. **List, status, Sign in, Remove.** The tab and `ProfilesSettingsViewModel`;
   `MutateStrictAsync`; `ExpectServer`, `CredentialSaved` and the two-part
   save guard;
   `ProfileRemoval` and the token lock seams, including the owner-aware legacy
   delete, `MigrateLegacyAsync` at every selection writer, the locked logout
   and the removal of `Delete(profile)`; `kcap use` deciding in its mutation;
   `kcap profile remove`; README and help text.
2. **Add.** The dialog, `IsValidProfileName`, `ExpectAbsent`, `SeedFrom`.
3. **Switch.** `--activate` with its five changes and `--retire-profile`;
   `DaemonInfoDto.Profile`; `RetireProfile`, `Activate`, `ActivationEvidence`,
   the profile leg of the evidence predicate and the restart latch with
   admission refusal; the attach identity check with its Replace repair; the
   flow; README and help text.

## Testing

- `ConfigMutatorTests`: `MutateStrictAsync` on a malformed file invokes no
  callback, leaves the bytes unchanged and throws; on an absent file it
  publishes the callback's result.
- `ProfileRemovalTests` (Core): token deleted; bindings dropped; each refusal;
  `ConfigUnreadable` on a malformed config with nothing touched; a profile
  made active between load and mutation is refused; a delete that
  throws returns `RemovedTokenRetained` with the path; a case-alias and a
  same-name recreation keep the file with `Removed`; a refresh waiting on the
  lock while removal runs ends signed out with no file written; a refresh that
  saved before removal took the lock ends deleted.
- `TokenStoreTests`: an expired, refreshable legacy-only token is refreshed and
  migrated on the reactive and proactive paths; a save for profile B leaves
  active profile A's legacy file, also with a concurrent refresh of A;
  `MigrateLegacyAsync` moves the file when the per-profile file is absent,
  deletes it when a valid or a corrupt per-profile file exists, is a no-op
  without a legacy file, and throws on a failed move or delete; after any of
  these, selecting an uncredentialed B reads no credential; a config made
  unreadable before the owner read refuses the selection with nothing moved;
  the guarded save skips when the profile is absent or names another server,
  and when the config is unreadable; the guarded delete retains the token on
  an unreadable config and removal reports it; the refresh path
  saves under the lock it already holds; no config write occurs under the
  token lock; logout against a refresh holding its lock ends with no file,
  for a per-profile and for a legacy-only credential, and logout sweeps temps
  without racing a live save.
- `UseCommandTests`: a profile removed between read and mutation is refused on
  the global, binding and `--save` arms; a global selection away from A with a
  legacy-only credential, with and without `--global` outside a repository,
  leaves it in `tokens/A.json`, and selecting A again reads it; a migration
  that throws refuses the selection.
- `ProfileCommandTests` (CLI): remove prints each outcome and leaves no token.
- Facade tests: `ExpectAbsent` (including a case-variant created during
  authentication) and `ExpectServer` refuse at commit with no token written,
  for a change made before and after `LoginTarget` is built, including a
  same-server Paste; a `config.json` corrupted after `LoginTarget` is built
  and before commit yields `Failed`, the on-disk bytes unchanged and no token,
  while an absent file commits into a fresh config; under `ExpectAbsent` the profile is created and seeded
  even when `LoginTarget` saw a same-server profile that has since vanished;
  a missing seed falls back to defaults; `CredentialSaved` is false when the
  token save throws or its guard fails, true for a `None` server; the
  foreign-profile `kcap login` save is unchanged for an existing and for a
  never-existing profile; a `kcap login` paused after its config commit while
  the profile is removed saves nothing and exits non-zero; GitHub and WorkOS
  discovery moving the selection from a legacy-only A to B leave A's
  credential in `tokens/A.json`; `active_profile` is untouched by a
  known-server sign-in.
- `ServiceVerify`: `--retire-profile` match, mismatch, unit with no
  `KCAP_PROFILE`, absent, unreadable, and the unchanged default; `--activate`
  refused without `--replace --verify` and with an empty or unset
  `KCAP_EXPECT_SERVER_URL`; with `--activate`: `expect_server_mismatch` and
  `daemon_name_mismatch` refuse before recovery and retire;
  `live_owner_without_unit` for an absent plist whose label answers hello with
  no validated pid, and the unchanged no-op without `--activate`;
  `activation_owner_alive` for an owner appearing under either label after
  the matrix, by hello alone as well as by pid; `activation_stale` for a
  target removed, repointed or renamed between viability and activation, with
  nothing written for the new unit; the legacy credential migrated in
  preparation, before recovery and retire, a migration delayed by a refresh
  holding the lock not shortening the forward budget, a migration that throws
  stopping with `activation_migration_failed`, and a config unreadable at
  preparation stopping with `activation_config_unreadable`, nothing touched
  in either;
  `active_profile` unchanged after
  viability, contention and retire refusals, written only after both labels
  are confirmed stopped, and still written after a readiness rollback, a
  `restore_verification` return and a rollback that gives up on a foreign
  plist; `activation_failed` stops before the unit write; the retired label's
  lock is held through activation.
- `KcapCliTests`: exact argv for a same-id request (`--activate`, no retire
  flags) and a cross-id request (all three); the factory refuses
  `retireProfile` without `retireServiceId`; `cli_unsupported` when a flag is
  missing from help at dispatch; an `Activate` request's timeout outlives a
  transaction that waits the full token lock, recovers a leftover marker,
  retires and then rolls back.
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
  old unit retired, it reinstalls the previous profile's; an attached daemon
  whose `ServerUrl`, `Name` or reported `Profile` differs from the bound
  resolution's surfaces attention with the Replace repair, refused while
  agents are active; a daemon reporting no `Profile` that matches on server
  and name is accepted; under a `KCAP_URL` launch and with auto actions closed
  the mismatch is reported without a repair, and the URL-launch guidance names
  the daemon and says to unset the override; an external `active_profile`
  change alone reports nothing; the manual sequence for a detached survivor
  (stop, restart the app) ends with the target's unit installed.
- `DaemonMutationLaneTests` (evidence): an `Activate` request whose daemon
  reports another profile is not `Succeeded`; one reporting none is judged on
  server and name.
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
  attach identity check reports the mismatch and the Profiles tab's Switch on
  the active-but-not-bound row, or the attach repair, moves the daemon.
- Excluding a runtime start from the activation interval: it would need the
  daemon's runtime lock, which a transaction that is about to bootstrap a
  daemon cannot hold; the attach identity check is the repair.
- Renaming the token files to close the case-alias sharing at its root; the
  file name is a persistence contract shared with the CLI and the daemon.
- Moving the existing `ConfigMutator.MutateAsync` callers onto the strict
  variant: the publish-over-unreadable hazard predates this work and is the
  same for every one of them.
- A foreign writer replacing the target's unit after activation: the same
  exposure a rename has, surfaced as attention, not repaired.
