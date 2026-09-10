# Desktop daemon settings

GitHub #791 / AI-2535, the first slice of the settings surfaces tracked as AI-1656.

## Agreed outcome

The desktop app gets a Settings window, reachable from a native application menu
item (⌘,) and from the tray, with one **Daemon** group: the daemon's name and the
number of agents it may run at once. Capacity applies to the running daemon
without a restart. A rename restarts the daemon under the new name and relaunches
the app, because the name is the daemon's identity everywhere, not a label.

The user chose **restart to rename** over a live rename, a capacity-only slice, or
a separate display name, and a **separate window** over a third area in the main
window. Delivery is two PRs: backend first, then the app.

## What the code imposes

**Capacity is a plain live field.** `DaemonConfig.MaxConcurrentAgents` is read per
launch by the orchestrator and per push by the status snapshot; nothing caches it.
The daemon already re-sends its connect payload on the same server connection
through a single-flighted trigger (the vendor-CLI watcher uses it), and the server
treats a repeat connect on the same connection as an idempotent overwrite of the
entry, capacity included.

**The name is identity.** Locally it keys the lock, pid, socket and state paths
through `DaemonStore.Sanitize`, is the launchd label, and is baked into the
service unit as `--name`. On the server it is the slot key (team, owner, name);
a repeat connect under a new name would leave the old name mapped to the same
connection, and there is no rename endpoint. In the app, the attach client, the
control ops, the consent and permission subscriptions and the activity log path
are all bound to the name when the daemon graph is built, once per process.

**The startup precedence has a sentinel bug.** The profile's `max_agents` is
applied only while the live value still equals the default 5, so a profile set to
exactly 5 is indistinguishable from unset and an explicit `--max-agents 5` is
overridden by the profile.

**The app must not orchestrate destructive sequences.** The daemon-lifecycle
design makes the CLI the safety boundary: the app issues one verb through the
mutation lane and the CLI runs it as one transaction.

## Capacity

### Persistence and live apply

Saving a changed capacity does two things in this order:

1. Write `daemon.max_agents` on the active profile through `ConfigMutator`, the
   same lock and profile resolution the onboarding defaults step uses. This is
   what survives a restart.
2. If the daemon is attached and advertises `settings/1`, push the value over the
   local control socket. The ack decides what the form reports.

Config first, so a live push that fails leaves a durable value rather than a live
value that the next start forgets.

### IPC

A new frame pair in `Capacitor.Cli.Core.LocalIpc`, appended, never renumbered:

- `FrameType.DaemonSettingsPut = 23` (client → daemon), payload
  `DaemonSettingsPutDto { max_agents: int? }`.
- `FrameType.DaemonSettingsAck = 81` (daemon → client), payload
  `DaemonSettingsAckDto { ok: bool, reason: string?, max_agents: int? }` where
  `max_agents` echoes the value now in effect.

Payloads live in a new `SettingsIpc.cs` with their own snake_case
`JsonSerializerContext`, nulls written, members trailing and nullable so later
settings are additive. Reasons: `malformed` (unparseable or no field set) and
`invalid_max_agents` (below 1). The capability string is `settings/1`, added to
`LocalControlCapabilities.Current` beside its `LocalControlServer` case arms.
`ILocalControlOps` gains `PutDaemonSettingsAsync`, with the same transport
exceptions as the consent put; the two fakes that implement the interface gain
it too.

No Get frame. The form reads what will apply from the profile file and what is
running from the status stream the app already subscribes to, which carries
`name` and `max_agents`.

### Daemon handler

A `DaemonSettingsIpc` service, registered like `LaunchConsentIpc`. On a valid put
it sets `MaxConcurrentAgents`, pulses `DaemonStatusNotifier` so status
subscribers re-read, and asks the orchestrator to republish its registration
through the same single-flighted re-register trigger the vendor-CLI watcher
uses. The ack returns as soon as the local value is set; the server learns on the
republish. Lowering the cap below the current count refuses new launches and
evicts nothing. An invalid put changes nothing and acks with its reason.

### Startup precedence

`DaemonRunner` records whether `--max-agents` was passed. Without it, a profile
`daemon` section sets the value whatever it is; with it, the flag wins.
`KCAP_MAX_AGENTS` keeps overriding both.

## Rename

### App flow

The name field accepts only what `DaemonStore.Sanitize` returns unchanged, so
the daemon runs under exactly the typed name; anything else shows an inline
error. A changed name enables a separate action, **Rename and restart daemon**,
gated on all of:

- the last status snapshot reports zero active agents, an unreachable daemon
  counting as idle (otherwise the action is disabled and the form says how many
  are running);
- a one-shot hello probe of the target name's socket gets no well-formed reply
  (otherwise "a daemon named X is already running on this machine");
- the user confirms a lifecycle prompt saying the daemon restarts and the app
  relaunches.

Then, in order:

