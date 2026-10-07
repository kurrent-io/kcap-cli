# #1351 — Reload a daemon still running as launchd Adaptive, from the desktop app

## Problem

A macOS daemon whose LaunchAgent plist declares `ProcessType=Adaptive` runs in the background QoS band: launchd holds an adaptive job at priority 4 until it receives a Mach importance boost over XPC, and a Unix-socket daemon never receives one. Every hosted agent and every process those agents spawn inherits the band. Under memory pressure the band is throttled on CPU and I/O, so the daemon and the agents on the keystroke path stall while the foreground desktop app stays responsive: terminals redraw late and typed input lags, worse the more agents run.

New installs write `Standard`. An existing plist is brought across by `kcap daemon service refresh`, which runs after an update, but three things keep that from reaching a busy machine:

- The refresh reloads the job only after the daemon accepts an idle-only restart. A machine that always has agents running never idles, and nothing tells the user the plist is still Adaptive.
- The upgrade matches the writer's single-line `ProcessType` element byte for byte. A plist in Apple's canonical layout (tab-indented, one element per line) is left as it is.
- `kcap daemon restart` does not apply a rewritten plist: KeepAlive relaunches the job from the definition launchd cached at load. No in-process call lifts the band either; `taskpolicy -B` exits 0 and changes nothing on the daemon or on a child.

## Goal

The desktop app shows when its daemon runs as Adaptive and offers the reload that fixes it, with the consequences stated before it runs, and the result proven from evidence rather than from the click.

## Out of scope

- The daemon detecting its own scheduling band from inside the process. launchd is the authority and the CLI asks it directly.
- An app-driven reload that waits for the daemon to be idle, and a daemon-side idle-only reload that refuses a newly busy state atomically. The reload here is always disclosed as destructive, so no idle check needs to be exact.
- Teaching `kcap daemon restart` to reload the job. The app does not use that command, and `service refresh --force` covers the terminal user.
- Windows and systemd. Neither has a process type with this behaviour.

## Design

### CLI: data and flags

**Loaded spawn type in the query.** `LaunchdServiceManager.QueryCore` already runs `launchctl print` and classifies the label. `ServiceQuery` gains a trailing `string? LoadedSpawnType`: the word launchd prints on its `spawn type =` line (`adaptive`, `interactive`, `daemon`, ...), read by a new `LaunchdUnit.LoadedSpawnType` beside the existing `LoadedAsAdaptive`; null when the label is not `Loaded`, when the print has no such line, and for every non-launchd manager. A null never stands for Standard: only a present word other than `adaptive` does.

**Status JSON.** `ServiceStatusJson` gains a trailing `string? LoadedSpawnType = null`, serialised as `loaded_spawn_type` by the existing snake-case source generator. Older CLIs omit the key; a reader treats absence as unknown.

**Status text.** `kcap daemon status` prints, under the existing `service:` line and only when the spawn type is `adaptive`, naming the daemon it describes:

```
  priority: loaded as Adaptive — background priority; run `kcap daemon service refresh --name <daemon> --force` to reload (ends this daemon's hosted agents)
```

**Layout-independent upgrade.** `LaunchdUnit.UpgradeProcessType` locates the value through the parser, then edits the text:

1. Parse with line information. Find the unique top-level `ProcessType` key through the existing strict key/value walk; a duplicate key, a nested-only key, or no key returns null.
2. The paired value must be a `string` element whose only child is the text `Adaptive`; a comment or CDATA inside it, or any other value (`Standard`, `Background`, `Interactive`), returns null.
3. Map the element's line and column to a character offset in the source and require the bytes there to read exactly `<string>Adaptive</string>`; otherwise return null.
4. Splice `Standard` over `Adaptive` at that span and return the result only when `DeclaresStandardProcessType` accepts it.

Every other byte survives, including comments elsewhere, CRLF line endings and a missing final newline, as `WithBinary` already guarantees for the binary path. A comment or whitespace between the key and its value is skipped by construction because the splice targets the value element's own span.

**Refresh outcomes.** `UnitRefresh` distinguishes what today's `Unchanged` folds together. The forced run reports exactly one of them on stderr as `refresh_outcome=<token>`:

| Token | Meaning | Forced exit |
|---|---|---|
| `reloaded` | the job was booted out and bootstrapped from the rewritten plist | 0 |
| `current` | the plist declares Standard and the loaded job's spawn type is not `adaptive` | 0 |
| `not_loaded` | the plist is current or was rewritten, but the label is not loaded | 1 |
| `deferred` | the daemon refused the restart, or the time left was under the reload reserve | 1 |
| `contended` | another service operation holds the label's transaction lock | 1 |
| `unverified` | `launchctl print` timed out, so launchd's state is unknown | 1 |
| `unit_missing` | no plist at the path | 1 |
| `unit_unreadable` | the plist exists but cannot be read | 1 |
| `unit_unsupported` | the plist parses but cannot be brought to Standard | 1 |
| `failed` | bootout or bootstrap failed; the detail follows on its own stderr line | 1 |

