# Daemon-advertised vendor model catalogs

Spans two repositories: kcap-cli (daemon, wire, desktop app) and kcap-server (registry, web
launch dialog). Pi is the first and only producer; the shape is per-vendor so a second producer
needs no wire change.

## 1. Problem

The desktop launcher and the web launch dialog offer Pi as a hosted agent but list no models for
it, so the only row is "Default — Pi chooses". The launch plumbing already works: the Pi runtime
factory declares model selection and passes `--model <id>` on argv, and both launchers send the
selected model on the wire. The list is empty because both catalog sources are empty for Pi:

- The server catalog (`GET api/agents/model-options`, built by kcap-server's
  `VendorModelCatalog`) maps only vendors with a models.dev provider, and its static-list table
  for the rest ships empty.
- The desktop app's curated fallback (`HostedHarnessCatalog.ModelChoices`) has no Pi entry.

Pi's model list is also not a server-knowable fact. Pi hosts many providers, and which models are
usable on a machine depends on which providers have auth there. On the author's machine Pi lists
51 models across four providers; another machine with one key lists a dozen. A server-side static
list would either offer models that fail at launch or omit ones that work.

## 2. Decisions

| Decision | Choice |
|---|---|
| Source of truth | The daemon asks the installed Pi process for its model list and advertises it. |
| Probe mechanism | `pi --mode rpc --offline --no-extensions --no-session` in an empty daemon-owned directory, one `get_available_models` command on stdin. Never table-parsing `--list-models`, never reading Pi's config files. |
| Model id on the wire | `provider/id` (e.g. `anthropic/claude-opus-5`). |
| Label | Pi's `name` followed by the provider, e.g. `Claude Opus 5 · anthropic`. |
| Wire shape | One optional dictionary, vendor token → model options, on both daemon advertisement paths (server `DaemonConnect` and local status IPC). |
| Null vs absent vs empty | Null = old daemon, unknown. Key absent = no catalog for that vendor. Key present and empty = probed, nothing usable. |
| Consumer precedence | Daemon list for the selected machine → server catalog → curated fallback. A present key wins even when empty. |
| Refresh | Startup, then re-probe when the Pi binary or Pi's `auth.json` / `models.json` fingerprint changes, via the existing `VendorCliWatcher`. |
| Files the daemon touches | Stat only (existence, size, mtime). Contents are never read: `models.json` can carry provider API keys and `auth.json` always does. |
| Out of scope | Effort → Pi thinking level; the mobile launcher; catalogs for ACP vendors whose model list only appears at `session/new`. |

## 3. Daemon probe (kcap-cli, `Capacitor.Cli.Daemon`)

### 3.1 Factory surface

`IHostedAgentRuntimeFactory` gains one default-implemented member:

```csharp
/// The models this vendor can launch on this machine, or null when the vendor publishes no
/// catalog. Null is also the answer to any probe failure: the consumer falls through to its
/// other sources rather than seeing an empty list it would read as "nothing usable".
Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) =>
    Task.FromResult<IReadOnlyList<VendorModelOption>?>(null);

/// Paths whose fingerprint change means the model catalog may have changed. Empty by default.
IReadOnlyList<string> CatalogFingerprintPaths => [];
```

`VendorModelOption(string Value, string Label)` is a new record in `Capacitor.Cli.Core`
(`Capacitor.Cli.Core/Models.cs` already hosts the `DaemonConnect` record; the option record gets
its own file, `VendorModelOption.cs`, one type per file).

Only `PiRpcHostedAgentRuntimeFactory` overrides both. Every other factory keeps the defaults and
is untouched.

### 3.2 Pi implementation

New file `Harness/Pi/PiModelCatalogProbe.cs`, a static helper the factory calls:

1. Skip when `IsAvailable()` is false (return null without spawning).
2. `ProcessStartInfo(config.PiPath, ["--mode", "rpc", "--offline", "--no-extensions",
   "--no-session"])`, redirected stdin/stdout/stderr, `PiLaunchEnvironment.Apply(env)` so the
   kcap extension stands down even if extension discovery were ever re-enabled. `--offline`
   (equivalently `PI_OFFLINE=1`) stops Pi's startup network operations: `--no-extensions` alone
   still runs a configured npm package install, with lifecycle scripts, before RPC is served
   (`docs/probes/2026-09-21-pi-reviewer/findings.md` §8). `--no-extensions` keeps third-party
   extensions (MCP bridges and the like) from starting, which otherwise print to stderr and add
   seconds.
   `WorkingDirectory` is `PiModelCatalogProbe.DirectoryFor(stateDir)` =
   `<state dir>/pi-probe`, created with a plain `Directory.CreateDirectory` on every platform
   (the state directory is already the user's own; nothing is written there and no owner-only
   mode is claimed, so the reviewer launch dir's POSIX-only `CreateOwnerOnly` is not used and
   Windows hosts probe like any other). Its contents, if any, are deleted before each probe so it
   is always empty. Pi renames a project's `.pi/commands` at startup, so the probe never runs in
   a repository or in the daemon's own cwd.
