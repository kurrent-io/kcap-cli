# Experimental features: one switch per feature, applied live

## Intent

Codex hosted over `codex app-server` has been in daily use for weeks and is meant to become the primary Codex path. Before it does, the team wants it behind an experimental switch that is off by default and trivial to flip, so anyone can try it and anyone can back out of it in seconds

Today the switch is two environment variables (`KCAP_CODEX_TRANSPORT=app-server`, `KCAP_CODEX_APPSERVER_INTERACTIVE`), read once when the daemon starts and frozen into the service unit at install. Flipping it means re-exporting, reinstalling the service and restarting the daemon, which ends every hosted agent. Nothing in the desktop app shows or changes it

More experimental features will follow, so the mechanism is general: a list of named features, a Settings tab in the desktop app and a CLI command, all reading and writing the same stored setting

## Goals

- One stored setting per experimental feature, off by default, shared by the desktop app and the CLI
- A change takes effect in the running daemon without a restart whenever the feature allows it
- Both surfaces show what is actually in effect, not only what was saved, including why a feature that is on is not active
- Adding the next feature is one registry entry plus the code that reads it, with no new UI or command work
- The Codex app-server switch is the first feature, and the environment variables it used are removed

## Non-goals

- Server-side or tenant-wide feature gates. Those stay `Features:` in the server's `FeatureCatalog`. This is a per-machine opt-in for the local daemon, app and CLI
- Remote or admin control of a user's experimental features
- Graduating app-server to the default. That is a later change that flips the default and eventually deletes the switch

## Anti-goals

- A switch that silently does nothing. If the saved value and the effective value differ, both surfaces say so and why
- A flip that fails or kills work already running. Agents already running keep the mode they started on, and a launch already on its way is not refused because of a switch made while it was in flight
- A restart prompt for a change the daemon can absorb live

## Decisions made with the user

- **One Codex switch.** Feature id `codex-app-server`. When on, unattended reviewers and interactive Codex agents both run over app-server. It replaces both environment variables. The existing 0.146.0 version floor still applies: below it, or with an unknown version, the daemon stays on PTY
- **The CLI uses a command,** not environment variables
- **Live, not restart.** A saved change reaches the running daemon without a restart
- **Environment variables removed.** `KCAP_CODEX_TRANSPORT` and `KCAP_CODEX_APPSERVER_INTERACTIVE` are deleted from the daemon's startup binding and from the keys `ServiceEnvironment` captures into a service unit. The config is the only source

## Design

### Storage

A file of its own, `experimental.json` under the config root (`ConfigRoot.Path("experimental.json")`), mapping profile name to an object of feature id → boolean. An absent key means off. Unknown feature ids are preserved on write and ignored on read, so a newer CLI's feature survives an older app saving the file

Not inside `config.json` (my call): `ProfileConfig`, `Profile` and `DaemonSettings` carry no extension data, so any already-shipped CLI or desktop app that rewrites `config.json` (`kcap login`, a capacity change, a rename) would drop an `experimental` section and silently switch the feature off. A file older binaries never open cannot be clobbered by them. Writes take the file's own lock through `ConfigRoot.AcquireLock`

Keyed per profile, like `max_agents` and `auto_resume`, because each profile runs its own daemon (my call)

### The registry

One static list of feature descriptors in `Capacitor.Cli.Core`, each with: id, display name, one-line description, and whether it can apply live. The desktop tab and the CLI both render from it, so neither hard-codes a feature

### The daemon applies changes live

The daemon watches `experimental.json`. When its profile's entry changes, it re-resolves each feature and applies the ones that can apply live. A change from the desktop app, the CLI or a hand edit all arrive the same way

For `codex-app-server`, the effective state is always "switch on AND installed Codex meets the 0.146.0 floor", re-resolved on two triggers: a change to the switch, and every Codex version change the existing `VendorCliWatcher` already detects. So upgrading Codex past the floor activates a switched-on feature without anyone touching it, and a downgrade below the floor deactivates it (my call)

Both triggers run inside `AgentOrchestrator.RefreshAdvertisedCapabilities`, which is single-flighted, so a burst coalesces and the last publication reflects the newest state. Each pass:

1. Reads the switch and probes the Codex version once, producing one immutable resolution: switch value, probed version, effective transport for new reviewer and interactive launches, launcher policy version, and a generation number
2. Re-reads the switch after the probe; if it changed during the probe, discards the resolution and reruns the pass
3. Commits the resolution with a single reference swap. The launch router, the capability advertisement and the status snapshot all read that one reference, and `ComputeUnattendedVendorCapabilities` takes the Codex version and transport from it rather than probing or reading `DaemonConfig` again, so a policy, a version and a transport from different generations can never be published together
4. Pulses `DaemonStatusNotifier` on every commit, whether or not the advertisement or the model catalog changed
5. Re-registers with the server. A failed re-registration leaves the committed resolution in place (it already governs routing) and is reported in status as "applied locally, server not yet told" until a later re-registration succeeds

Running agents are untouched

### The switch decides new work only

The switch picks the transport for a launch that has none pinned. A launch that already names its transport keeps it whatever the switch says now (my call, from the anti-goals):

- a resume or restart-resume names the transport its agent ran on, from the resume ledger (`RequestedTransport`, as today)
- a server launch, reviewer or interactive, carries the transport the server expected for it (below)

