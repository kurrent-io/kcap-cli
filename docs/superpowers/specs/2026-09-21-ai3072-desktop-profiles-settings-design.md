# Desktop profiles settings

GitHub #1093, Linear AI-3072, a sub-issue of AI-1656 (settings surfaces).

## Agreed outcome

The Settings window gets a **Profiles** tab beside Daemon and Notifications. It
lists every profile in `config.json` and lets the user sign in to one, add one,
remove one, and switch the active one.

- **Switch moves the app and the daemon together.** One daemon and one identity
  at a time: the service unit is replaced for the target profile through the
  mutation lane, then the app relaunches.
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
`install --replace` takes over a live validated owner whatever profile it was
pinned to, so two profiles resolving to one daemon name need nothing new.
When the names differ the old unit must be retired, and `--retire` refuses any
unit whose `KCAP_PROFILE` is not the profile being installed
(`retire_reason=foreign_profile`).

**The lane already speaks for the target.** Its executor is built per request
from `request.Profile` and `request.CanonicalServer`, so a request naming the
target profile runs `kcap` under that profile's `KCAP_PROFILE` and
`KCAP_EXPECT_SERVER_URL`, and the evidence predicate verifies the target's
server and daemon name.

**The lane latches retired service ids only.** After a rename it refuses every
request touching the old id, which is also what closes the lifecycle
controller's auto actions (`requiresAppRestart`). A same-id switch retires
nothing, so without a second latch the old process, still bound to the previous
profile, could `ensure` that profile's unit back over the new one.

**Credential status has a refresh-free evaluator.**
`OnboardingGate.EvaluateResolvedAsync(name, profile)` reads the token raw and
never refreshes. WorkOS refresh tokens are single-use and shared with the CLI
and the daemon, so a status column must not spend one.

**A known-server sign-in can create a profile and leaves `active_profile`
alone.** The wizard's Paste path calls `LoginAsync(..., adoptServer: true)`;
an absent profile is created by `PointProfileAtServer` and stamped in the same
`ConfigMutator` mutation. Tenant discovery is different: `MergeProfiles`
reassigns `active_profile`, which is why Add does not offer it.

**A new profile starts from defaults.** `PointProfileAtServer` builds an absent
profile from `new Profile()`: `default_visibility` is `org_public` and every
exclusion list is empty. A user who excluded repos on the current profile and
switches to a fresh one would record them, publicly.

**`kcap profile remove` leaves the credential.** It drops the profile and its
bindings, not `tokens/<name>.json`, and resets `active_profile` to `default`
even when `default` has no server, which sends the app to the wizard on its next
start.

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

Each row is a card in the Notifications tab's pattern:

- profile name and server URL, in text and muted tokens;
- an **Active** mark on the config's active profile, in `KcapPurple*` (location,
  not outcome);
- a status label from the gate verdict: signed in, sign-in expired, signed out,
  signed in to another server, no server; a profile whose stamp says its server
  needs no authentication reads "no sign-in needed" and never offers Sign in;
- row actions as `kcapChip` buttons: **Sign in** when the verdict is not
  complete, **Switch** on a non-active row, **Remove** on a removable row.

**Add profile** is a `kcapPrimary` button under the list. One shared `Message`
line reports refusals and confirmations, as the Daemon tab does.

`ProfileRows` is rebuilt from a fresh `ConfigMutator.TryLoadPure` read when the
window opens, after every action, and when the window is activated, so an
external `kcap use` or `kcap profile add` shows up on return to the window.

Two identities are tracked separately: the **active** profile (what
`config.json` names) and the **bound** profile (what this process resolved at
startup). They differ under a `KCAP_PROFILE` launch. The Active mark follows the
first; the gates below name which one they use.

## Sign in

Sign in on a row opens the existing sign-in dialog composed for that row:
`ReauthComposition.Build(..., profile: row.Name, serverUrl: row.ServerUrl, ...)`.
`OpenSignInDialog` takes the profile and server as arguments instead of reading
them from the bound `ProfileContext`. The post-success refresh of the app's own
server lane runs only when the row is the bound profile.

## Add

A dialog with two fields:

- **Workspace or server**: a workspace name (`acme`) or a full `https://` URL,
  resolved by `WizardSignInOperation.ResolveServer` and accepted by
  `OnboardingGate.ValidServerUrl`, the rule the wizard's Connect step uses.
- **Profile name**: defaulted from the workspace name or the URL's first host
  label, validated by `TokenStore.ValidateProfileName`, refused when a profile
  of that name exists.

Continue runs the sign-in step with a `ConnectIntent.Paste` for the new profile
name. The facade creates the profile, stamps it and saves the token in its one
commit boundary; a cancelled or failed sign-in writes nothing.

**Seeding.** `LoginAsync` gains an optional profile basis used only when the
target profile is absent: `PointProfileAtServer` builds from it instead of
`new Profile()`. Add passes the bound profile with `server_url`, `remotes`,
`import_org` and `auth_provider` cleared. `remotes` must not be copied: two
profiles matching one git remote make `RemoteMatcher` throw. `daemon` is copied,
so the new profile resolves to the same daemon name and a later switch takes the
same-id path.

## Remove

`Capacitor.Cli.Core/Config/ProfileRemoval.cs`:

```csharp
public enum ProfileRemovalResult { Removed, NotFound, IsDefault, IsActive }

public static Task<ProfileRemovalResult> RemoveAsync(ConfigRoot config, TokenStore tokens, string name, CancellationToken ct)
```

