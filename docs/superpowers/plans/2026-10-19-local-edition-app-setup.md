# Local Edition Setup in the Desktop App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop app's setup wizard takes a fresh Mac from the edition screen to a running local stack, picked agents, the daemon service, an offered import and the main window, with no terminal.

**Architecture:** A new Install step drives the bundled CLI's `kcap setup --local --json` through an app-lifetime install controller that owns the child, sends at most one interrupt, and settles one outcome after exit. Routing (gate, edition, navigation locks) reads durable local state from the CLI's lifecycle journal and registry, never from progress lines. Existing Harnesses, History, daemon enabler and Done steps are reused, with their sign-in gates widened to "installed".

**Tech Stack:** .NET 10, Avalonia + ReactiveUI (`Capacitor.App`), `Capacitor.Cli.Core` (process runner, local edition state, telemetry), TUnit.

**Spec:** `docs/superpowers/specs/2026-10-19-local-edition-app-setup-design.md`

## Global Constraints

- Command line, exactly: `setup --local --json --single-user --yes` (the CLI accepts nothing else under `--json` besides `--no-update-check`).
- Success is a `committed` or `reused` line, whatever the exit code; never the exit code alone.
- Retryable codes: `download_failed`, `start_failed`, `verification_failed`, `recovery_failed`, `commit_failed`, `cancelled`, and a non-zero exit with no `failed` line. Busy: `lifecycle_busy`. Every other code is permanent.
- Pending local setup = journal holds an entry whose `Operation` is `setup` (any phase) or the journal is `Unreadable`.
- At most one SIGINT per child; a second cancel request reuses the first.
- `kcap local start` timeout: 11 minutes (above the CLI's 10-minute readiness budget).
- Local Agents visibility: `private`.
- Telemetry event names, fixed once shipped: `cli_setup_local_install_started`, `cli_setup_local_install_committed` (prop `plan`: `fresh`|`reused`|`recovered`), `cli_setup_local_install_failed` (props `code`, `locked`), `cli_setup_local_install_retried`.
- The Local card is available only on macOS and only when no configured profile names a nonblank server that does not classify `LocalEndpointClass.Local`.
- Comments follow `CLAUDE.md`'s rules (scarce, no history); one type per file; tests use `TempDir`/`TempConfigRoot`, `AvaloniaSession.DispatchAsync` for view models, `[NotInParallel("AvaloniaSession")]` where existing neighbours use it.
- Run `dotnet build src/Capacitor.App/Capacitor.App.csproj` and the touched suite after each task; `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must print nothing after Task 1.

## Review Focus

- **Non-JSON or truncated stdout lines** (a warning, a line cut by a kill): ignored, never a failure. Test in Task 2.
- **No CLI resolved** (`CliPath` null): Install settles Failed retryable with "kcap CLI not found", not a hang. Test in Task 4.
- **`download_progress` with `bytes` 0 or missing**: the bar is indeterminate, no divide-by-zero. Test in Task 2.
- **Dashboard URL unknown** (registry unreadable after commit): Open web dashboard hidden, Continue still works. Test in Task 8.
- **A relaunch while the previous app's child still holds the lock**: settles `lifecycle_busy`, locked to local, no Try again. Test in Task 5.

---

### Task 1: Graceful interrupt in the streaming process runner

**Files:**
- Modify: `src/Capacitor.Cli.Core/ProcessRunner.cs` (`RunOptions`, `RunStreamingAsync`)
- Create: `src/Capacitor.Cli.Core/ProcessSignals.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/ProcessRunnerTests.cs`

**Interfaces:**
- Produces: `RunOptions` gains `CancellationToken Interrupt = default` and `TimeSpan InterruptGrace = default` (default treated as 10 s). When `Interrupt` fires, `RunStreamingAsync` sends SIGINT once to the child pid, keeps pumping, and returns the normal `StreamingResult` when the child exits; if it has not exited after `InterruptGrace`, it kills the tree and returns. `ct` keeps its existing hard-kill meaning.
- Produces: `public static class ProcessSignals { public static bool Interrupt(int pid); }` — `kill(pid, SIGINT)` via `[LibraryImport("libc")]` on Unix, `false` on Windows.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test, ParallelLimiter<SubprocessLimit>]
public async Task Interrupt_lets_the_child_finish_its_own_cleanup_and_report() {
    // stub: trap 'echo rolled_back; exit 130' INT; echo started; while :; do sleep 0.05; done
    var script = Tmp.CreateExecutable("trap.sh", "#!/bin/sh\ntrap 'echo rolled_back; exit 130' INT\necho started\nwhile :; do sleep 0.05; done\n");
    using var interrupt = new CancellationTokenSource();
    var lines = new List<string>();
    var run = new ProcessRunner().RunStreamingAsync(script, [], new RunOptions(Interrupt: interrupt.Token),
        l => { lock (lines) lines.Add(l.Text); if (l.Text == "started") interrupt.Cancel(); }, CancellationToken.None);
    var result = await run;
    await Assert.That(result.ExitCode).IsEqualTo(130);
    await Assert.That(lines).Contains("rolled_back");
}

[Test, ParallelLimiter<SubprocessLimit>]
public async Task Interrupt_kills_the_tree_after_the_grace_when_the_child_ignores_it() {
    var script = Tmp.CreateExecutable("ignore.sh", "#!/bin/sh\ntrap '' INT\necho started\nwhile :; do sleep 0.05; done\n");
    using var interrupt = new CancellationTokenSource();
    var result = await new ProcessRunner().RunStreamingAsync(script, [],
        new RunOptions(Interrupt: interrupt.Token, InterruptGrace: TimeSpan.FromMilliseconds(300)),
        l => { if (l.Text == "started") interrupt.Cancel(); }, CancellationToken.None);
    await Assert.That(result.ExitCode).IsNotEqualTo(0);
}
```

- [ ] **Step 2:** Run `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit -- --treenode-filter "/*/*/ProcessRunnerTests/*"`. Expected: compile failure on `Interrupt`.
- [ ] **Step 3:** Implement `ProcessSignals.Interrupt` and the `Interrupt`/`InterruptGrace` handling in `RunStreamingAsync` (register on `Interrupt` to signal once; a linked delay of `InterruptGrace` started at the signal kills the tree).
- [ ] **Step 4:** Re-run the filter. Expected: PASS. Run the AOT publish check from Global Constraints. Expected: no output.
- [ ] **Step 5: Commit** `git commit -m "Let a streamed child finish its own cleanup on interrupt"`

### Task 2: Setup line parsing and the settled outcome

**Files:**
- Create: `src/Capacitor.App/Services/LocalSetup/LocalSetupLine.cs`, `LocalSetupProgress.cs`, `LocalInstallOutcome.cs`, `LocalInstallAffordance.cs`
- Test: `test/Capacitor.App.Tests.Unit/LocalSetupProgressTests.cs`, `LocalInstallOutcomeTests.cs`

**Interfaces:**
- Produces: `sealed record LocalSetupLine(string Event, JsonElement Root)` with `static LocalSetupLine? Parse(string text)` — null for non-JSON, non-object, or no string `event`; any `v` accepted.
- Produces: `sealed class LocalSetupProgress` (ReactiveObject) with `void Apply(LocalSetupLine line)`; properties `InstallRow Download, Verify, Install, FirstStart` (each `RowState { Pending, Running, Done }`), `double? DownloadFraction` (null when `bytes` ≤ 0 or absent), `bool Recovering`, `bool RollingBack`, `bool Reused`, `string? ServerUrl`, `bool SawSuccess`, `string? FailedCode`, `string? FailedMessage`, `string? Plan` (`fresh`|`reused`|`recovered`). Row mapping exactly the spec's table.
- Produces: `enum LocalInstallAffordance { Retry, Busy, Permanent }` with `static LocalInstallAffordance For(string? code)` per Global Constraints (null code → `Retry`).
- Produces: `abstract record LocalInstallOutcome { Installed(string ServerUrl, string Plan); Failed(string? Code, string Message, LocalInstallAffordance Affordance, bool LockedToLocal) }` with `static LocalInstallOutcome Settle(LocalSetupProgress progress, int exitCode, bool lockedToLocal)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Unknown_events_and_garbage_lines_change_nothing() { /* Apply Parse("warning: x"), Parse("{\"v\":2,\"event\":\"embedding_pulling\"}"), Parse("{\"v\":1,\"eve") → all rows Pending, SawSuccess false */ }
[Test] public async Task Download_without_a_size_is_indeterminate() { /* downloading {bytes:0} then download_progress {received:10,bytes:0} → DownloadFraction null, Download Running */ }
[Test] public async Task Committed_after_reused_is_one_success() { /* reused then committed → SawSuccess, Reused, Plan "reused" */ }
[Test] public async Task Committed_with_exit_130_settles_installed() { /* Settle(progress with committed, 130, false) is Installed */ }
[Test] public async Task Commit_failed_before_the_profile_is_retryable_and_locked_when_the_journal_stands() { /* failed{commit_failed}, exit 1, lockedToLocal:true → Failed(Retry, LockedToLocal:true) */ }
[Test] [Arguments("lifecycle_busy", LocalInstallAffordance.Busy)] [Arguments("other_server", LocalInstallAffordance.Permanent)] [Arguments("cancelled", LocalInstallAffordance.Retry)] [Arguments(null, LocalInstallAffordance.Retry)]
public async Task Affordance_follows_the_code_alone(string? code, LocalInstallAffordance expected) { /* For(code) == expected */ }
```

- [ ] **Step 2:** Run the App suite filtered to both classes. Expected: compile failure.
- [ ] **Step 3:** Implement the four types. Read fields with `JsonElementExtensions`.
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Parse local setup progress and settle one install outcome"`

### Task 3: Durable local state and the gate

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalEdition/LocalSetupState.cs`
- Modify: `src/Capacitor.App/Services/Onboarding/OnboardingGate.cs` (`GateReason`, `EvaluateResolvedAsync`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalEdition/LocalSetupStateTests.cs`, `test/Capacitor.App.Tests.Unit/OnboardingGateTests.cs`

**Interfaces:**
- Produces: `public static class LocalSetupState { public static bool Pending(LocalRoot root); public static bool HasForeignServerProfile(ProfileConfig config, LocalEndpoints endpoints); }`. `Pending` is true for `StateRead.Present` with `Operation == "setup"` or `StateRead.Unreadable`. `HasForeignServerProfile` mirrors `LocalPreflight.CheckProfiles` (any profile with nonblank `ServerUrl` whose `endpoints.Classify` is not `Local`); reuse that logic by extracting it rather than copying.
- Produces: `GateReason.LocalSetupPending`, checked first in `EvaluateResolvedAsync` (before `NoProfile`) using the gate's `LocalRoot`.

- [ ] **Step 1: Write the failing tests**

```csharp
// LocalSetupStateTests
[Test] public async Task A_setup_entry_in_any_phase_is_pending() { /* write journal Operation "setup" Phase "committing" → Pending true */ }
[Test] public async Task An_unreadable_journal_is_pending() { /* write "{not json" to the journal path → true */ }
[Test] public async Task An_upgrade_entry_is_not_pending_setup() { /* Operation "upgrade" → false */ }
[Test] public async Task An_inactive_cloud_profile_is_foreign() { /* active profile empty, second profile https://x.kcap.ai → true */ }
// OnboardingGateTests
[Test] public async Task Pending_local_setup_is_incomplete_even_with_a_none_stamped_local_profile() { /* Incomplete(LocalSetupPending) */ }
[Test] public async Task Pending_local_setup_is_incomplete_before_any_profile_exists() { /* no profile + setup journal → Incomplete(LocalSetupPending), not NoProfile */ }
```

Journal fixtures go through the internal `LocalStateFiles.WriteJournal` (Core tests already have IVT); App tests write the file by path from `LocalStateFiles`' public path helper.

- [ ] **Step 2:** Run both filtered suites. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Re-run both. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Hold the onboarding gate open while a local setup is pending"`

### Task 4: CLI facade calls

**Files:**
- Modify: `src/Capacitor.App/Services/KcapCli.cs` (`IKcapCli`, `KcapCli`), `src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs` (`LateBoundKcapCli` forwards)
- Test: `test/Capacitor.App.Tests.Unit/KcapCliTests.cs`

**Interfaces:**
- Produces: `Task<StreamingResult> SetupLocalAsync(Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct)` — runs `setup --local --json --single-user --yes` with `SpawnEnvAsync`, `RunOptions(Interrupt: interrupt)`, no timeout. Null `CliPath` returns `StreamingResult(-1, false, [stderr "kcap CLI not found"])`.
- Produces: `Task<ProcessResult> LocalStartAsync(CancellationToken ct)` — `local start`, `RunOptions(EnvOverlay: SpawnEnv, Timeout: 11 min)`; null `CliPath` → `NoCliResult()`.

- [ ] **Step 1: Write the failing tests** — with the existing fake runner: argv equals `["setup","--local","--json","--single-user","--yes"]`, the env carries `KCAP_PROFILE` and `KCAP_APP_SPAWN_NO_TELEMETRY=1`, `Interrupt` is the token passed; `LocalStartAsync` argv `["local","start"]` and `Timeout == TimeSpan.FromMinutes(11)`; null CliPath for each.
- [ ] **Step 2:** Run filtered. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Run the local install and local start from the app's CLI facade"`

### Task 5: Install controller, telemetry and the Install step

**Files:**
- Create: `src/Capacitor.App/Services/LocalSetup/LocalInstallController.cs`, `src/Capacitor.App/ViewModels/Onboarding/InstallStepViewModel.cs`, `src/Capacitor.App/Views/Onboarding/InstallStepView.axaml(.cs)` (or a template in `OnboardingPaneView.axaml`, matching how the other steps render)
- Modify: `src/Capacitor.App/ViewModels/Onboarding/WizardStep.cs` (`WizardStepId.Install` after `Edition`), `src/Capacitor.Cli.Core/Telemetry/SetupFunnel.cs`
- Test: `test/Capacitor.App.Tests.Unit/LocalInstallControllerTests.cs`, `InstallStepViewModelTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Telemetry/SetupFunnelTests.cs` (or the existing funnel test file)

**Interfaces:**
- Consumes: Task 2 types, `IKcapCli.SetupLocalAsync`, `LocalSetupState.Pending`.
- Produces: `SetupFunnel` methods `LocalInstallStarted()`, `LocalInstallCommitted(string plan)`, `LocalInstallFailed(string code, bool locked)`, `LocalInstallRetried()` emitting the Global Constraints names.
- Produces: `sealed class LocalInstallController(Func<IKcapCli> cli, LocalRoot root, Func<bool> committedLocal, Action<Action> post, SetupFunnel funnel)` with `LocalSetupProgress Progress`, `bool Running`, `LocalInstallOutcome? Outcome`, `event Action<LocalInstallOutcome>? Settled`, `Task StartAsync(CancellationToken ct)` (no-op while running; resets progress; returns once the child is spawned), `void Interrupt()` (idempotent: one shared CTS), `Task SettleAsync()` (interrupts if running, awaits exit; completes immediately when idle). Settles with `lockedToLocal = LocalSetupState.Pending(root) || committedLocal()`.
- Produces: `sealed class InstallStepViewModel(LocalInstallController controller, Func<string?> appProfile, Func<string?> configActiveProfile, Func<bool> onLocalPath)`: `Id = Install`, `PassThrough => !onLocalPath() || controller.Outcome is LocalInstallOutcome.Installed`, `OwnsPrimaryAction`, `Skippable = false`, `CanLeaveAsync(Back)` false while running or locked, `CanLeaveAsync(Next)` only when Installed; `OnEnterAsync` refuses with the profile-mismatch message when `appProfile() != configActiveProfile()` (both non-null), else starts the controller unless already Installed; raises `AdvanceRequested` on Installed; `TryAgainCommand` (visible for `Retry`, emits retried), `CancelCommand` (visible while running → `controller.Interrupt()`), `BackVisible` false when locked or running; texts for each affordance (busy: "Another kcap setup, upgrade or uninstall is running on this Mac."; locked: "The install did not finish.").

- [ ] **Step 1: Write the failing tests** (fake `IKcapCli` scripting lines and exit code)
  - `Cancel_then_settle_sends_one_interrupt` — `Interrupt(); Interrupt(); await SettleAsync()` → the interrupt token fired once; fake records one signal.
  - `A_late_and_an_immediate_install_each_advance_once` — immediate `committed` exit 0 and a delayed one; inside an `OnboardingViewModel([edition, install, harnesses, done])` both end on `Harnesses` with one `AdvanceRequested`.
  - `Locked_lifecycle_busy_offers_neither_retry_nor_back` — journal `setup` entry present, fake prints `failed{lifecycle_busy}` → `TryAgainVisible` false, `BackVisible` false.
  - `Permanent_refusal_while_locked_shows_the_cli_message` — `failed{not_our_server,"..."}` with journal → message shown verbatim, no retry, no back.
  - `Unlocked_transient_failure_offers_retry_and_back` — `failed{download_failed}`, no journal, no local profile.
  - `Profile_override_refuses_before_starting` — `appProfile "work"`, `configActiveProfile "personal"` → no spawn, message names both.
  - `No_cli_settles_retryable` — fake returns exit -1 with no lines → `Failed(Retry)`, message "kcap CLI not found".
  - Funnel: started on spawn, committed(`fresh`) on Installed, failed(`lifecycle_busy`, `true`), retried on Try again.
- [ ] **Step 2:** Run filtered suites. Expected: FAIL.
- [ ] **Step 3:** Implement controller, step, view (four rows, download bar, Cancel link, failure panel; Kcap theme classes, Lucide icons through `AppIcon`).
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the wizard's Install step over one install controller"`

### Task 6: Wizard shell navigation

**Files:**
- Modify: `src/Capacitor.App/ViewModels/Onboarding/OnboardingViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/OnboardingViewModelTests.cs`

**Interfaces:**
- Produces: an `AdvanceRequested` raised while `Navigating` is queued and run once after the in-flight navigation releases the gate (one pending flag, not a counter). Back moves to the nearest earlier step that is not `PassThrough`; when none exists Back is a no-op and `BackVisible` is false. Constructor gains `IObservable<bool>? canFinishLater = null`, ANDed into `FinishLaterCommand`'s can-execute.

- [ ] **Step 1: Write the failing tests**
  - `An_advance_raised_during_entry_is_not_lost` — a fake step whose `OnEnterAsync` raises `AdvanceRequested` before returning; after `PendingEnterForTesting` the wizard is on the next step, exactly once.
  - `Back_skips_pass_through_steps` — `[A, B(PassThrough), C]` on C, Back → A.
  - `Back_is_hidden_when_every_earlier_step_passes_through` — `[A(PassThrough), C]` on C → `BackVisible` false.
  - `Finish_later_follows_its_gate` — `canFinishLater` false → command cannot execute.
- [ ] **Step 2:** Run filtered. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Re-run, plus the whole `OnboardingViewModelTests` and `EditionStepViewModelTests`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Keep a wizard advance requested mid-navigation and skip passed steps on Back"`

### Task 7: Edition, composition and the local path

**Files:**
- Modify: `src/Capacitor.App/ViewModels/Onboarding/EditionStepViewModel.cs`, `src/Capacitor.App/Views/Onboarding/OnboardingPaneView.axaml`, `src/Capacitor.App/Services/Onboarding/WizardComposition.cs` (`WizardGraphOptions`, `WizardGraph`, `BuildGraph`), `src/Capacitor.App/ViewModels/Onboarding/SignInStepViewModel.cs` (`PassThrough`)
- Test: `test/Capacitor.App.Tests.Unit/EditionStepViewModelTests.cs`, `WizardCompositionTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 5, 6.
- Produces: `EditionStepViewModel(SetupFunnel funnel, Func<string?> localUnavailableReason)` — `LocalUnavailableReason` text (null = available); `ChooseLocalCommand` enabled only when null; choosing Local raises `AdvanceRequested`; `CanLeaveAsync(Next)` true for any choice; `PassThrough => committed local or pending local setup`. `LocalUnavailable`/`LocalPending` removed. Reasons: non-macOS "The local edition isn't available on this platform yet."; foreign profile "This Mac is set up for a cloud workspace. Moving it to local comes in a later release."
- Produces: `WizardGraphOptions` gains `LocalRoot LocalRoot`, `bool IsMacOs`; `WizardGraph` gains `LocalInstallController Install`. `BuildGraph` defines `bool OnLocalPath() => edition.Choice == Local || LocalSetupState.Pending(root) || committedLocal()` where `committedLocal` classifies the resolved identity's server with `LocalEndpoints.Read(root)` as `Local`; Sign-in `PassThrough` when `OnLocalPath()`; daemon identity `() => (signIn.Satisfied || install.Outcome is Installed || committedLocal()) ? ResolveIdentity() : null`; `EnsureAsync` triggered by Sign-in satisfied **or** `install.Settled` with Installed **or** at composition when `committedLocal()`; `startAt` = `Install` when pending, `Harnesses` when `AlreadyAuthenticated` (cloud or committed local); `canFinishLater` = not on local path or Installed. Step order `[edition, install, signIn, harnesses, history, done]`.

- [ ] **Step 1: Write the failing tests**
  - Edition: `Local_advances_to_install` (replaces `Local_stops_at_its_hand_off…`); `Local_is_unavailable_off_macos`; `An_inactive_cloud_profile_makes_local_unavailable`.
  - Composition: `Local_path_skips_sign_in_and_enables_the_daemon_only_after_install`; `Forced_setup_on_a_committed_local_machine_starts_at_harnesses_with_no_route_to_cloud` (Back from Harnesses is a no-op); `Pending_setup_starts_at_install`; `Cloud_forced_setup_is_unchanged` (existing assertions still hold); `Finish_later_is_disabled_until_installed`.
- [ ] **Step 2:** Run filtered. Expected: FAIL.
- [ ] **Step 3:** Implement; update the pane's Local card bindings and copy (detail line mentions one person and the approximate download size).
- [ ] **Step 4:** Re-run both classes and `WizardSimpleStepsTests`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Route the wizard's Local choice through Install"`

### Task 8: Agents and the closing frame on the local path

**Files:**
- Modify: `src/Capacitor.App/ViewModels/Onboarding/HarnessesStepViewModel.cs` (+ its view), `DoneFacts.cs`, `DoneStepViewModel.cs` (+ its view), `WizardComposition.cs` (pass the predicates)
- Test: `test/Capacitor.App.Tests.Unit/HarnessesStepViewModelTests.cs`, `WizardSimpleStepsTests.cs` (Done cases live there)

**Interfaces:**
- Produces: `HarnessesStepViewModel` gains `Func<bool>? onLocalPath = null`; when true, `Visibility` is preset to `"private"` on entry, `VisibilityVisible` false, and persistence writes `private`.
- Produces: `DoneFacts` gains `bool Local`, `string? DashboardUrl`; `DoneStepViewModel` on `Local`: `DaemonLine`/`Lede`/`FooterLine` without any `kcap` command text, `OpenDashboardCommand` and `DashboardVisible` (`DashboardUrl` non-empty) opening through `IUrlOpener`; `NextLabel` "Continue". `DashboardUrl` comes from `LocalEndpoints.Read(root).Active?.ServerUrl`.

- [ ] **Step 1: Write the failing tests**
  - `Local_agents_continue_skip_and_none_detected_all_pass_with_private` — three cases, each persists `default_visibility = private`.
  - `Local_done_names_no_terminal_command` — no `Lede`/`FooterLine`/`DaemonLine`/`FailedBody` contains "kcap ".
  - `Local_done_opens_the_dashboard_only_on_request` — opener called once after the command, never on entry.
  - `Local_done_hides_the_dashboard_when_its_url_is_unknown`.
- [ ] **Step 2:** Run filtered. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Word the Agents and closing pages for a local install"`

### Task 9: Startup handoff and shutdown settle the install

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (`RunWizardModeAsync`, the startup block around the gate, `HandoffAfterWizardAsync`, `QuiesceAppAsync`, shutdown fields)
- Test: `test/Capacitor.App.Tests.Unit/WizardStartupTests.cs`

**Interfaces:**
- Consumes: `WizardGraph.Install`, `GateReason.LocalSetupPending`.
- Produces: `_localInstall` field set from the graph; `HandoffAfterWizardAsync` awaits `install.SettleAsync()` before the lane quiesce; `QuiesceAppAsync` gains a `LocalInstallController?` parameter settled before the lane (Quit path); startup loops: while `!_shutdown.IsCancellationRequested` and the re-evaluated gate is `Incomplete(LocalSetupPending)`, run wizard mode again (`startAt: Install`) instead of `BuildDaemonGraph`.

- [ ] **Step 1: Write the failing tests** (existing harness in `WizardStartupTests`)
  - `A_close_with_pending_local_setup_reopens_the_wizard_and_builds_no_graph` — graph-construction count 0, a second wizard shown on Install.
  - `Quit_during_install_settles_the_child_and_builds_no_graph` — one interrupt observed, count 0.
  - `A_close_after_installed_builds_exactly_one_graph`.
  - `Cancel_then_quit_sends_one_interrupt`.
- [ ] **Step 2:** Run filtered. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Re-run `WizardStartupTests` and `AppStartupTests`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Settle the local install before the wizard hands over or the app quits"`

### Task 10: Main window Start for a stopped local server

**Files:**
- Create: `src/Capacitor.App/ViewModels/LocalServerRecoveryViewModel.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (`requestSignIn` skips `Local`; wire the recovery VM), the launcher banner host (`HomeViewModel` / its view, beside `AttachDaemonRecovery`)
- Test: `test/Capacitor.App.Tests.Unit/LocalServerRecoveryViewModelTests.cs`

**Interfaces:**
- Consumes: `IKcapCli.LocalStartAsync`, `IServerLane.Status`, `IServerLane.RestartAsync()`.
- Produces: `sealed class LocalServerRecoveryViewModel(IObservable<ServerLaneStatus> status, bool isLocalServer, Func<CancellationToken, Task<ProcessResult>> start, Func<Task> reconnect, CancellationToken shutdown)` with `bool Visible` (`isLocalServer && state is Retrying`), `ReactiveCommand StartCommand` (one in flight), `string? Message` (CLI stdout+stderr tail on non-zero exit or timeout). On exit 0 calls `reconnect()`. `isLocalServer` = resolved server classifies `LocalEndpointClass.Local`.

- [ ] **Step 1: Write the failing tests**
  - `A_retrying_local_server_offers_start_whatever_the_daemon_says`.
  - `A_retrying_cloud_server_never_offers_start`.
  - `A_start_longer_than_a_minute_succeeds_and_reconnects` — fake start completes after a `FakeTimeProvider` advance of 90 s; reconnect called once.
  - `A_failed_or_contended_start_shows_the_cli_output_and_can_be_pressed_again`.
  - `A_second_press_while_starting_does_nothing` — fake start called once.
  - `Sign_in_is_never_requested_for_a_local_server` (in `MainWindowViewModelTests` or the `requestSignIn` seam).
- [ ] **Step 2:** Run filtered. Expected: FAIL.
- [ ] **Step 3:** Implement and place the banner (Kcap theme: `KcapWarning*` for the stopped state, `kcapChip` Start button).
- [ ] **Step 4:** Re-run plus `MainWindowViewModelTests`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Offer Start when the local server is not running"`

### Task 11: Whole-suite verification and the server follow-up

- [ ] **Step 1:** `dotnet test --solution Capacitor.slnx --max-parallel-test-modules 3 -- --maximum-parallel-tests 4`. Expected: green (rerun known environmental daemon timing tests alone if they fail).
- [ ] **Step 2:** AOT check from Global Constraints. Expected: no output.
- [ ] **Step 3:** Manual run, only with the user's go-ahead and on a Mac or macOS user account with no kcap setup (the local root `~/.config/kcap/local` is fixed and ignores `KCAP_CONFIG_DIR`, so a developer machine's own setup would be touched): `dotnet run --project src/Capacitor.App -- --setup`, choose Local, observe Install → Agents → History → closing frame → main window; stop the server with `kcap local stop` and press Start.
- [ ] **Step 4:** File the kcap-server issue for the dashboard's import prompt (spec "Server follow-up"), with the user's go-ahead.
