# #1351 — Reload a daemon still running as launchd Adaptive, from the desktop app

## Problem

A macOS daemon whose LaunchAgent plist declares `ProcessType=Adaptive` runs in the background QoS band: launchd holds an adaptive job at priority 4 until it receives a Mach importance boost over XPC, and a Unix-socket daemon never receives one. Every hosted agent and every process those agents spawn inherits the band. Under memory pressure the band is throttled on CPU and I/O, so the daemon and the agents on the keystroke path stall while the foreground desktop app stays responsive: terminals redraw late and typed input lags, worse the more agents run.

New installs write `Standard`. An existing plist is brought across by `kcap daemon service refresh`, which runs after an update, but three things keep that from reaching a busy machine:

- The refresh reloads the job only after the daemon accepts an idle-only restart. A machine that always has agents running never idles, and nothing tells the user the plist is still Adaptive.
- The upgrade matches the writer's single-line `ProcessType` element byte for byte. A plist in Apple's canonical layout (tab-indented, one element per line) is left as it is.
- `kcap daemon restart` does not apply a rewritten plist: KeepAlive relaunches the job from the definition launchd cached at load. No in-process call lifts the band either; `taskpolicy -B` exits 0 and changes nothing on the daemon or on a child.

## Goal

The desktop app shows when its daemon runs in the background band and offers the reload that fixes it, with the consequences stated before it runs, and the result proven from evidence rather than from the click.

## Out of scope

- The daemon detecting its own scheduling band from inside the process. launchd is the authority and the CLI asks it directly.
- An app-driven reload that waits for the daemon to be idle, and a daemon-side idle-only reload that refuses a newly busy state atomically. The reload here is always disclosed as destructive, so no idle check needs to be exact.
- Teaching `kcap daemon restart` to reload the job. The app does not use that command, and `service refresh --force` covers the terminal user.
- Windows and systemd. Neither has a process type with this behaviour.
- Showing a Reload failure in the tray. The reload is started from the window, and its outcome is shown beside the button that started it.

## Design

### Spawn-type vocabulary

`launchctl print` reports a loaded job's type on one line, `spawn type = <word> (<n>)`. The words observed on macOS 26 are `daemon (3)` for a Standard job and for a plist with no `ProcessType`, `interactive (4)`, `background (5)`, `adaptive (6)` and `app (1)`. A new `LaunchdUnit.LoadedSpawnType(printStdout)` returns the word, or null when the line is absent, and a new `SpawnTypes` classifier gives each word one of three readings:

| Reading | Words | Meaning |
|---|---|---|
| positive | `daemon`, `interactive` | the priority goal is met |
| background band | `adaptive`, `background` | the daemon is throttled; the reload is wanted |
| unknown | any other word, or no line | nothing is established either way |

Only a positive reading ever counts as success, in the CLI and in the app. `LaunchdUnit.LoadedAsAdaptive` is replaced by this classifier everywhere it is read, including the timed-out-bootstrap success path in `LaunchdServiceManager.Bootstrap`: a bootstrap that timed out counts as a reload only when the follow-up print finds the label loaded with a positive spawn type. A loaded label whose print has no spawn-type line is not a reload.

The two background-band words come from two plist values, `Adaptive` and `Background`. The writer has only ever produced `Adaptive`, but the plist is kcap's own file either way, so both values are repaired the same way: rewritten to `Standard` and reloaded.

### CLI: data and flags

**Loaded spawn type in the query.** `LaunchdServiceManager.QueryCore` already runs `launchctl print` and classifies the label. `ServiceQuery` gains a trailing `string? LoadedSpawnType`: the word when the label is `Loaded`, null when it is not, when the print has no such line, and for every non-launchd manager.

**Status JSON.** `ServiceStatusJson` gains a trailing `string? LoadedSpawnType = null`, serialised as `loaded_spawn_type` by the existing snake-case source generator. Older CLIs omit the key; a reader treats absence as unknown.

**Status text.** `kcap daemon status` prints, under the existing `service:` line and only for a background-band word, naming the daemon it describes:

```
  priority: loaded as <word> — background priority; run `kcap daemon service refresh --name <daemon> --force` to reload (ends this daemon's hosted agents)
```

