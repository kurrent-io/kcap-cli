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
| Probe mechanism | `pi --mode rpc --no-extensions --no-session`, one `get_available_models` command on stdin. Never table-parsing `--list-models`, never reading Pi's config files. |
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
2. `ProcessStartInfo(config.PiPath, ["--mode", "rpc", "--no-extensions", "--no-session"])`,
   redirected stdin/stdout/stderr, `PiLaunchEnvironment.Apply(env)` so the kcap extension stands
   down even if extension discovery were ever re-enabled. `--no-extensions` also keeps third-party
   extensions (MCP bridges and the like) from starting, which otherwise print to stderr and add
   seconds.
3. Write `{"type":"get_available_models"}\n`, close stdin.
4. Read stdout lines until a line parses as JSON with `type == "response"` and
   `command == "get_available_models"`. Ignore every other line (Pi may emit other frames first).
5. Deadline: 10 seconds from spawn, measured on `TimeProvider`. On expiry, `ProcessTree.Kill`
   the child (the daemon never uses `Process.Kill(bool)`), return null, log once at Warning with
   the reason.
6. On the response: `success != true` → null. Otherwise map `data.models[]` to
   `VendorModelOption($"{provider}/{id}", $"{name} · {provider}")`. An entry missing `id` or
   `provider` is skipped; a missing `name` falls back to `id`. Pi's order is kept (it is the order
   Pi cycles with Ctrl+P).
7. Wait for exit after the response with a short grace (2 seconds), then `ProcessTree.Kill` if
   still running. Exit code is not consulted: the response is the result.

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
`DaemonConfig.VendorModels` (`IReadOnlyDictionary<string, VendorModelOption[]>?`), null only
before startup has run.

Startup cost is one Pi spawn, about 1.5 seconds on a warm machine, run concurrently with the
other factories' probes. It lands before the first `DaemonConnect`, so the server never sees a
registration without it.

### 3.4 Refresh

`VendorCliWatcher` today watches the unattended vendors' CLI binaries. Two changes:

- **Watch set.** Unattended vendors ∪ vendors whose factory has non-empty
  `CatalogFingerprintPaths` or is in `config.VendorModels`. Pi is normally both; the union
  matters when Pi is installed but its reviewer capability is withheld (build too old).
- **Per-vendor fingerprint.** Alongside the binary stat, a stat of each `CatalogFingerprintPaths`
  entry (`FileInfo` existence, length, last-write ticks; a missing file is a distinct value, not
  a transient). A change in any of them counts as that vendor changing. The Pi factory returns
  `PiPaths.AuthJson` and `PiPaths.ModelsJson` (two new members on `PiPaths`, both under
  `AgentDir`).

The existing tick calls `orchestrator.RefreshAdvertisedCapabilities(reason)`. That pass now also
re-runs `ProbeVendorModelsAsync` for every catalog-publishing vendor (one Pi spawn today),
merges the answer into `config.VendorModels` (a null re-probe leaves the previous entry in place
so a transient failure does not withdraw a working list), and treats a differing catalog like a
differing capability: it re-registers with the server and pulses `DaemonStatusNotifier` so local
subscribers receive a new snapshot. An unchanged catalog does nothing.

Baseline rule is unchanged: fingerprints are recorded when the advertisement was computed, so a
file that changes between startup probe and watcher start reads as a change on the first tick.

## 4. Wire (kcap-cli)

### 4.1 Server registration

`DaemonConnect` (`Capacitor.Cli.Core/Models.cs`) gains a trailing optional member:

```csharp
Dictionary<string, VendorModelOption[]>? VendorModels = null,
```

serialized as `vendorModels` under the record's existing naming. `ServerConnection.
DaemonConnectCoreAsync` passes `_config.VendorModels`. Old servers ignore the field.

### 4.2 Local status IPC

`DaemonInfoDto` (`Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`) gains a trailing optional member:

```csharp
Dictionary<string, VendorModelOption[]>? VendorModels = null
```

Same additive rule as `SupportedVendors`: null from an older daemon means unknown. The source
generator context (`StatusIpcJsonContext`) needs the dictionary and array types registered.
`DaemonStatusIpc.Snapshot` passes `config.VendorModels`.

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
- `HomeViewModel` keeps a live `_localVendorModels` from `daemon.Snapshots` (like
  `_currentLocalVendors`), and a `_remoteVendorModels` set by `SelectMachineAsync`.
- `ModelChoicesFor(vendor)`:

  ```
  machine list (local or selected remote) has key vendor → that list (even when empty)
  else server catalog has non-empty list for vendor     → server list
  else                                                   → HostedHarnessCatalog.ModelChoicesFor(vendor)
  ```

- `ModelLabelFor` for the agent chip resolves through the same three sources in the same order.
- A snapshot that changes the local catalog raises `ModelCatalog` so the chip re-renders and the
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
- Probe against a fake `pi` from `tmp.CreateExecutable` that prints a canned response after
  reading stdin: returns the mapped list and the child is gone afterwards (`PidIdentity`).
- Fake `pi` that never answers: probe returns null within the deadline (fake `TimeProvider`),
  child killed.
- `VendorCliWatcher`: a changed catalog fingerprint path fires one refresh naming the vendor; an
  unchanged path fires none; a vendor in the catalog set but not unattended is watched.
- `RefreshAdvertisedCapabilities`: differing catalog re-registers and pulses the notifier;
  identical catalog does neither; null re-probe keeps the previous entry.
- `DaemonConnect` and `DaemonStatusDto` round-trip through their source-generated contexts with
  the field null, absent-key, and present-empty.
- `DaemonStatusIpc`: a subscriber receives a second snapshot after a catalog refresh.

App (`test/Capacitor.App.Tests.Unit/`, beside `HomeViewModelTests`):

- Precedence: daemon list wins over a non-empty server list; empty daemon key wins over both
  fallbacks; absent key falls to server; empty server falls to curated.
- Selecting a remote machine switches to that machine's list; switching back to local restores
  the local one.
- Chip label resolves from the daemon list.

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