Launch classes the switch does not touch (my call, matching today's router): PR reviews stay on PTY, and local terminal spawns (`kcap agent start`, which bypasses the hosted runtime factory) stay on PTY. "Reviewers" in this design means unattended review-flow reviewers

`CodexHostedAgentRuntimeFactory.UsesAppServer` therefore stops gating a pinned transport on `CodexAppServerActive`. Its only gate for a pinned app-server launch is the version floor, probed on demand when the switch is off and no resolved version is at hand. A pinned app-server launch on a Codex below the floor is refused, as a parked reviewer's PTY resume is today, never silently moved to PTY

A feature that cannot apply live (none yet) is marked so in the registry; both surfaces then say it takes effect when the daemon next starts

### A launch caught mid-switch

The server records the transport a launch must register with at the moment it dispatches the launch, from the daemon's last advertisement (`InteractiveLaunchIntentRecorder.ExpectedTransport` for interactive, the reviewer launch expectation for reviewers). It refuses a registration that reports anything else. A launch dispatched just before the daemon re-advertises but started just after the switch would be refused

Required behaviour: the daemon launches in the transport the server expected for that launch

One additive field on the launch command (`LaunchAgentCommand`), `ExpectedRuntimeTransport`, carries the transport the server will validate the registration against, and the router honours it as a pinned transport:

- **Interactive:** the server fills it from the transport it records in the launch intent
- **Reviewers:** the server fills it from the reviewer launch expectation it already derives from the selected daemon's advertised policy (`RuntimeTransports.FromLauncherPolicy`). The launch command carries no other usable policy for Codex: `ReviewerCertification.RequiredLauncherPolicyVersion` is optional and its daemon-side check only knows Claude's policy, so the router cannot infer a reviewer's transport today. Because the field is part of the staged command body, a reviewer launch replayed after recovery, however long after a flip, still names the transport it was staged with

Rules for the field:

- present and `app-server`: launch on app-server if the installed Codex meets the floor, otherwise refuse the launch with a reason naming the version. Never fall back to PTY, which the server would refuse anyway
- present and `pty`: launch on PTY
- present and unrecognised: refuse the launch
- absent (an older server): use the committed resolution, as today

Hub payload rules in the server's `docs/gotchas/SIGNALR.md` apply: a new field on the existing record, never a new arity. Adding the field to the staged reviewer command changes its stored body and hash; the server change has to keep already-staged commands without the field replayable

Acceptance cases the plan must cover, in both flip directions: an interactive launch dispatched before a flip and started after it; a reviewer launch likewise; a reviewer launch replayed from staging after a flip; a flip while the version probe is running; a resume and a restart-resume of an agent on each transport

A launch expecting app-server that arrives after the switch went off runs on app-server. That starts one app-server agent after the switch is off, which is the right outcome: refusing it fails work the user did not ask to stop

### Desktop app

A new "Experimental" tab in the Settings window, after Privacy, built from the registry. One toggle per feature, its description, and a status line from the daemon's status snapshot:

- on and active
- on but not active, with the reason (for Codex: the installed version is below 0.146.0, or the version could not be read)
- off
- applying: saved, and the daemon has not yet reported a resolution that includes it
- applied locally, server not yet told (re-registration failed)
- unknown: the running daemon is too old to report experimental features, so it keeps its previous mode until it is updated and restarted
- daemon not running: the setting is saved and applies when it starts

Saving writes the attached profile's entry in `experimental.json`. The tab never restarts the daemon

### CLI

```
kcap experimental                              # list features: saved state, effective state, reason
kcap experimental enable <feature>
kcap experimental disable <feature>
```

`enable` and `disable` write the active profile's entry in `experimental.json`, then wait briefly for a status snapshot from the active profile's daemon and print one line:

- applied live: only once a snapshot reports a resolution generation that includes the new saved value
- saved but not active, with the reason
- applied locally, server not yet told
- saved; the running daemon is too old to apply it, update kcap and restart the daemon
- saved; the daemon did not confirm in time, and `kcap experimental` shows its state
- saved; the daemon is not running and applies it when it starts

An unknown feature id is refused with the list of known ids

### Effective state reporting

The daemon's local status (`DaemonStatusDto`) gains, per experimental feature, the saved value it last resolved, the effective state, the resolution generation, and a reason whenever saved and effective differ or the server has not been told. Both surfaces read it, and compare its saved value with the file to tell "applying" from "applied". An older daemon that sends nothing is shown as unknown, never as off

### Leftover environment variables

An existing service unit may still carry the two removed variables, which would now be ignored. When the daemon starts and sees either one, it logs a single warning naming `kcap experimental enable codex-app-server`. It does not copy them into the config (builder's call)

## Constraints

- Changing what the CLI captures into service units needs no reinstall to take effect for the removal: a unit that still sets the variables only triggers the warning
- The status and launch-command additions are additive; an older server, app or daemon on either end keeps working
- The server change for the launch-command field ships in kcap-server; the rest ships in kcap-cli
- Local control IPC is append-only and capability-gated: any new frame or field follows `FrameType` and `LocalControlCapabilities` rules
- The new command and the removed environment variables go into `README.md` and the CLI help text in the same change

## Left to the builder

- How the daemon watches the config file (watcher, debounce, tolerance for partial writes and the existing config lock)
- The exact shape of the status addition and the registry type
- Test layout, following each repo's testing rules
- Wording of the tab and command output