**Layout-independent upgrade.** `LaunchdUnit.UpgradeProcessType` locates the value through the parser, then edits the text:

1. Parse with `LoadOptions.SetLineInfo`. Find the unique top-level `ProcessType` key through the existing strict key/value walk; a duplicate key, a nested-only key, or no key returns null. A parse or strict-walk exception is contained and returns null.
2. The paired value must be a `string` element whose only child is the text `Adaptive` or `Background`; a comment or CDATA inside it, or any other value, returns null.
3. Map the element to a source offset. Its `IXmlLineInfo.LinePosition` is the column of the element name's first character, so the opening `<` sits one column earlier; the offset is found by walking the source's own line endings, LF or CRLF, to the reported line. The bytes at that offset must read exactly `<string>Adaptive</string>` or `<string>Background</string>`; otherwise return null.
4. Splice `Standard` over the value at that span and return the result only when `DeclaresStandardProcessType` accepts it.

Every other byte survives, including comments elsewhere, CRLF line endings and a missing final newline, as `WithBinary` already guarantees for the binary path. A comment or whitespace between the key and its value is skipped by construction because the splice targets the value element's own span.

A null means there was nothing to splice; it is not a verdict on the plist. `RefreshUnit` keeps validating `upgraded ?? original` with `DeclaresStandardProcessType`, as it does today, so a plist that is already Standard on disk stays eligible for the binary repoint through `WithBinary` and for the reload of a stale loaded job. That is the first live case: the plist on the diagnosing machine was edited to Standard by hand while launchd still holds the Adaptive definition it cached at load, and a forced refresh must reload it. Only a plist that fails `DeclaresStandardProcessType` after the attempted upgrade is unsupported, and only that never leads to a write or a restart request.

**Refresh outcomes.** `UnitRefresh` distinguishes what today's `Unchanged` folds together. The forced run reports exactly one of them on stderr as `refresh_outcome=<token>`:

| Token | Meaning | Forced exit |
|---|---|---|
| `reloaded` | the job was booted out and bootstrapped from the plist on disk (rewritten now, or already Standard), and the loaded spawn type reads positive | 0 |
| `current` | the plist declares Standard, the loaded job runs the plist's binary, and the loaded spawn type reads positive | 0 |
| `not_loaded` | the plist is current or was rewritten, but the label is not loaded | 1 |
| `deferred` | the daemon refused the restart, or the time left was under the reload reserve | 1 |
| `contended` | another service operation holds the label's transaction lock | 1 |
| `unverified` | launchd's state could not be read: the print timed out, or failed for a reason other than an absent label; or the loaded spawn type reads unknown | 1 |
| `unit_missing` | no plist at the path | 1 |
| `unit_unreadable` | the plist exists but cannot be read | 1 |
| `unit_unsupported` | the plist cannot be parsed as a plist (malformed XML, duplicate keys) or does not declare Standard after the attempted upgrade (no `ProcessType`, or a value other than Adaptive, Background or Standard) | 1 |
| `failed` | bootout or bootstrap failed, or a unit write or a `launchctl` start threw; the detail follows on its own stderr line | 1 |

`RefreshUnit` itself contains exceptions from the unit writer and from starting `launchctl` and returns `Failed` with the message, so the command only maps outcomes to tokens and the token line is always present and always single. The unforced refresh keeps today's output and exit codes for every outcome: its "busy" line additionally names `kcap daemon service refresh --name <daemon> --force` as the way to apply the change now, and a lock contention prints that another service operation is in progress and the change waits for the next update.