1. Write `daemon.name` on the active profile.
2. Run the mutation lane's `Replace` verb for the new name with the old sanitized
   id as `RetireServiceId`, a new optional field on `MutationRequest` that only
   the factory sets and only the `Replace` dispatch forwards to
   `IKcapCli.ServiceInstallVerifiedAsync`.
3. On `Succeeded`, relaunch the app: `open -n` on the bundle root the
   install-location guard already detects, then shutdown. Unbundled (a dev run)
   there is no bundle to reopen, so the form says to restart the app instead.
4. Any other outcome goes through the lane's existing outcome consumer and is
   also shown in the form. The profile keeps the new name: the next app start's
   `ensure` installs the new unit.

The rename runs whether or not the daemon is attached. A stopped but installed
unit under the old name would otherwise start at login beside the new one.

The lane's evidence predicate verifies the request's daemon name, which is the
new one, so a success means the renamed daemon answered hello with a matching
pid and name.

### CLI: `install --replace --verify --retire <id>`

`--retire` names a service id (sanitized on parse) whose unit is removed inside
the same verify transaction. Rules:

- Requires `--replace --verify`; equal to the target id is an argument error.
- Runs after the viability checks and before the replace matrix, so a refusal
  touches nothing.
- Absent unit: no-op. This is what makes a rename from a detached or never
  installed daemon work.
- Present unit whose plist `KCAP_PROFILE` is not the profile being pinned:
  refused with a coded stderr reason, nothing touched. A retire never removes
  another profile's daemon.
- Otherwise: uninstall through the service manager with the same stop
  confirmation the replace matrix uses; a failure to confirm is a coded refusal
  and the transaction stops there.
- With `--retire`, a live validated owner under the **target** label is
  contended, never taken over: for a rename that is a collision, not a stale
  unit.

Rollback keeps its existing meaning, uninstalling only the unit this transaction
wrote; a retired unit is not restored. The lane maps the new coded exits to
`Failed` with the reason token and the Attention surface.

`--verify` stays macOS-only, so rename is too, like every other lane verb. The
README's daemon service section documents the flag.

## Settings window

`Views/SettingsWindow.axaml`, a fixed-size window in the Kcap token kit, single
instance held by the app the way the sign-in window is: opening it again brings
the open one forward. `ViewModels/SettingsViewModel.cs` follows the ReactiveUI
conventions: properties raised through `RaiseAndSetIfChanged`, commands from
`ReactiveCommand.CreateFromTask` gated on `!IsBusy`, a nullable `Message` for
errors and confirmations, dependencies injected from `App.axaml.cs` with fakes
for tests.

Contents, one **Daemon** group:

- **Name**: text box, initial value from the profile.
- **Capacity**: numeric input, minimum 1, initial value from the profile.
- **Status line** from the daemon client service: "Running as *name*, N of M
  agents", or "Daemon not running. Changes apply when it starts."
- **Save** for capacity, enabled when the value changed and is valid.
- **Rename and restart daemon** for the name, with the gates above.

Save reports one of: applied live; saved, daemon not running; saved, the daemon
needs an update to apply this without a restart (attached, no `settings/1`);
saved, could not reach the daemon (transport failure after the config write); or
the daemon's own refusal reason.

Entry points:

- A native application menu on `Application` with **Settings…** on ⌘,. The
  item is enabled once the daemon graph exists; wizard-first mode has no profile
  to edit.
- A **Settings…** tray item after **Open Kurrent Capacitor**.

Later AI-1656 groups (profiles, recording, consent rules, app preferences) are
added beside the Daemon group in the same window.

## Delivery

Two PRs, each green on its own, both referencing #791 and AI-2535:

1. **Backend**: frame pair, capability, `ILocalControlOps` member and fakes,
   daemon handler and republish trigger, startup precedence fix, `--retire`,
   README and `docs/CHANGES.md`.
2. **App**: window, view model, application menu, tray item, `RetireServiceId`
   through the lane and executor, relaunch.

## Testing

- **Core**: frame byte pins and codec round-trips for 23 and 81, wire-shape
  tests for both payloads, scripted-server tests for the new op including a
  malformed reply.
- **Daemon**: a real-socket test through the routing switch that a put changes
  the next status snapshot, makes the orchestrator refuse the next launch over
  the new cap, and re-registers through the capture connection; an invalid put
  changes nothing; the capability list pin gains `settings/1`; runner tests for
  each precedence arm including a profile value of exactly 5.
- **CLI**: `--retire` refused without `--replace --verify` and when equal to the
  target; absent unit is a no-op; foreign-profile unit refused with nothing
  written; a live target label is contended; the happy path retires then installs
  through the fake service manager.
- **App**: view-model tests for dirty and valid gating, the config-then-push
  order, every Save report, the rename gates (agents running, live target,
  declined prompt), the request the lane receives, and the relaunch versus
  restart-the-app branch; a headless smoke render of the window; a tray-builder
  test for the new item and an application-menu test for the gesture.

## Out of scope

Live rename, a separate display name, a server-side rename endpoint, rename on
Linux and Windows, and every other settings group listed for AI-1656.