3. Write `{"type":"get_available_models"}\n` and flush. Stdin stays **open**: Pi's RPC loop
   treats stdin EOF as a shutdown request and does not await an in-flight command first, so
   closing before the response arrives races shutdown against the answer.
4. Read stdout lines until a line parses as JSON with `type == "response"` and
   `command == "get_available_models"`. Ignore every other line (Pi may emit other frames first).
   Only then close stdin.
5. Deadline: 10 seconds from spawn, measured on `TimeProvider`. On expiry, `ProcessTree.Kill`
   the child (the daemon never uses `Process.Kill(bool)`), return null, log once at Warning with
   the reason.
6. On the response: `success != true` → null. `success == true` with `data` missing, `models`
   missing, or `models` not an array → null as well: an invalid envelope never establishes
   emptiness, and null lets consumers fall through, whereas `[]` would suppress both fallback
   catalogs. A present, empty `models` array → `[]`. Otherwise map `data.models[]` to
   `VendorModelOption($"{provider}/{id}", $"{name} · {provider}")`. An entry missing `id` or
   `provider`, or that is not an object, is skipped; a missing `name` falls back to `id`. Pi's
   order is kept (it is the order Pi cycles with Ctrl+P).
7. After closing stdin, wait for exit with a short grace (2 seconds), then `ProcessTree.Kill`
   if still running. Exit code is not consulted: the response is the result.

Parsing lives in a pure static `PiModelCatalogProbe.Parse(string responseLine)` so the fixture
tests need no process.

The probe's stderr is drained to a bounded buffer and discarded; it is not logged unless the probe
fails, and then only its first 512 characters.

### 3.3 Startup

In `DaemonRunner`, after `SupportedVendors` is computed and before `LogStartupPhase("vendors
probed")`:

```csharp
config.VendorModels = await ProbeVendorModelsAsync(runtimeFactories, config.SupportedVendors, ct);
```

`ProbeVendorModelsAsync` runs `ProbeModelsAsync` for every advertised factory concurrently and
folds non-null answers into a `Dictionary<string, VendorModelOption[]>` keyed by vendor token,
ordinal. A vendor whose probe returned null is not a key. The result is stored on
`DaemonConfig.VendorModels` as `Dictionary<string, VendorModelOption[]>?`, the same concrete type
the two wire records declare, so the config value is passed through without conversion. Null only
before startup has run.

Startup cost is one Pi spawn, about 1.5 seconds on a warm machine, run concurrently with the
other factories' probes. It lands before the first `DaemonConnect`, so the server never sees a
registration without it.

### 3.4 Refresh

`VendorCliWatcher` today watches the unattended vendors' CLI binaries. Two changes:

- **Watch set.** Unattended vendors ∪ vendors whose factory has non-empty
  `CatalogFingerprintPaths` or is in `config.VendorModels`. Pi is normally both; the union
  matters when Pi is installed but its reviewer capability is withheld (build too old).
- **Per-vendor fingerprint.** Alongside the binary stat, a `CatalogPathStat` of each
  `CatalogFingerprintPaths` entry (existence, length, last-write ticks; a missing file is a
  distinct value, not a transient, since deleting `auth.json` does change the catalog). A change
  in any of them counts as that vendor changing. The Pi factory returns
  `PiPaths.AuthJson` and `PiPaths.ModelsJson` (two new members on `PiPaths`, both under
  `AgentDir`).

The existing tick calls `orchestrator.RefreshAdvertisedCapabilities(reason)`. That pass now also
re-runs `ProbeVendorModelsAsync` for every catalog-publishing vendor (one Pi spawn today) and
builds a **new** dictionary: the fresh answer for each vendor that returned one, the previous
entry for each vendor whose re-probe returned null (a transient failure does not withdraw a
working list). `config.VendorModels` is never mutated in place: the serializers on the hub and
on status-IPC subscriber tasks may be enumerating the current instance, so publication is one
reference swap of the new dictionary, after which the pass re-registers and pulses
`DaemonStatusNotifier`. Whether the catalog changed is decided by
`VendorModelCatalogs.Equal(a, b)`: same key set, and per key the same ordered sequence of
`(Value, Label)` pairs. An unchanged catalog swaps nothing, re-registers nothing and pulses
nothing.

