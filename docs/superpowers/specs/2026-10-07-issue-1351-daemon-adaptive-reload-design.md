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
- An app-driven reload that waits for the daemon to be idle. It can be layered on later; on the machines that hit this, idle never comes.
- Teaching `kcap daemon restart` to reload the job. The app does not use that command, and `service refresh --force` covers the terminal user.
- Windows and systemd. Neither has a process type with this behaviour.

## Design

### CLI: data and flags

**Loaded spawn type in the query.** `LaunchdServiceManager.QueryCore` already runs `launchctl print` and classifies the label. `ServiceQuery` gains a trailing `bool? LoadedAsAdaptive`: true or false when the label is `Loaded`, read through the existing `LaunchdUnit.LoadedAsAdaptive`; null for any other probe result and for every non-launchd manager.

**Status JSON.** `ServiceStatusJson` gains a trailing `bool? LoadedAsAdaptive = null`, serialised as `loaded_as_adaptive` by the existing snake-case source generator. Older CLIs omit the key; a reader treats absence as unknown.

**Status text.** `kcap daemon status` prints, under the existing `service:` line and only when the field is true:

```
  priority: loaded as Adaptive — background priority; run `kcap daemon service refresh --force` to reload (ends hosted agents)
```

**Layout-independent upgrade.** `LaunchdUnit.UpgradeProcessType` becomes a text-level splice: locate `<key>ProcessType</key>`, skip whitespace, require `<string>Adaptive</string>`, replace the value with `Standard`, and return the result only when `DeclaresStandardProcessType` accepts it. Every other byte of the file survives, as `WithBinary` already guarantees for the binary path. A plist with no `ProcessType` key, or one whose value is not Adaptive, returns null as before.