**Forced refresh.** `kcap daemon service refresh --force` targets the daemon the command resolved (`--name`, else the profile's), not every installed one, and passes `RefreshUnit` a restart request in mode `force` instead of `now`. The daemon then ends its agents and exits, and the existing sequence boots the job out while it is exiting and bootstraps the plist on disk. `DaemonServiceCommands.RequestIdleRestart` becomes `RequestRestart(serviceId, mode)`; the acknowledgement it accepts is unchanged (`RestartAck` with text `restarting`).

**Transaction lock.** Every `RefreshUnit` caller serialises on the per-label `ServiceTxnLock` that install, start and uninstall already take. The forced run waits up to 10 s, like a plain install, and reports `contended` on timeout. The unforced run tries once without waiting and defers on contention. No plist write and no restart request happens without the lock.

**Budgets.** The command's refresh deadline stays 55 s, inside which the 47 s reload reserve applies after the initial query. The app's mutation bound of 60 s covers the whole command.

**README.** The `kcap daemon service` section documents `--force`, the outcome line and the status line.

### App: lane verb and classification

**Snapshot.** `ServiceSnapshot` gains `string? LoadedSpawnType`, deserialised from `loaded_spawn_type`, read through the same three-way classifier.

**Verb.** `MutationVerb.Reload` joins the lane. `IKcapCli.ServiceReloadAsync(ct)` runs:

```
kcap daemon service refresh --name <daemon> --force
```

under the existing 60 s mutation bound.

**Pre-dispatch gate.** An older CLI ignores both `--force` and the `--name` scoping and runs its all-daemon idle refresh, so the capability must be proven before anything is spawned, on the executable that will run the mutation. After the existing version-floor probe, the lane reads the pinned executor's service status: a snapshot that carries no spawn type, or no snapshot at all, is `Refused("reload_unsupported", Attention)` and nothing is dispatched. The controller's own earlier snapshot is not evidence here; the lane and the controller can resolve different executables.

**Classification.** A timed-out run is `UnconfirmedNoAttach`. A non-zero exit is `Failed(exitCode, token, Attention)` with the token from `ReasonLine.TrySingle(stderr, "refresh_outcome=")`. Exit 0 is confirmed in two steps:

1. *Readiness window.* `RefreshUnit` returns as soon as `launchctl bootstrap` succeeds, before the successor has bound its socket, and the lane's one-shot observation never retries. So Reload polls the observation for up to the existing 10 s detached confirmation window, at its 1 s interval, until the shared evidence predicate passes or the window ends. The window ending on an unreachable daemon is `UnconfirmedNoAttach`; on any other failing leg it is that leg's skew.
2. *Shared predicate plus priority evidence.* Once the observation passes, `ClassifyServiceSuccessAsync` runs unchanged, with one leg added after the ownership repair leg and before the pid checks, read from the ownership snapshot the pinned executor just took: a background-band word is `AttentionSkew("background_band")`; an unknown reading, including a missing field, is `AttentionSkew("spawn_type_unknown")`; only a positive word lets the leg pass.

Exit 0 with `current` passes the same two steps and is a success: the daemon runs at standard priority, which is what the user asked for.

**One presenter.** Every other verb's failure goes through the outcome channel to the app's presenter and from there to the shared attention lane. A Reload outcome does not: the controller that started it awaits the lane's outcome and is its only presenter. The channel consumer acknowledges a Reload envelope and logs its token without posting anything. The shared attention lane is a replaying subject whose text the window copies into a field that ignores null, so a message posted there cannot be withdrawn once the condition it described is gone; a Reload outcome, which is resolved by later evidence, must not live there.

**Controller.** `DaemonLifecycleController` gains:

- `IObservable<bool> BackgroundPriority`, a replaying subject set from every `ServiceSnapshot` the controller reads: true for a background-band word, false for a positive word, unchanged for an unknown reading or a failed read. A passive status read runs on every transition into `Connected`, not only the once-per-run arm, so a daemon that connects later, or is reloaded outside the app, updates the indicator. The passive read never arms a mutation.
- `IObservable<ReloadState?> ReloadState`, a replaying subject: null, or the last Reload outcome that still stands, as a record of the token, the exit code when there is one, the daemon name and a sequence number. All writes to it happen under the controller's lock, in the order the controller observes them.
- `IObservable<bool> IsReloading`, true from an accepted prompt until the lane's outcome has been recorded. A click while it is true is ignored, so at most one reload is in flight and outcomes arrive in click order.
- `Task ReloadServiceAsync(CancellationToken ct)`: captures the attach generation, shows the reload prompt, and cancels with a status line if the generation changed while the prompt was open, as `ConfirmAndReplaceAsync` does. On accept it sets `IsReloading`, awaits the lane's outcome for `MutationVerb.Reload`, records it, clears `IsReloading`, then re-queries the service status so the indicator follows evidence. `Succeeded` records null and posts "Daemon runs at standard priority." on the status lane; every other outcome records its token.

The prompt is shown on every click. The count it names is the latest `DaemonInfoDto.ActiveAgents`, but the disclosure states that any agent, pending launch or evaluation running when the daemon exits ends with it, so a launch that lands after the count was read is covered by the consent given.

**Resolution.** `ReloadState` changes in exactly three ways, all under the controller's lock:

- A new outcome replaces whatever stands, in click order.
- A passive status read with a positive spawn type clears a standing outcome whose sole complaint was the priority state or a transient inability to act, that is `background_band`, `spawn_type_unknown`, `deferred`, `contended` or `unverified`, and only when the read started after that outcome was recorded. The controller takes one monotonic counter under its lock for both outcome records and read starts, so a read that began before the failure cannot clear it. Every other token stays until a later `Succeeded`, because a positive spawn type does not disprove it: launchd keeps a loaded `daemon` job after its plist is deleted, and an ownership mismatch is independent of priority.
- A `Succeeded` outcome clears whatever stands.

There is nothing to withdraw from a message lane and nothing to intercept in a queue: the view renders the state, so the moment it changes the text changes with it, in an open window as much as in a fresh one.

### App: indicator and prompt

**Indicator.** `MainWindowViewModel` gains `BackgroundPriority`, `BackgroundPriorityText`, `ReloadFailureText`, `IsReloading` and a `ReloadDaemonCommand`, fed by the controller's observables through constructor parameters and gated to a connected attach like `RestartPending`. The indicator text is

> Daemon runs at background priority — agent terminals lag under load.

In `SessionRailView.axaml` one block sits beside the update-pending indicator, as `Classes.backgroundPriority`, in `KcapWarning*` tone because it is a needs-you status. It holds the indicator line, a `kcapGhost` "Reload" button bound to the command and disabled while `IsReloading`, and under them the failure line, visible only while `ReloadFailureText` is non-null. The block is visible while either the indicator or a failure stands, so a failure that outlives a lowered indicator, such as `unit_missing` after a positive read, remains readable until a success clears it. The update-pending indicator is unchanged.

**Failure copy.** `ReloadCopy.For(ReloadState)` builds the failure line, naming the daemon and the terminal command: `reload_unsupported` ("This kcap CLI cannot reload the daemon service. Update kcap, then press Reload again."), `background_band` ("The daemon still runs at background priority after the reload. Run `kcap daemon service refresh --name <daemon> --force` from a terminal and check `kcap daemon status`."), `spawn_type_unknown` ("The reload finished but the daemon's priority could not be confirmed. Check `kcap daemon status --name <daemon>`."), `deferred`, `contended`, `not_loaded`, `unverified`, `unit_missing`, `unit_unreadable`, `unit_unsupported`, `failed`, the unconfirmed outcome ("The daemon reload is not yet confirmed — check `kcap daemon status --name <daemon>`."), and a fallback for any other token, including the exit-code token an absent or malformed reason line produces ("The daemon reload for <daemon> failed (exit N). Check `kcap daemon status --name <daemon>`; details are in the app log."). No copy claims the daemon restarted unless `reloaded` was established. The raw token is logged alongside.

**Prompt.** `LifecyclePrompt.KindReloadService` is a new kind. `LifecyclePromptViewModel` titles it "Reload the daemon service" and labels accept "Reload now"; decline stays visible. The controller builds the disclosure:

> Reloading restarts the daemon and ends everything it hosts: N agent(s) now, plus any agent, launch or evaluation running when it exits. Uncommitted work in their worktrees is lost.

## Testing

**CLI (`test/Capacitor.Cli.Tests.Unit`).**

- `LaunchdUnitTests`: `LoadedSpawnType` reads `daemon`, `interactive`, `background`, `adaptive` and `app` shapes and null when the line is absent; the classifier maps each word and an unknown word. The upgrade handles the single-line writer layout and a tab-indented one-element-per-line plist with every other byte intact, including CRLF and no final newline, using real `XDocument` line information, for both `Adaptive` and `Background`; returns null for a missing key, a duplicate key, a nested-only key, an already-Standard value, `Interactive`, a CDATA value, a comment inside the value and malformed XML; a comment elsewhere containing the Adaptive pair is untouched while the real value is upgraded; a comment between the key and its value does not block the upgrade.
- `LaunchdUnitRefreshTests`: a forced refresh sends mode `force`, reloads a daemon that reports agents, and returns `Reloaded` when the post-reload print reads positive; a forced refresh of a plist already Standard on disk under an Adaptive-loaded job is `Reloaded` without a write; the existing `Rewritten_plist_with_an_adaptive_loaded_job_is_still_reloaded` and the `ServiceRepointTests` keep passing; a loaded `background` job with a `Background` plist is rewritten and `Reloaded`; an unforced one still defers; a timed-out bootstrap whose follow-up print finds the label loaded with no spawn-type line, or with a background-band word, is not `Reloaded`; each of `current`, `not_loaded`, `deferred`, `unverified` (timed-out print, failed print, unknown word), `unit_missing`, `unit_unreadable` and `unit_unsupported` (malformed, no key, `Interactive`) is returned from its fixture with no write and no restart request where the table says so; a held transaction lock yields `contended` without a plist write or a restart request; a throwing unit writer and a throwing process start each yield `Failed`.
- `DaemonServiceCommands` tests: the forced command prints exactly one `refresh_outcome=` line and the exit mapping above for every outcome; with two installed daemons, `--force` touches only the named one and the unforced run still iterates both; the status hint and the deferred line carry `--name`.
- `ServiceStatusJsonTests`: the field round-trips as `loaded_spawn_type`; a document without the key deserialises to null.
- `DaemonStatusServingTests` or a sibling: the priority line appears only for a background-band word and names the daemon and the word.

**App (`test/Capacitor.App.Tests.Unit`).**

- `KcapCliTests`: the snapshot parses the field as a word, as null and as absent; `ServiceReloadAsync` passes the exact arguments.
- `DaemonMutationLaneTests`: a pinned executor whose status carries no spawn type, with two installed services, is refused with `reload_unsupported` and dispatches nothing; with the field present the verb dispatches; timeout, non-zero exit with and without the token, and exit 0 followed by a daemon that becomes reachable within the window, one that never does, an ownership snapshot reading `adaptive`, one reading `background`, one with an unknown word, one without the field, and one reading `daemon` with full evidence each map to the stated outcome.
- `DaemonLifecycleControllerTests`: a background-band snapshot raises the indicator, a positive one lowers it, an unknown or failed read leaves it; an initial Unreachable followed by Connected to an Adaptive daemon raises it, and a later reconnect to a Standard successor lowers it and clears a standing `background_band` state; a positive read leaves a standing `unit_missing` state and a standing ownership-mismatch state; a positive read that started before a `contended` outcome was recorded does not clear it, one that started after does; a reload that succeeds followed by one that fails leaves the failure standing; a second click while a reload is in flight is ignored; the prompt shows on every click, a decline runs no mutation, a generation change while the prompt is open cancels with the status line; `Succeeded` posts the standard-priority line, clears the state and lowers the indicator after the re-query; an unknown token renders the fallback copy with the exit code.
- `App` tests: the outcome consumer acknowledges and logs a Reload envelope without posting to the attention lane.
- `SessionRailView` smoke: the block binds the indicator text, the failure text, the button's command and its disabled state; the block is visible with a failure standing and the indicator lowered.

**Live, on a Mac whose daemon is loaded as Adaptive.** The rail shows the indicator; clicking shows the prompt naming the agent count; accepting at a quiet moment ends them, the daemon pid changes, `launchctl print` reads `daemon (3)` and `ps -o pri` on the daemon reads 20, and the indicator disappears without a restart of the app. The diagnosing machine, with its hand-edited Standard plist, is this case.

## Notes

- The reload ends every hosted agent and the daemon tears down their worktrees, so the prompt is shown on every click and names the count and the loss; the lane never runs the verb without that consent.
- Rewriting the plist is safe at any time; only the reload is destructive.