Baselines follow the existing rule and are recorded before any probe runs, for **every
advertised factory**, not just the eventual watch set: `FingerprintUnattendedVendors` is called
with `SupportedVendors`, so `UnattendedVendorBaselines` records the binary of a catalog-only Pi
and of a producer that only reveals itself by returning a catalog, and every factory's
`CatalogFingerprintPaths` are recorded on `DaemonConfig.VendorCatalogBaselines`
(`IReadOnlyDictionary<string, CatalogPathStat[]>?`, keyed by vendor; an empty path list records
an empty array). A baseline for a vendor the watcher never watches is inert. The watch set
itself is computed after the probe, by one static `VendorCliWatcher.WatchSet(config, factories)`
(unattended ∪ non-empty `CatalogFingerprintPaths` ∪ key in `VendorModels`), and
`VendorCliWatcher.ExecuteAsync` reads both baseline tables, `PrimeBaselines` seeding from them
and statting live only for a vendor with no recorded entry. A binary or file that changes
between the startup probe and the watcher's first tick therefore reads as a change on that tick
instead of becoming the baseline.
`CatalogPathStat(string Path, bool Exists, long Length, long LastWriteTicks)` is a new record in
`Services/`.

## 4. Wire (kcap-cli)

### 4.1 Server registration

`DaemonConnect` (`Capacitor.Cli.Core/Models.cs`) gains a trailing optional member:

```csharp
Dictionary<string, VendorModelOption[]>? VendorModels = null,
```

serialized as `vendor_models`: the hub protocol is configured with `SnakeCaseLower` and
`CapacitorJsonContext` uses the same policy, so the server contract binds `vendor_models` and a
serialization test pins that spelling. `ServerConnection.DaemonConnectCoreAsync` passes
`_config.VendorModels`. Old servers ignore the field.

### 4.2 Local status IPC

`DaemonInfoDto` (`Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`) gains a trailing optional member:

```csharp
Dictionary<string, VendorModelOption[]>? VendorModels = null
```

serialized as `vendor_models` (`StatusIpcJsonContext` is `SnakeCaseLower`). Same additive rule
as `SupportedVendors`: null from an older daemon means unknown. The context needs the dictionary
and array types registered. `DaemonStatusIpc.Snapshot` passes `config.VendorModels`.

### 4.3 Remote registry DTO

`DaemonInfo` (`Capacitor.Remote.Models/DaemonInfo.cs`) gains:

```csharp
[JsonPropertyName("vendor_models")] public Dictionary<string, VendorModelOptionDto[]>? VendorModels { get; init; }
```

reusing the existing `VendorModelOptionDto` (`value` / `label`). `RemoteModelsJsonContext`
registers the dictionary type.

All three are AOT-published types; `dotnet publish -c Release` must stay free of IL2026/IL3050.

## 5. Server (kcap-server)

The field follows `UnattendedVendors` through every hop, null-stays-null:

1. `DaemonConnectArgs` (`Capacitor.Api.Public.Abstractions/Agents/Daemon/`): trailing optional
   `Dictionary<string, LaunchModelOption[]>? VendorModels`.
2. `CapacitorHub.DaemonConnect` → `DaemonRegistry.Register` → `DaemonEntry.VendorModels`
   (positional trailing, so existing constructions still bind).
3. Core `DaemonInfo` (`Capacitor.Server.Core/DaemonInfo.cs`), `ConnectedDaemon`, and
   `ConnectedDaemonMapping` carry it to the public `DaemonInfo` as `vendor_models`.
4. `DaemonView` (`Capacitor.Api.Web.Abstractions/Agents/`) gains
   `IReadOnlyDictionary<string, IReadOnlyList<VendorModelOptionView>>? VendorModels`, mapped in
   `DaemonWebDataService`.
5. `LaunchAgentDialog.razor`: `ModelOptionsForSelectedVendor` becomes

   ```
   selected daemon's VendorModels has key _selectedVendor  → that list
   else                                                    → _modelOptions[_selectedVendor] (existing)
   ```

   The "Default" and "Custom…" rows are unchanged. Changing the target daemon re-evaluates the
   list and clears a selected model that is no longer offered (same rule the vendor change
   already applies).

