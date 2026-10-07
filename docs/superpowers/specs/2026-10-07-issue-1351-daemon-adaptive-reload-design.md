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

**Controller.** `DaemonLifecycleController` gains:

- `IObservable<bool> BackgroundPriority`, a replaying subject set from every `ServiceSnapshot` the controller reads: true for a background-band word, false for a positive word, unchanged for an unknown reading or a failed read. A passive status read runs on every transition into `Connected`, not only the once-per-run arm, so a daemon that connects later, or is reloaded outside the app, updates the indicator. The passive read never arms a mutation.
- `Task ReloadServiceAsync(CancellationToken ct)`: captures the attach generation, shows the reload prompt, and cancels with a status line if the generation changed while the prompt was open, as `ConfirmAndReplaceAsync` does. On accept it awaits the lane's outcome for `MutationVerb.Reload`, posts "Daemon runs at standard priority." on the status lane for `Succeeded`, leaves every other outcome to the outcome channel, then re-queries the service status so the indicator follows evidence.

The prompt is shown on every click. The count it names is the latest `DaemonInfoDto.ActiveAgents`, but the disclosure states that any agent, pending launch or evaluation running when the daemon exits ends with it, so a launch that lands after the count was read is covered by the consent given.

### App: indicator, prompt and attention

**Indicator.** `MainWindowViewModel` gains `BackgroundPriority` and `BackgroundPriorityText`, built like `RestartPending`: gated to a connected attach, fed by the controller's observable through a constructor parameter, with the message

> Daemon runs at background priority — agent terminals lag under load.

and a `ReloadDaemonCommand` bound to the controller's action. In `SessionRailView.axaml` the block sits beside the update-pending indicator, as `Classes.backgroundPriority`, in `KcapWarning*` tone because it is a needs-you status, with a `kcapGhost` "Reload" button carrying the command. The update-pending indicator is unchanged.

**Prompt.** `LifecyclePrompt.KindReloadService` is a new kind. `LifecyclePromptViewModel` titles it "Reload the daemon service" and labels accept "Reload now"; decline stays visible. The controller builds the disclosure:

> Reloading restarts the daemon and ends everything it hosts: N agent(s) now, plus any agent, launch or evaluation running when it exits. Uncommitted work in their worktrees is lost.

**Outcome copy.** `App.AttentionCopyFor` gains mappings for every Reload token: `reload_unsupported` ("This kcap CLI cannot reload the daemon service. Update kcap, then press Reload again."), `background_band` ("The daemon still runs at background priority after the reload. Run `kcap daemon service refresh --name <daemon> --force` from a terminal and check `kcap daemon status`."), `spawn_type_unknown` ("The reload finished but the daemon's priority could not be confirmed. Check `kcap daemon status --name <daemon>`."), `deferred`, `contended`, `not_loaded`, `unverified`, `unit_missing`, `unit_unreadable`, `unit_unsupported` and `failed`, each naming the daemon and the terminal command. No copy claims the daemon restarted unless `reloaded` was established.

**Fallback copy.** A Reload envelope whose token has no mapping, including the exit-code token an absent or malformed reason line produces, is still shown: "The daemon reload for <daemon> failed (exit N). Check `kcap daemon status --name <daemon>`; details are in the app log." The raw token stays in the log. Every other verb keeps today's log-only treatment of unknown tokens.

**Attention entries with owners.** The attention lane is a replaying subject shared by the main window and the tray, and the status lane is a different subject, so a success line cannot clear an earlier failure: the tray would keep asserting attention and a reopened window would replay the stale text over the newer status. Text is not an identity either: several tokens and verbs already map to the same copy, so clearing "the text a Reload posted" could delete a newer warning from another verb that happens to read the same.

The lane therefore carries an `AttentionEntry` (an opaque id plus the text) instead of a bare string; the window and the tray bind to its text. `ILifecycleSurface.Attention` returns the entry's id, and a new `ILifecycleSurface.ClearAttention(AttentionId id)` sets the lane to null only if it currently holds that exact entry, as one atomic conditional step on the subject. The presenter records the id of the Reload-owned entry it last posted together with the token it carried.

**Resolution.** Two things resolve a Reload-owned entry:

- A verified Reload success (`Succeeded`) resolves any Reload-owned entry.
- A passive status read with a positive spawn type resolves only the entries whose sole complaint was the priority state or a transient inability to act: `background_band`, `spawn_type_unknown`, `deferred`, `contended` and `unverified`. Entries for `reload_unsupported`, `not_loaded`, `unit_missing`, `unit_unreadable`, `unit_unsupported`, `failed`, the fallback copy, and any ownership or skew leg from the shared classifier stay until a verified success, because a positive spawn type does not disprove them: launchd keeps a loaded `daemon` job after its plist is deleted, and an ownership mismatch is independent of priority.

Resolution also supersedes what has not been shown yet. The lane delivers a failure to the waiter at once and enqueues its envelope for a presenter that drains separately and may be paused behind another outcome's dialog, so a stale Reload failure can still be queued when the success or the positive read arrives. The controller stamps each resolution with the lane's envelope sequence at that moment; the presenter, before showing a Reload envelope, drops one enqueued at or before a stamp that resolves its token, acknowledges it and logs it instead. Failures reach the window and the tray through the attention lane every mutation already uses; this change adds no tray indicator of its own.

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
- `DaemonLifecycleControllerTests`: a background-band snapshot raises the indicator, a positive one lowers it, an unknown or failed read leaves it; an initial Unreachable followed by Connected to an Adaptive daemon raises it, and a later reconnect to a Standard successor lowers it and resolves a `background_band` entry; a positive read leaves a `unit_missing` entry and an ownership-repair entry in place; the prompt shows on every click, a decline runs no mutation, a generation change while the prompt is open cancels with the status line; `Succeeded` posts the standard-priority line, resolves every Reload-owned entry and lowers the indicator after the re-query.
- `App` presentation tests: every Reload token has copy; an envelope with an absent or unknown token is presented with the fallback copy, not logged only; a failure followed by a verified success leaves the tray out of attention and a reopened main window without the stale text; an entry with identical text posted by a different outcome survives the Reload clear; a Reload failure whose presentation is paused until after a verified success or a resolving positive read is acknowledged and logged, not shown.
- `SessionRailView` smoke: the indicator binds to the view model's property and its button to the command.

**Live, on a Mac whose daemon is loaded as Adaptive.** The rail shows the indicator; clicking shows the prompt naming the agent count; accepting at a quiet moment ends them, the daemon pid changes, `launchctl print` reads `daemon (3)` and `ps -o pri` on the daemon reads 20, and the indicator disappears without a restart of the app. The diagnosing machine, with its hand-edited Standard plist, is this case.

## Notes

- The reload ends every hosted agent and the daemon tears down their worktrees, so the prompt is shown on every click and names the count and the loss; the lane never runs the verb without that consent.
- Rewriting the plist is safe at any time; only the reload is destructive.