**Forced refresh.** `kcap daemon service refresh --force` targets the daemon the command resolved (`--name`, else the profile's), not every installed one, and passes `RefreshUnit` a restart request in mode `force` instead of `now`. The daemon then ends its agents and exits, and the existing sequence boots the job out while it is exiting and bootstraps the rewritten plist. For machine callers the forced run writes exactly one line to stderr:

```
refresh_outcome=<reloaded|rewritten|unchanged|deferred|unverified|failed>
```

Exit code 0 for `reloaded`, `rewritten` and `unchanged`; 1 otherwise. The unforced refresh keeps today's behaviour and output, and its "busy" line names `--force` as the way to apply the change now.

`DaemonServiceCommands.RequestIdleRestart` becomes `RequestRestart(serviceId, mode)`; the acknowledgement it accepts is unchanged (`RestartAck` with text `restarting`).

**README.** The `kcap daemon service` section documents `--force` and the status line.

### App: lane verb and classification

**Snapshot.** `ServiceSnapshot` gains `bool? LoadedAsAdaptive`, deserialised from `loaded_as_adaptive`.

**Verb.** `MutationVerb.Reload` joins the lane. `IKcapCli.ServiceReloadAsync(ct)` runs:

```
kcap daemon service refresh --name <daemon> --force
```

under the existing 60 s mutation bound, which covers the CLI's own reload budget of 47 s.

**Classification** reuses `ClassifyServiceVerbAsync`:

- a timed-out run is `UnconfirmedNoAttach`;
- a non-zero exit is `Failed(exitCode, token, Attention)` with the token from `ReasonLine.TrySingle(stderr, "refresh_outcome=")`;
- exit 0 goes through `ClassifyServiceSuccessAsync` unchanged, plus one leg evaluated after the ownership repair leg: an ownership snapshot whose `LoadedAsAdaptive` is true returns `AttentionSkew("still_adaptive")`. A refresh that did nothing is therefore never a success, whatever the exit code said.

**Capability.** The field and the flag ship in one CLI change, so a snapshot that reports `loaded_as_adaptive: true` comes from a CLI that accepts `--force`. No help-text probe and no floor bump. Should an older CLI ever receive the flag, it runs an idle-only refresh and exits 0, and the still-Adaptive leg turns that into attention rather than a claimed success.

**Controller.** `DaemonLifecycleController` gains:

- `IObservable<bool> BackgroundPriority`, a replaying subject set from every `ServiceSnapshot` the controller reads: true when `LoadedAsAdaptive` is true, false when it is false or null. The existing reads at startup and on attach transitions feed it; the reload action adds one read after the mutation so the indicator clears from evidence.
- `Task ReloadServiceAsync(CancellationToken ct)`: takes the latest `DaemonInfoDto.ActiveAgents` from the client's snapshots; when it is above zero, shows the reload prompt and stops on decline; runs `RunLaneMutationAsync(MutationVerb.Reload)`; routes the outcome through the existing outcome channel, with `Succeeded` reported on the status lane as "Daemon reloaded at standard priority." and `still_adaptive` on the attention lane as "The daemon restarted but launchd still runs it as Adaptive; run `kcap daemon service refresh --force` from a terminal."; then re-queries the service status.

### App: indicator and prompt

**Indicator.** `MainWindowViewModel` gains `BackgroundPriority` and `BackgroundPriorityText`, built like `RestartPending`: gated to a connected attach, fed by the controller's observable through a constructor parameter, with the message

> Daemon runs at background priority — agent terminals lag under load.

and a `ReloadDaemonCommand` bound to the controller's action. In `SessionRailView.axaml` the block sits beside the update-pending indicator, as `Classes.backgroundPriority`, in `KcapWarning*` tone because it is a needs-you status, with a `kcapGhost` "Reload" button carrying the command. The update-pending indicator is unchanged.

**Prompt.** `LifecyclePrompt.KindReloadService` is a new kind. `LifecyclePromptViewModel` titles it "Reload the daemon service" and labels accept "Reload now"; decline stays visible. The controller builds the disclosure:

> Reloading restarts the daemon and ends the N running agent(s). Uncommitted work in their worktrees is lost.

An idle daemon shows no prompt: the click is the consent and nothing is ended.

Nothing is posted to the tray.

## Testing

**CLI (`test/Capacitor.Cli.Tests.Unit`).**

- `LaunchdUnitRefreshTests`: a tab-indented, one-element-per-line plist is upgraded with every other byte intact; a plist whose `ProcessType` is not Adaptive, or has none, returns null; the existing "cannot upgrade" case keeps a genuinely unparseable shape. A forced refresh sends mode `force`, reloads a daemon that reports agents, and prints the outcome line; an unforced one still defers.
- `LaunchdClassifyTests` or a sibling: `ServiceQuery.LoadedAsAdaptive` is true and false from the two print shapes, null when the label is absent.
- `ServiceStatusJsonTests`: the field round-trips as `loaded_as_adaptive`; a document without the key deserialises to null.
- `DaemonStatusServingTests` or a sibling: the priority line appears only when the field is true.

**App (`test/Capacitor.App.Tests.Unit`).**

- `KcapCliTests`: the snapshot parses the field as true, false and absent; `ServiceReloadAsync` passes the exact arguments.
- `DaemonMutationLaneTests`: the verb dispatches; timeout, non-zero exit with and without the token, exit 0 with a still-Adaptive ownership snapshot, and exit 0 with full evidence each map to the stated outcome.
- `DaemonLifecycleControllerTests`: a snapshot with the field true raises the indicator and false lowers it; with agents running the prompt shows and a decline runs no mutation; idle skips the prompt; a verified reload lowers the indicator and posts the status line; `still_adaptive` posts the attention line.
- `SessionRailView` smoke: the indicator binds to the view model's property and its button to the command.

**Live, on a Mac whose daemon is loaded as Adaptive.** The rail shows the indicator; clicking with agents running shows the prompt naming their count; accepting at a quiet moment ends them, the daemon pid changes, `ps -o pri` on the daemon reads 20, and the indicator disappears without a restart of the app.

## Notes

- The reload ends every hosted agent and the daemon tears down their worktrees, so the prompt names the count and the loss; the lane never runs the verb without it while agents are running.
- Rewriting the plist is safe at any time; only the reload is destructive. The plist on the machine where this was diagnosed is already rewritten by hand and still loaded as Adaptive, which is the first live case for this change.