`GET api/agents/model-options` and `VendorModelCatalog` are unchanged. The mobile launcher keeps
reading the endpoint; adopting the daemon list there is a follow-on.

## 6. Desktop app (kcap-cli, `Capacitor.App`)

- `MachineOption` gains `IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? VendorModels`,
  filled from the remote `DaemonInfo.VendorModels` for remote machines and from the latest local
  status snapshot for the local machine.
- `HomeViewModel` derives one observable, the **machine catalog**:
  `daemon.Snapshots.Select(s => s.Daemon.VendorModels).StartWith(null)` combined with `_daemons`
  and `_machineSelectionChanges`. `Snapshots` is a replay subject with no initial value, so the
  `StartWith(null)` is what lets the pipeline emit before the first local status snapshot (the
  `Harnesses` pipeline does the same). When the local machine is selected the value is the latest
  local snapshot's dictionary (null before one arrives); when a remote machine is selected it is
  the `VendorModels` of the daemon that `FindMachine(list, sel.Name, _lastViewerId)` resolves in
  the latest `_daemons` emission — the same name-and-owner predicate the availability pipeline
  uses, so a same-named daemon owned by someone else never supplies the list and a re-registration
  by the selected daemon updates the picker while it stays selected. A remote machine `FindMachine`
  does not resolve yields null (unknown), not the last value. `FindMachine` populates
  `MachineOption.VendorModels` from the `DaemonInfo`.
- The server catalog subscription and the machine catalog are combined into
  `EffectiveModelCatalog` (`IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>`), an
  `ObservableAsPropertyHelper` on the main thread with initial value
  `ServerVendorModelCatalog.Empty`, replacing the `ModelCatalog` property. Per vendor: machine key
  present → machine list (even when empty); else server list when non-empty; else no key.
  `ModelChoicesFor(vendor)` reads it and falls back to `HostedHarnessCatalog.ModelChoicesFor(vendor)`
  when the key is absent.
- **Selected-model revalidation.** `SelectedModel` is a session value; `SetVendor` clears it
  only on a vendor change. `EffectiveModelCatalog` now also revalidates it on every emission,
  using the previous and next values (`Scan`/pairwise): when `SelectedModel` is non-empty, the
  previous catalog had a key for `SelectedVendor` that contained it, and the next catalog has a
  key for `SelectedVendor` that does not, `SelectedModel` resets to `""`. This covers a machine
  switch to a same-vendor machine with a disjoint list and a live refresh that withdraws the
  model. A model the previous catalog did not list (a typed custom id, or one picked before any
  catalog arrived) is never cleared by a catalog change: the daemon is the authority at launch,
  and clearing a custom id on every emission would make the custom row unusable.
- The agent chip's fourth `MultiBinding` in `LauncherPaneView.axaml` binds
  `EffectiveModelCatalog` instead of `ModelCatalog`; `AgentChipTextConverter` is unchanged, since
  it already resolves the label against that dictionary and falls back to the curated
  `HostedHarnessCatalog.ModelLabelFor`. Any change to the local snapshot, the selected machine,
  the registry list or the server catalog re-emits the property, so the chip re-renders and the
  next flyout open reads the new rows (`RebuildRows` runs per open, unchanged).
- The Pi rows render Label as the title; the search box matches on both Value and Label, so
  `opus`, `anthropic` and `github-copilot/` all narrow the list. The typed-term custom row stays.
- "Default — Pi chooses" stays as the `""` row, which the daemon resolves to its configured
  `PiModel` or Pi's own default.

## 7. Compatibility

| Pair | Behaviour |
|---|---|
| New daemon, old server | Server ignores `vendorModels`; web dialog unchanged. |
| Old daemon, new server / app | Field null → consumers use the server catalog and curated fallback exactly as today. |
| New app, old daemon | Local snapshot has no field → same as above. |
| New daemon, Pi missing | Pi is not in `SupportedVendors`, no probe, no key. |
| New daemon, Pi with no auth | Key present, empty array → launchers show Default only. |

No persisted data changes. No `FrameType` changes: the status IPC payload is a JSON body inside
the existing `DaemonStatus` frame.

## 8. Testing

Daemon (`test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi/`, `Services/`):