An exception from a file write or from starting `launchctl` is caught at the command and reported as `failed`, so the token line is always present and always single. The unforced refresh keeps today's output and exit codes for every outcome: its "busy" line additionally names `kcap daemon service refresh --name <daemon> --force` as the way to apply the change now, and a lock contention prints that another service operation is in progress and the change waits for the next update.

**Forced refresh.** `kcap daemon service refresh --force` targets the daemon the command resolved (`--name`, else the profile's), not every installed one, and passes `RefreshUnit` a restart request in mode `force` instead of `now`. The daemon then ends its agents and exits, and the existing sequence boots the job out while it is exiting and bootstraps the rewritten plist. `DaemonServiceCommands.RequestIdleRestart` becomes `RequestRestart(serviceId, mode)`; the acknowledgement it accepts is unchanged (`RestartAck` with text `restarting`).

**Transaction lock.** Every `RefreshUnit` caller serialises on the per-label `ServiceTxnLock` that install, start and uninstall already take. The forced run waits up to 10 s, like a plain install, and reports `contended` on timeout. The unforced run tries once without waiting and defers on contention. No plist write and no restart request happens without the lock.

**Budgets.** The command's refresh deadline stays 55 s, inside which the 47 s reload reserve applies after the initial query. The app's mutation bound of 60 s covers the whole command.

**README.** The `kcap daemon service` section documents `--force`, the outcome line and the status line.

### App: lane verb and classification

**Snapshot.** `ServiceSnapshot` gains `string? LoadedSpawnType`, deserialised from `loaded_spawn_type`. Adaptive means the value is exactly `adaptive`.

**Verb.** `MutationVerb.Reload` joins the lane. `IKcapCli.ServiceReloadAsync(ct)` runs:

```
kcap daemon service refresh --name <daemon> --force
```

under the existing 60 s mutation bound.

**Classification.** A timed-out run is `UnconfirmedNoAttach`. A non-zero exit is `Failed(exitCode, token, Attention)` with the token from `ReasonLine.TrySingle(stderr, "refresh_outcome=")`. Exit 0 is confirmed in two steps:

1. *Readiness window.* `RefreshUnit` returns as soon as `launchctl bootstrap` succeeds, before the successor has bound its socket, and the lane's one-shot observation never retries. So Reload polls the observation for up to the existing 10 s detached confirmation window, at its 1 s interval, until the shared evidence predicate passes or the window ends. The window ending on an unreachable daemon is `UnconfirmedNoAttach`; on any other failing leg it is that leg's skew.
2. *Shared predicate plus priority evidence.* Once the observation passes, `ClassifyServiceSuccessAsync` runs unchanged, with one leg added after the ownership repair leg and before the pid checks: the ownership snapshot the pinned executor just read must carry a spawn type. `adaptive` is `AttentionSkew("still_adaptive")`; a missing or null spawn type is `AttentionSkew("spawn_type_unknown")`. Only a present word other than `adaptive` lets the leg pass.

The ownership read comes from the same pinned executable that ran the refresh, so a CLI too old to know the flag also omits the field and never produces a success. Exit 0 with `current` passes the same two steps and is a success: the daemon runs at standard priority, which is what the user asked for.

**Controller.** `DaemonLifecycleController` gains:

- `IObservable<bool> BackgroundPriority`, a replaying subject set from every `ServiceSnapshot` the controller reads: true when the spawn type is `adaptive`, false when it is any other word, unchanged when the read failed or the field is absent. A passive status read runs on every transition into `Connected`, not only the once-per-run arm, so a daemon that connects later, or is reloaded outside the app, updates the indicator. The passive read never arms a mutation.
- `Task ReloadServiceAsync(CancellationToken ct)`: captures the attach generation, shows the reload prompt, and cancels with a status line if the generation changed while the prompt was open, as `ConfirmAndReplaceAsync` does. On accept it awaits the lane's outcome for `MutationVerb.Reload`, posts "Daemon runs at standard priority." on the status lane for `Succeeded`, leaves every other outcome to the outcome channel, then re-queries the service status so the indicator follows evidence.

The prompt is shown on every click. The count it names is the latest `DaemonInfoDto.ActiveAgents`, but the disclosure states that any agent, pending launch or evaluation running when the daemon exits ends with it, so a launch that lands after the count was read is covered by the consent given.

### App: indicator and prompt

**Indicator.** `MainWindowViewModel` gains `BackgroundPriority` and `BackgroundPriorityText`, built like `RestartPending`: gated to a connected attach, fed by the controller's observable through a constructor parameter, with the message

> Daemon runs at background priority — agent terminals lag under load.

and a `ReloadDaemonCommand` bound to the controller's action. In `SessionRailView.axaml` the block sits beside the update-pending indicator, as `Classes.backgroundPriority`, in `KcapWarning*` tone because it is a needs-you status, with a `kcapGhost` "Reload" button carrying the command. The update-pending indicator is unchanged.

**Prompt.** `LifecyclePrompt.KindReloadService` is a new kind. `LifecyclePromptViewModel` titles it "Reload the daemon service" and labels accept "Reload now"; decline stays visible. The controller builds the disclosure:

> Reloading restarts the daemon and ends everything it hosts: N agent(s) now, plus any agent, launch or evaluation running when it exits. Uncommitted work in their worktrees is lost.

**Outcome copy.** `App.AttentionCopyFor` gains mappings for the Reload tokens so none is log-only: `still_adaptive` ("The daemon still runs as Adaptive after the reload. Run `kcap daemon service refresh --name <daemon> --force` from a terminal and check `kcap daemon status`."), `spawn_type_unknown` ("The reload finished but this kcap CLI cannot report the daemon's priority. Update kcap and check `kcap daemon status`."), `deferred`, `contended`, `not_loaded`, `unverified`, `unit_missing`, `unit_unreadable`, `unit_unsupported` and `failed`, each naming the daemon and the terminal command. No copy claims the daemon restarted unless `reloaded` was established. Failures reach the window and the tray through the attention lane every mutation already uses; this change adds no tray indicator of its own.

## Testing

**CLI (`test/Capacitor.Cli.Tests.Unit`).**

- `LaunchdUnitTests`: the upgrade handles a tab-indented one-element-per-line plist with every other byte intact, including CRLF and no final newline; returns null for a missing key, a duplicate key, a nested-only key, an already-Standard value, `Background`, `Interactive`, a CDATA value and a comment inside the value; a comment elsewhere containing the Adaptive pair is untouched while the real value is upgraded; a comment between the key and its value does not block the upgrade. `LoadedSpawnType` reads `adaptive` and `interactive` shapes and null when the line is absent.
- `LaunchdUnitRefreshTests`: a forced refresh sends mode `force`, reloads a daemon that reports agents, and returns `Reloaded`; an unforced one still defers; each of `current`, `not_loaded`, `deferred`, `unverified`, `unit_missing`, `unit_unreadable` and `unit_unsupported` is returned from its fixture; a held transaction lock yields `contended` without a plist write or a restart request; a throwing unit writer and a throwing process start each yield `failed`.
- `DaemonServiceCommands` tests: the forced command prints exactly one `refresh_outcome=` line and the exit mapping above; with two installed daemons, `--force` touches only the named one and the unforced run still iterates both; the status hint and the deferred line carry `--name`.
- `ServiceStatusJsonTests`: the field round-trips as `loaded_spawn_type`; a document without the key deserialises to null.
- `DaemonStatusServingTests` or a sibling: the priority line appears only for `adaptive` and names the daemon.

**App (`test/Capacitor.App.Tests.Unit`).**

- `KcapCliTests`: the snapshot parses the field as a word, as null and as absent; `ServiceReloadAsync` passes the exact arguments.
- `DaemonMutationLaneTests`: the verb dispatches; timeout, non-zero exit with and without the token, and exit 0 followed by a daemon that becomes reachable within the window, one that never does, an ownership snapshot reading `adaptive`, one without a spawn type (the old-CLI case), and one reading `daemon` with full evidence each map to the stated outcome.
- `DaemonLifecycleControllerTests`: an `adaptive` snapshot raises the indicator, another word lowers it, a failed read leaves it; an initial Unreachable followed by Connected to an Adaptive daemon raises it, and a later reconnect to a Standard successor lowers it; the prompt shows on every click, a decline runs no mutation, a generation change while the prompt is open cancels with the status line; `Succeeded` posts the standard-priority line and lowers the indicator after the re-query.
- `App` tests: every Reload token has attention copy.
- `SessionRailView` smoke: the indicator binds to the view model's property and its button to the command.

**Live, on a Mac whose daemon is loaded as Adaptive.** The rail shows the indicator; clicking shows the prompt naming the agent count; accepting at a quiet moment ends them, the daemon pid changes, `ps -o pri` on the daemon reads 20, and the indicator disappears without a restart of the app.

## Notes

- The reload ends every hosted agent and the daemon tears down their worktrees, so the prompt is shown on every click and names the count and the loss; the lane never runs the verb without that consent.
- Rewriting the plist is safe at any time; only the reload is destructive. The plist on the machine where this was diagnosed is already rewritten by hand and still loaded as Adaptive, which is the first live case for this change.