One `ConfigMutator` mutation decides and applies: refuse `default`, refuse a
missing profile, refuse the profile `active_profile` names at that moment, else
drop the profile and every binding pointing at it. `tokens.Delete(name)` runs
after a `Removed` result. Deciding inside the mutation is what stops a
concurrent `kcap use --global` from making the removed profile active between
check and write.

`kcap profile remove` calls it and prints a refusal per result; for `IsActive`
it names `kcap use <other> --global`. This changes CLI behaviour in two ways,
both documented in `README.md` and `help-profile.txt`: the token is deleted, and
the active profile is refused instead of silently reset to `default`.

The app offers Remove on a row that is neither active, bound, nor `default`,
behind a confirm dialog naming the profile and saying its sign-in is deleted.

## Switch

Switch is enabled on a non-active row when all hold, and otherwise the row says
why:

- the target's gate verdict is complete (sign in first);
- `KCAP_PROFILE` is not set (it would override `active_profile` on relaunch);
- the platform supports `--verify` (macOS);
- the startup phase has settled and the last snapshot reports zero active
  agents, an unreachable daemon counting as idle;
- the lane holds no restart latch.

Then, in order:

1. Resolve the target's daemon name with `DaemonNameResolver.Resolve([], target.Daemon?.Name)`.
   When it differs from the running name, probe the target name's socket with
   the one-shot hello the rename uses; a reply is a collision and stops here.
2. Confirm a lifecycle prompt: the daemon restarts for the target profile and
   the app relaunches.
3. Recheck settled and idle. Build the request with
   `MutationRequestFactory.TryBuild(Replace, target.Name, target.ServerUrl, targetDaemonName, out request, retireServiceId, supersedesProfile: bound.Name)`,
   `retireServiceId` being the running name when the names differ and null
   otherwise. When it is non-null, probe the lane-selected CLI for
   `--retire-profile` support.
4. Write `active_profile = target.Name`, remembering the previous value.
5. Run the request through the lane.
6. `cli_unsupported` (a pre-spawn refusal, nothing touched): restore the
   previous `active_profile` only if the config still holds the value this
   attempt wrote, and report that kcap needs updating.
7. Any other outcome sets the window's restart-required state. `Succeeded`
   relaunches the app through `RelaunchForSettingsAsync`; unbundled, the form
   says to restart. A failure is reported through the lane's outcome consumer
   and in the form, and `active_profile` stays on the target: the next start
   resolves the target and its `ensure` installs the target's unit, so a
   half-finished switch converges forward instead of leaving the app and the
   daemon on different profiles.

### Lane

`MutationRequest` gains `string? SupersedesProfile`, set only by the factory and
only for `Replace`. After a request carrying it has run, with any outcome except
`Failed { Reason: "cli_unsupported" }`, the lane latches restart-required and
refuses every later request with `profile_switched_restart_app`. `requiresAppRestart`
becomes `lane.IsRetired(name) || lane.RestartRequired`, which closes the
lifecycle controller's auto actions for the same-id switch that retires
nothing.

The `Replace` dispatch forwards `RetireServiceId` as today and, when
`SupersedesProfile` is also set, adds `--retire-profile <SupersedesProfile>`.

The rename's timeout for a request with a retire id applies unchanged.

### CLI: `--retire-profile <name>`

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

`README.md`'s daemon service section and `help-daemon.txt` document the flag.

## Delivery

Three PRs, each green on its own, each referencing #1093 and AI-3072; the last
closes it.

1. **List, status, Sign in, Remove.** The tab, `ProfilesSettingsViewModel`,
   `ProfileRemoval`, the CLI change, README and help text.
2. **Add.** The dialog and the facade's profile basis.
3. **Switch.** `--retire-profile`, `SupersedesProfile` and the lane latch, the
   flow, README and help text.

## Testing

- `ProfileRemovalTests` (Core): token deleted, bindings dropped, each refusal,
  and a profile made active between load and mutation is refused.
- `ProfileCommandTests` (CLI): remove prints each refusal and leaves no token.
- Facade tests: an absent profile is built from the basis; an existing profile
  ignores it; `active_profile` is untouched by a known-server sign-in.
- `ServiceVerify` retire matrix: `--retire-profile` match, mismatch, unit with
  no `KCAP_PROFILE`, absent, unreadable, and the unchanged default.
- `DaemonMutationLaneTests`: the latch is set by every outcome except
  `cli_unsupported`, refuses later requests, and is not set by a plain rename.
- `ProfilesSettingsViewModelTests`, in the `SettingsViewModelTests` pattern
  (`TempConfigRoot`, fake lane, confirm and relaunch): row verdicts, each switch
  gate, same-id and cross-id requests, restore only when the config still holds
  this attempt's value, forward convergence on failure, remove gates for active,
  bound and `default`, re-read on activation.
- `SettingsWindowSmokeTests`: the tab and its named controls, `kcapChip` and
  `kcapPrimary` classes, and the Active mark using a purple brush, not a
  success one.
- `dotnet publish -c Release` for IL warnings on each PR.

## Out of scope

- Tenant discovery from Settings (a merge that does not reassign `active_profile`).
- Per-profile sign-out.
- A config file watcher.
- Settings when no profile resolves (`KCAP_URL` or `--server-url` launch).
- Running daemons for two profiles side by side.