- `PiModelCatalogProbe.Parse`: success with three models across two providers → three options
  with `provider/id` values and `name · provider` labels; entry missing `id` skipped; missing
  `name` falls back to id; `success:false` → null; wrong `command` → not a response; non-JSON
  line → not a response.
- `PiModelCatalogProbe.Parse`, envelope cases: `success:true` with no `data` → null; `models`
  missing → null; `models` a string → null; `models: []` → empty list (not null); a non-object
  entry inside a valid array is skipped and the rest kept.
- Probe against a fake `pi` from `tmp.CreateExecutable` that emulates Pi: reads one command line,
  prints the canned response, then keeps running until stdin EOF and exits on it. Asserts the
  mapped list, that the response was received before stdin was closed (the fake exits non-zero
  if it sees EOF before a command), and that the child is gone afterwards (`PidIdentity`).
- Fake `pi` that never answers: probe returns null within the deadline (fake `TimeProvider`),
  child killed.
- The probe's argv carries `--offline`, `--no-extensions` and `--no-session`, and its working
  directory is the empty probe directory under the state dir, not the daemon's cwd (assert on the
  built `ProcessStartInfo`, and on the fake `pi` recording its cwd).
- `VendorCliWatcher`: a changed catalog fingerprint path fires one refresh naming the vendor; an
  unchanged path fires none; a vendor in the catalog set but not unattended is watched; a
  recorded baseline that differs from the file at watcher start fires on the first tick; a
  deleted `auth.json` fires.
- `RefreshAdvertisedCapabilities`: differing catalog re-registers and pulses the notifier;
  an equal-content re-probe (fresh objects, same values) does neither and leaves the reference
  untouched; null re-probe keeps the previous entry; a status subscriber serializing while a
  refresh publishes sees either the old or the new dictionary, never a partial one.
- `VendorModelCatalogs.Equal`: same keys and ordered pairs → true; reordered models, a changed
  label, or an extra key → false.
- Startup baselines: a catalog-only vendor (Pi advertised, reviewer withheld) has its binary and
  catalog paths recorded, and a binary change before watcher start fires on the first tick; a
  producer with empty `CatalogFingerprintPaths` that returns a catalog has its binary recorded
  and is in the watch set.
- Probe directory: created when missing, emptied when it holds a leftover file, works on Windows
  (no owner-only gate).
- `DaemonConnect` and `DaemonStatusDto` round-trip through their source-generated contexts with
  the field null, absent-key, and present-empty, and the serialized property is spelled
  `vendor_models` on both.
- `DaemonStatusIpc`: a subscriber receives a second snapshot after a catalog refresh.

App (`test/Capacitor.App.Tests.Unit/`, beside `HomeViewModelTests`):

- Precedence: daemon list wins over a non-empty server list; empty daemon key wins over both
  fallbacks; absent key falls to server; empty server falls to curated.
- Before any local snapshot: `EffectiveModelCatalog` is empty, a remote selection shows that
  machine's list, and the server catalog alone populates the local view.
- A same-named daemon owned by another user in the `_daemons` emission never supplies the list
  for the selected remote machine.
- Selecting a remote machine switches to that machine's list; switching back to local restores
  the local one; a new `_daemons` emission with a changed list for the selected remote machine
  updates `EffectiveModelCatalog` without reselecting; the machine vanishing from the emission
  falls back to the server catalog.
- Chip label resolves from the daemon list through `EffectiveModelCatalog` (converter test with
  a machine-only entry).
- Selected-model revalidation: Pi model A picked from machine 1's list, switch to machine 2
  whose Pi list lacks A → `SelectedModel` is `""`; a live refresh that drops A → `""`; a typed
  custom id survives both a machine switch and a refresh; a model picked before any catalog
  arrived survives the first catalog emission.

Server (kcap-server unit suites beside `DaemonRegistry` and the dialog):

- Registry stores and exposes `VendorModels`; null stays null across re-register; a
  re-register with a new list replaces the old one.
- Hub → registry → public `DaemonInfo` mapping carries `vendor_models`.
- Dialog: daemon key present → daemon list; absent → catalog; switching daemons clears a model
  the new daemon does not offer.

Manual check on a machine with Pi: open the desktop launcher, pick Pi, see the machine's models;
run `pi /login` for a new provider, wait one watcher tick, reopen the flyout, see the new
provider's models without a daemon restart.

## 9. Documentation

- `README.md`: no CLI surface changes (no new command or flag). Nothing to update.
- The daemon's startup log gains one line per catalog-publishing vendor: `pi: 51 models`, or the
  probe failure reason.
