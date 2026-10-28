# Experimental Features Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-profile experimental-features switch, applied to the running daemon live, with `codex-app-server` as the first feature replacing `KCAP_CODEX_TRANSPORT` and `KCAP_CODEX_APPSERVER_INTERACTIVE`

**Architecture:** A feature registry and an `experimental.json` store in `Capacitor.Cli.Core`. The daemon folds the switch and the probed Codex version into one immutable resolution inside its single-flighted capability refresh, routes launches from it, reports it in local status and re-advertises it. The server pins every Codex launch's transport with an additive `ExpectedRuntimeTransport` field so a flip never strands a launch in flight

**Tech Stack:** .NET 10 (NativeAOT for the CLI), Avalonia + ReactiveUI (desktop app), SignalR, TUnit

**Spec:** `docs/superpowers/specs/2026-10-25-experimental-features-design.md` (kcap-cli). Read it before any task

## Global Constraints

- Feature id `codex-app-server`; display name "Codex app-server"; off by default
- Codex version floor `0.146.0` (`CodexTransportDecision.VersionFloor`), unchanged
- Store file `experimental.json` under `ConfigRoot`, shape `{ "<profile>": { "<feature-id>": true|false } }`; unknown profiles and ids preserved on write
- Wire field name `ExpectedRuntimeTransport`, values `"app-server"` / `"pty"`, appended last on both `LaunchAgentCommand` records and on `DaemonLaunchCommand`; never a new hub arity
- Local IPC is append-only: new status fields go last with null defaults; new capability string `experimental/1`
- PR reviews and `kcap agent start` local spawns stay on PTY in every case
- No restart prompt anywhere for this feature
- Removed env vars produce one startup warning naming `kcap experimental enable codex-app-server`
- CLI surface changes update `README.md` and `src/Capacitor.Cli.Core/Resources/help-*.txt` in the same task
- After CLI changes: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing
- Run tests method-targeted with `--treenode-filter`, one test process at a time; never a full suite locally
- Comments follow each repo's AGENTS.md/CLAUDE.md comment rules: scarce, no history, no spec coordinates

## Review Focus

- A hand-edited, half-written or corrupt `experimental.json`: reads as all-off with a reason, and a save never overwrites it (Task 1)
- Toggling twice quickly: the daemon's final resolution matches the final saved value, and status never shows the intermediate one as current (Task 5)
- Codex binary missing or its version unreadable with the switch on: "on but not active" with a reason, never an exception in refresh (Task 5)
- The desktop app attached to profile A while the CLI flips profile B: A's tab and A's daemon are unaffected (Tasks 1, 8)
- An older daemon or an older server on either side: shown as unknown, launches keep working (Tasks 3, 6, 7, 9)

---

## kcap-cli (`src/cli`)

### Task 1: Feature registry and `experimental.json` store

**Files:**
- Create: `src/Capacitor.Cli.Core/Config/ExperimentalFeature.cs`, `src/Capacitor.Cli.Core/Config/ExperimentalFeatures.cs`, `src/Capacitor.Cli.Core/Config/ExperimentalSettingsStore.cs`, `src/Capacitor.Cli.Core/Config/ExperimentalSettingsRead.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Config/ExperimentalSettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ExperimentalFeature(string Id, string DisplayName, string Description, bool AppliesLive)`
  - `public static class ExperimentalFeatures { public static ExperimentalFeature CodexAppServer { get; } public static IReadOnlyList<ExperimentalFeature> All { get; } public static ExperimentalFeature? Find(string id); }`
  - `public sealed record ExperimentalSettingsRead(bool Readable, IReadOnlyDictionary<string, bool> Features, string? Problem)` with `bool IsEnabled(string id)` (absent → false)
  - `public sealed class ExperimentalSettingsStore(ConfigRoot root) { public const string FileName = "experimental.json"; public string Path { get; } public ExperimentalSettingsRead Load(string profile); public Task<bool> SetAsync(string profile, string featureId, bool enabled, CancellationToken ct); }`

- [ ] **Step 1: Write the failing tests** (`[TempDir]` injection, a `ConfigRoot` over it)
  - `Missing_file_reads_every_feature_off`: `Load("default").Readable` true, `IsEnabled("codex-app-server")` false
  - `Set_then_load_round_trips_per_profile`: set `default/codex-app-server=true`; `Load("default")` true, `Load("work")` false
  - `Set_preserves_unknown_profiles_and_ids`: seed `{"work":{"future-x":true},"default":{"future-y":false}}`, set `default/codex-app-server=true`; the file still holds `work.future-x=true` and `default.future-y=false`
  - `Corrupt_file_reads_off_with_a_problem_and_is_never_overwritten`: seed `{"default":`; `Load` → `Readable` false, `Problem` not null; `SetAsync` returns false; file bytes unchanged
  - `Non_boolean_value_reads_as_off`: seed `{"default":{"codex-app-server":"yes"}}` → `IsEnabled` false, `Readable` true
- [ ] **Step 2: Run them, expect compile failure**
  Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ExperimentalSettingsStoreTests/*"`
- [ ] **Step 3: Implement.** Parse with `JsonNode` (AOT-safe; `new JsonObject()` constructors, no collection expressions over `JsonArray`). Writes take `root.AcquireLock(FileName)`, re-read inside the lock, write to a temp file in the same directory and `File.Move(..., overwrite: true)`. `CodexAppServer`: `AppliesLive = true`, description "Run Codex reviewers and interactive Codex agents over codex app-server instead of the terminal. Needs Codex 0.146.0 or later"
- [ ] **Step 4: Run the tests, expect PASS**
- [ ] **Step 5: Commit** `Add the experimental features registry and store`

### Task 2: Remove the Codex environment switch

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexTransportDecision.cs` (delete `BindFromEnvironment`, `IsInteractiveOptIn`, `InteractiveTransport(DaemonConfig)`, `UsesAppServer(string?, string?)`, `ResolveActive`; keep the constants, `MeetsFloor`, `ParseSemver`)
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs:219-240` (delete `CodexTransport`, `CodexAppServerActive`, `CodexAppServerInteractive`)
- Modify: `src/Capacitor.Cli/Services/ServiceEnvironment.cs:25` (drop the two keys)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs:210-214` (startup now builds the first resolution in Task 5; here, add the leftover-variable warning)
- Create: `src/Capacitor.Cli.Daemon/Harness/Codex/LegacyCodexEnvironment.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexTransportDecisionTests.cs`, `test/Capacitor.Cli.Tests.Unit/Services/ServiceEnvironmentTests.cs:281-288`, new `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/LegacyCodexEnvironmentTests.cs`

**Interfaces:**
- Produces: `internal static class LegacyCodexEnvironment { public static readonly string[] RemovedVariables = ["KCAP_CODEX_TRANSPORT", "KCAP_CODEX_APPSERVER_INTERACTIVE"]; public static string? Warning(Func<string, string?> getEnv); }` returns null when neither is set, else one line naming the variables found and `kcap experimental enable codex-app-server`

This task leaves the build red until Task 5 provides the resolution; do Tasks 2-5 on one branch and commit each, building only at the end of Task 5. Keep `MeetsFloor` tests; delete tests of removed members

- [ ] **Step 1: Write the failing tests**
  - `Warning_is_null_when_no_removed_variable_is_set`
  - `Warning_names_each_variable_set_and_the_command` (both set → message contains both names and `kcap experimental enable codex-app-server`)
  - In `ServiceEnvironmentTests`: replace the pin at :281-288 with `Removed_codex_variables_are_not_carried_into_the_unit`
- [ ] **Step 2: Delete the members and keys listed above; implement `LegacyCodexEnvironment`; log its warning once at startup in `DaemonRunner` where `BindFromEnvironment` was called**
- [ ] **Step 3: Commit** `Remove the Codex transport environment variables`

### Task 3: `ExpectedRuntimeTransport` on the daemon's launch path

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs:1718` (append `string? ExpectedRuntimeTransport = null` after `TitleDerived`)
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs:292` (add `string? ExpectedRuntimeTransport = null` after `RequestedTransport`)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:2898` (pass `cmd.ExpectedRuntimeTransport`), and its launch admission (reject an unrecognised value)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LaunchAgentCommandWireFormatTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/SequencedSettlementWireTests.cs`, the orchestrator launch tests that cover `LaunchRejected`

**Interfaces:**
- Produces: `LaunchAgentCommand.ExpectedRuntimeTransport`, `RuntimeStartContext.ExpectedRuntimeTransport`; recognised values are `CodexTransportDecision.AppServer` and `CodexTransportDecision.Pty`

- [ ] **Step 1: Write the failing tests**
  - Wire: `Expected_runtime_transport_binds_by_name` (JSON with `"ExpectedRuntimeTransport":"app-server"` → field set); `Absent_expected_runtime_transport_binds_null`
  - Orchestrator: `Unrecognised_expected_runtime_transport_is_rejected_as_semantic` (`"tty"` on a Codex launch → `CommandOutcomeKind.LaunchRejected`, `CommandRejectedReason.Semantic`, launch-failed reason names the value)
- [ ] **Step 2: Add the fields, the plumbing and the admission check next to the existing `CodexPosturePolicy` rejection**
- [ ] **Step 3: Commit** `Carry the server's expected transport to the Codex launch`

### Task 4: Router precedence

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHostedAgentRuntimeFactory.cs:124-141`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexHostedAgentRuntimeFactoryTests.cs` (replace the `UsesAppServer` tests at :90-115 with one table)

**Interfaces:**
- Consumes: `RuntimeStartContext.RequestedTransport`, `.ExpectedRuntimeTransport` (Task 3); `CodexTransportResolution` and `DaemonConfig.AcknowledgedCodexResolution` (Task 5)
- Produces: `internal CodexRoute Route(RuntimeStartContext ctx)` returning `public readonly record struct CodexRoute(bool AppServer, string? Refusal)`; `StartAsync` fails the launch with `Refusal` when set

Precedence, first match wins:
1. `RequestedTransport` (resume ledger)
2. `ExpectedRuntimeTransport`
3. `ctx.IsReview && !ctx.IsReviewFlow` (PR review) → PTY
4. field absent → `AcknowledgedCodexResolution.Active` for review-flow and interactive

A pinned app-server (1 or 2) whose Codex fails the floor → `Refusal` naming the installed version and `0.146.0`; the floor check uses the committed resolution's probed version, else probes once via `DaemonRunner.ProbeCliVersionForLaunch(codexPath)`. The existing PTY review-flow resume refusal stays

- [ ] **Step 1: Write the failing table test** `Route_follows_precedence` with rows (requested, expected, kind, acknowledged active, version → app-server?, refused?):
  - `pty`, `app-server`, interactive, on, 0.146.0 → PTY
  - `app-server`, `pty`, interactive, off, 0.146.0 → app-server
  - null, `app-server`, review-flow, off, 0.146.0 → app-server
  - null, `pty`, review-flow, on, 0.146.0 → PTY
  - null, `app-server`, interactive, on, 0.145.9 → refused
  - null, null, PR review, on, 0.146.0 → PTY
  - null, null, review-flow, on, 0.146.0 → app-server
  - null, null, interactive, off, 0.146.0 → PTY
- [ ] **Step 2: Write** `Pty_review_flow_resume_is_still_refused`
- [ ] **Step 3: Implement `Route`; delete `UsesAppServer`; update its callers**
- [ ] **Step 4: Commit** `Route Codex launches by pinned transport before the switch`

### Task 5: One live resolution per refresh

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexTransportResolution.cs`, `src/Capacitor.Cli.Daemon/Services/ExperimentalSettingsWatcher.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (startup resolution at :210-214; `ComputeUnattendedVendorCapabilities` at :1573 and `LauncherPolicyVersion` callers at :1594; hosted-service registration beside `VendorCliWatcher` at :626), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:1584-1624`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexTransportResolutionTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorCodexResolutionTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/DaemonRunnerInteractiveTransportAdvertisementTests.cs`

**Interfaces:**
- Consumes: `ExperimentalSettingsStore`, `ExperimentalFeatures.CodexAppServer` (Task 1)
- Produces:
  - `internal sealed record CodexTransportResolution(long Generation, bool SwitchOn, string? CliVersion, bool Active, string? InactiveReason) { public string InteractiveTransport { get; } public string LauncherPolicyVersion { get; } public static CodexTransportResolution Resolve(long generation, bool switchOn, Func<string?> probe); }` (probe runs only when `switchOn`; `InactiveReason` is `"Codex {v} is below 0.146.0"` or `"Codex version could not be read"`)
  - `DaemonConfig.CodexResolution` (volatile reference, committed) and `DaemonConfig.AcknowledgedCodexResolution` (set after a successful `ReRegisterAsync`); `DaemonConfig.ServerAcknowledgedCodexGeneration => AcknowledgedCodexResolution.Generation`
  - `ComputeUnattendedVendorCapabilities(..., CodexTransportResolution codex, ...)` reads Codex version, policy and interactive transport from `codex`, never probing Codex itself
  - `ExperimentalSettingsWatcher : BackgroundService` watches the store's directory for `experimental.json`, debounces 250 ms, calls `orchestrator.RefreshAdvertisedCapabilities("experimental settings changed")`

Refresh pass, inside the existing single-flight: read the switch → `Resolve(next generation)` → re-read the switch, rerun on change → swap `CodexResolution` → `_statusNotifier.Pulse()` → compute capabilities from the resolution → `ReRegisterAsync` → on success set `AcknowledgedCodexResolution`, pulse again. Every pass resolves, so the `VendorCliWatcher` trigger also catches a Codex upgrade or downgrade

- [ ] **Step 1: Write the failing tests**
  - `Resolve_does_not_probe_when_the_switch_is_off`
  - `Resolve_is_inactive_below_the_floor_with_a_reason` (0.145.9) and `..._when_the_version_is_unreadable` (null)
  - `Refresh_commits_one_generation_for_routing_advertisement_and_status` (capabilities' Codex `CliVersion`, policy and `InteractiveTransport` equal the committed resolution's)
  - `Refresh_reruns_when_the_switch_flips_during_the_probe` (probe delegate flips the store; committed resolution reflects the final value)
  - `Refresh_pulses_status_when_only_the_resolution_changed`
  - `Failed_reregistration_keeps_the_commit_and_leaves_acknowledgement_behind` (`ReRegisterAsync` throws → `CodexResolution.Generation` > `AcknowledgedCodexResolution.Generation`)
  - `Codex_upgrade_past_the_floor_activates_a_switched_on_feature` (refresh with version 0.145.0 then 0.146.0)
  - `Watcher_triggers_refresh_on_store_write` (`[TempDir]`; write via `ExperimentalSettingsStore`; refresh delegate observed within 2 s)
- [ ] **Step 2: Implement; startup builds generation 1 before the first registration and sets both references after it succeeds**
- [ ] **Step 3: Build the daemon project and run Tasks 2-5 tests, expect PASS**
  Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`, then each test class via `--treenode-filter`
- [ ] **Step 4: Commit** `Resolve the Codex switch live inside the capability refresh`

### Task 6: Experimental state in local status

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/ExperimentalFeatureStatusDto.cs`, `src/Capacitor.Cli.Core/Config/ExperimentalFeatureState.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs:10` (append `List<ExperimentalFeatureStatusDto>? Experimental = null` to `DaemonStatusDto`; register the DTO in `StatusIpcJsonContext`), `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs:62-79`, `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs:11` (append `"experimental/1"`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Config/ExperimentalFeatureStateTests.cs`, the existing `DaemonStatusIpc` snapshot tests

**Interfaces:**
- Produces:
  - `public sealed record ExperimentalFeatureStatusDto(string Id, bool ResolvedSaved, bool Active, long Generation, bool ServerAcknowledged, string? Reason)`
  - `public enum ExperimentalFeatureStateKind { Off, Active, OnNotActive, Applying, AppliedLocally, Unknown, DaemonNotRunning }`
  - `public static class ExperimentalFeatureState { public static (ExperimentalFeatureStateKind Kind, string? Reason) Classify(bool saved, bool daemonRunning, DaemonStatusDto? status, string featureId); }` — the one rule both surfaces use: no daemon → `DaemonNotRunning`; status without `Experimental` → `Unknown`; `ResolvedSaved != saved` → `Applying`; `Active && !ServerAcknowledged` → `AppliedLocally`; `Active` → `Active`; saved on, not active → `OnNotActive` with the DTO's reason; else `Off`

- [ ] **Step 1: Write the failing table test** `Classify_covers_every_state` (one row per kind above)
- [ ] **Step 2: Write** `Snapshot_reports_the_committed_codex_resolution` in the daemon status tests
- [ ] **Step 3: Implement**
- [ ] **Step 4: Commit** `Report experimental feature state in daemon status`

### Task 7: `kcap experimental`

**Files:**
- Create: `src/Capacitor.Cli/Commands/ExperimentalCommand.cs`, `src/Capacitor.Cli.Core/Resources/help-experimental.txt`
- Modify: `src/Capacitor.Cli/Program.cs:277` (offline list) and `:492` (dispatch case), `src/Capacitor.Cli/Commands/CommandServices.cs:100`, `src/Capacitor.Cli.Core/Resources/help-usage.txt`, `README.md` (`## CLI commands` and, if it mentions the Codex variables, every such place)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ExperimentalCommandTests.cs`

**Interfaces:**
- Consumes: `ExperimentalSettingsStore`, `ExperimentalFeatures`, `ExperimentalFeatureState.Classify`, `ILocalControlOps` status reads
- Produces: `public sealed class ExperimentalCommand(ConfigRoot config, DaemonStore daemons, TimeProvider time) { public Task<int> HandleAsync(string[] args); }`

`kcap experimental` lists each feature: id, saved on/off, state text. `enable|disable <id>` writes the active profile, then polls status every 250 ms for up to 5 s and prints one line per spec: `Applied live`; `Saved, but not active: <reason>`; `Applied locally; the server has not been told yet`; `Saved; the running daemon is too old to apply it. Update kcap and restart the daemon`; `Saved; the daemon did not confirm in time. Run kcap experimental to check`; `Saved; the daemon is not running and will apply it when it starts`. "Applied live" only when `Classify` returns `Active` or `Off` matching the saved value. Unknown id → exit 1, lists known ids. Corrupt store → exit 1 with the store's problem and path

- [ ] **Step 1: Write the failing tests** (console capture, bare `[NotInParallel]`, a fake `ILocalControlOps`)
  - `Enable_prints_applied_live_once_status_reports_the_new_generation`
  - `Enable_against_an_older_daemon_prints_too_old` (status without `Experimental`)
  - `Enable_times_out_with_the_check_hint` (`TimeProvider` advanced past 5 s)
  - `Enable_with_no_daemon_prints_applies_on_start`
  - `Unknown_feature_is_refused_with_known_ids`
  - `Corrupt_store_is_refused_and_left_untouched`
  - `List_shows_each_registered_feature`
- [ ] **Step 2: Implement; help file and README in the same commit**
- [ ] **Step 3: Run the tests; then the AOT publish check from Global Constraints**
- [ ] **Step 4: Commit** `Add kcap experimental to toggle experimental features`

### Task 8: Desktop Experimental tab

**Files:**
- Create: `src/Capacitor.App/ViewModels/ExperimentalSettingsViewModel.cs`, `src/Capacitor.App/ViewModels/ExperimentalFeatureRowViewModel.cs`
- Modify: `src/Capacitor.App/Views/SettingsWindow.axaml` (new `TabItem x:Name="ExperimentalTab" Classes="kcapSettingsTab" Header="Experimental"` after Privacy at :260), `src/Capacitor.App/ViewModels/SettingsViewModel.cs` (an `Experimental` property like `Privacy`), `src/Capacitor.App/App.axaml.cs:1037-1046` (construct it)
- Test: `test/Capacitor.App.Tests.Unit/ExperimentalSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `ExperimentalSettingsStore`, `ExperimentalFeatures.All`, `ExperimentalFeatureState.Classify`, `IDaemonClientService.Snapshots`, the attached profile name from `SettingsProfileStore.ProfileName`
- Produces: `ExperimentalSettingsViewModel(ExperimentalSettingsStore store, string profile, IDaemonClientService service)` with `IReadOnlyList<ExperimentalFeatureRowViewModel> Features`; each row has `DisplayName`, `Description`, `bool IsEnabled` (two-way, saves on change), `string StatusText`

Row status text per kind: Active "Active"; OnNotActive "On, not active: <reason>"; Applying "Applying…"; AppliedLocally "Applied; the server has not been told yet"; Unknown "This daemon is too old to apply it. Update kcap and restart the daemon"; DaemonNotRunning "Saved; applies when the daemon starts"; Off "". Status text uses `KcapMutedBrush`, and `KcapWarningBrush` only for OnNotActive and Unknown. Toggle style follows the Notifications tab. Filter snapshots before `ObserveOn(MainThreadScheduler)`

- [ ] **Step 1: Write the failing tests**
  - `Toggling_saves_to_the_attached_profile_only`
  - `Status_text_follows_each_snapshot` (feed snapshots through a `Subject<DaemonStatusDto>`)
  - `Corrupt_store_disables_the_toggles_with_the_problem`
- [ ] **Step 2: Implement the view model and tab**
- [ ] **Step 3: Commit** `Add the Experimental settings tab`

## kcap-server

### Task 9: Wire field on the server's launch records

**Files:**
- Modify: `src/Capacitor.Api.Public.Abstractions/Agents/Commands/LaunchAgentCommand.cs:97`, `src/Capacitor.Server.Services/Flows/Settlement/DaemonLaunchCommand.cs:39` (append `string? ExpectedRuntimeTransport = null` to both), `src/Capacitor.Api.Public/Agents/DaemonCommandExtensions.cs:14-45` (map it)
- Test: `test/Capacitor.Server.Tests.Flows/DaemonCommandParityTests.cs:20-45` (fixture sets it), `test/Capacitor.Server.Tests.Flows/WireCompatTests.cs`, `test/Capacitor.Server.Tests.Agents/SequencedSettlementWireTests.cs`

Read `docs/gotchas/SIGNALR.md` before starting

- [ ] **Step 1: Extend the parity fixture; add** `Prepared_launch_without_the_field_still_replays` (a stored `DaemonLaunchCommand` JSON lacking the property deserializes with null and its normalized body hash matches)
- [ ] **Step 2: Add the field and mapping; run the parity, wire-compat and settlement wire tests**
- [ ] **Step 3: Commit** `Add the expected runtime transport to daemon launch commands`

### Task 10: Per-class expectation, recorded and sent together

**Files:**
- Create: `src/Capacitor.Server.Services/Agents/LaunchTransportExpectation.cs`
- Modify: `src/Capacitor.Api.Public/Sessions/CapacitorHub.cs:1540-1563` (interactive), `src/Capacitor.Api.Public/Sessions/AgentStoreDataService.cs:734-745` (interactive), `:1198`/`:1236` (interactive and direct reviewer), `:1493` (PR review), `:948` and `:1025` (staged reviewer bodies), `src/Capacitor.Api.Public/Flows/FlowReviewerAgentGateway.cs:331` (direct), `:502-515` (foreground), `:876` (recovery), `:1486` (legacy)
- Test: `test/Capacitor.Server.Tests.Agents/LaunchTransportExpectationTests.cs`, plus a send-site test per class in the suites that already cover each path

**Interfaces:**
- Produces: `public static class LaunchTransportExpectation { public static string Interactive(string vendor, DaemonInfo daemon); public static string ReviewFlow(string vendor, string? launcherPolicyVersion); public static string PrReview(string vendor); public static string? Recovery(DaemonLaunchCommand staged); }` — Codex only; non-Codex vendors return `RuntimeTransports.Pty` and the send sites leave the field null for them. `Interactive` absorbs `InteractiveLaunchIntentRecorder.ExpectedTransport`; `ReviewFlow` wraps `RuntimeTransports.FromLauncherPolicy`

Each Codex send site computes the value once, records it (launch intent or `ReviewerLaunchTransportExpectations.Record`) and passes the same value as `ExpectedRuntimeTransport`. Direct and legacy reviewer paths gain the `Record` they lack. Recovery re-records the staged body's value. PR review records and sends `pty`

- [ ] **Step 1: Write the failing tests**
  - `Pr_review_is_always_pty`
  - `Review_flow_follows_the_selected_policy` (`codex-appserver-unattended-v2` → app-server, `codex-unattended-v1` → pty)
  - Per send site: `<Path>_sends_the_transport_it_records` for interactive hub, started agent, direct reviewer, foreground reviewer, legacy reviewer, recovery, PR review
  - `Recovery_reuses_the_staged_transport_after_a_flip` (staged app-server, daemon now advertising pty → sent and recorded app-server)
- [ ] **Step 2: Implement; delete `InteractiveLaunchIntentRecorder.ExpectedTransport` once its callers use the helper**
- [ ] **Step 3: Run the touched suites method-targeted, then** `dotnet run --project test/Capacitor.Source.Tests/Capacitor.Source.Tests.csproj`
- [ ] **Step 4: Commit** `Pin every Codex launch to the transport the server expects`

## Shipping

- kcap-cli lands first: Tasks 1-8 in one PR. The daemon tolerates a server without the field (per-class fallback on the acknowledged resolution)
- kcap-server Tasks 9-10 in a second PR. The submodule pin bump that brings Tasks 1-8 into kcap-server is Alexey's, not part of either PR
- Manual check before merging the CLI PR: with a local daemon, `kcap experimental enable codex-app-server`, start an interactive Codex agent from the web and a review flow, confirm both run on app-server; `disable`, confirm new launches use PTY while the running agent continues; no daemon restart at any point
