# macOS App Uninstall Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An "Uninstall kcap…" macOS app-menu item that removes every kcap trace and trashes the app, on top of a `kcap uninstall` that also removes the local server and the PATH link.

**Architecture:** `kcap uninstall` gains a busy refusal (`--force`), the local-server removal (reusing `LocalUninstall` under the lifecycle lease), a consent rule for local data (`--purge-local`), and removal of `kcap` links that resolve to the running binary (skipped by the hidden `--keep-path-links`). The app confirms in its normal run, then relaunches itself as `--uninstall`; that copy builds nothing of the normal app, runs the CLI streamed, removes its own links (osascript admin fallback) and trashes its bundle.

**Tech Stack:** .NET 10 NativeAOT, Avalonia 12, ReactiveUI, TUnit.

**Spec:** `docs/superpowers/specs/2026-10-21-macos-app-uninstall-design.md`

## Global Constraints

- Issue: #1786 (commit subjects end ` (#1786)`, ≤ 80 chars). Linux is #1782 — no Linux/Windows UI here.
- Comments follow CLAUDE.md: scarce, no history, no spec coordinates.
- `--keep-path-links` is hidden: not in `help-uninstall.txt` or README. `--force` and `--purge-local` are public and documented.
- `Environment.GetFolderPath` is banned; take a `UserHome`.
- Tests: `TempDir` from Helpers (`tmp.CreateDir`, `tmp.PathTo`, `tmp.GetResolvedPath` for anything that resolves symlinks), console via `ConsoleOutput.StartCapture`, bare `[NotInParallel]` for console users.
- After the CLI tasks: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing.
- Desktop UI: `kcapDangerFill` for the destructive accept, `KcapWarning*` only for the data-loss warning line.

## Review Focus

1. A local server whose registry is unreadable — uninstall must fail and keep `~/.config/kcap`, never delete the root under it (an `Interrupted` one is finished by the lifecycle lease's existing recovery). Test in Task 3.
2. A `kcap` link pointing at another install (npm's `bin/kcap.js`, a Homebrew keg) — must survive both CLI and app link removal. Tests in Tasks 4 and 9.
3. `kcap uninstall --yes` in a script on a machine with a local server — refuses, changes nothing. Test in Task 3.
4. The uninstall copy writing into `~/.config/kcap` after the CLI deleted it (app state, crash log, wizard) — it must not. Test in Task 10.
5. CLI fails after removing hooks — the app must not trash itself and Retry must re-run. Test in Task 9.

---

### Task 1: One agent-ending path for every service removal

**Files:**
- Create: `src/Capacitor.Cli/Services/ServiceRemoval.cs`
- Modify: `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs:516-595`, `src/Capacitor.Cli/Commands/OutgoingRecorders.cs:34-50`

**Interfaces:**
- Produces: `static class ServiceRemoval { static Task<bool> EndAgentsAsync(DaemonStore store, string serviceId, TimeProvider time); }` — the body of today's `DaemonServiceCommands.EndAgentsBeforeRemovalAsync`, moved.

- [ ] **Step 1:** Move `EndAgentsBeforeRemovalAsync` into `ServiceRemoval.EndAgentsAsync`; both `DaemonServiceCommands` call sites use it.
- [ ] **Step 2:** In `OutgoingRecorders.StopAsync`, call `ServiceRemoval.EndAgentsAsync` before each `services.Uninstall(id, …)`, and `DaemonCommands.MarkLedgerInterrupted(store, id, time)` after a successful uninstall when it returned false — the same sequence as `Uninstall()`.
- [ ] **Step 3:** Run `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonCommandsServiceUninstallUnderTests/*"` and the `LocalUninstallCommandTests` / `UninstallCommandTests` classes. Expected: all pass (mechanical move).
- [ ] **Step 4:** Commit `End hosted agents before every daemon service removal (#1786)`.

### Task 2: Busy refusal and `--force` in `kcap uninstall`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/UninstallCommand.cs`, `src/Capacitor.Cli/Commands/CommandServices.cs:158` (DI unchanged if ctor params are registered types)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/UninstallCommandTests.cs`

**Interfaces:**
- Produces: ctor `UninstallCommand(DaemonStore, ConfigRoot, ProfileContext, UserHome, HarnessRegistry, BinaryProbe, AgentsPaths, WatcherManager, WorkingDirectory, TimeProvider, IOutgoingRecorders recorders, ILocalStackFactory stacks, KcapLinkRemover links)` — `links` arrives in Task 4; add `recorders` and `stacks` now. The inline `new OutgoingRecorders(…)` is replaced by `recorders`.

- [ ] **Step 1:** Add a test helper `UninstallCommand NewCommand(Fixture f, IOutgoingRecorders? recorders = null, ILocalStackFactory? stacks = null)` defaulting to the real `OutgoingRecorders` and a stack factory over an empty local root under the fixture home; convert every existing `new UninstallCommand(` call to it.
- [ ] **Step 2:** Write failing tests with `FakeOutgoingRecorders`:
  - `Busy_daemon_refuses_and_changes_nothing`: `Busy = ["daemon 'x' is running or starting agents"]`, `--yes` → exit 1, stderr contains `--force`, log has no `recorders:stop`, config dir still exists.
  - `Force_proceeds_past_a_busy_daemon`: same with `--yes --force` → exit 0, log contains `recorders:stop`.
- [ ] **Step 3:** Run them; expected FAIL.
- [ ] **Step 4:** Implement: parse `--force`; after the `--project` check and before printing the plan, `recorders.BusyAsync()`; non-empty and no `--force` → print the lines plus "Stop them, or pass --force to end them. Nothing was changed." to stderr, return 1.
- [ ] **Step 5:** Run the class; expected PASS. Commit `Refuse kcap uninstall while a daemon runs agents (#1786)`.

### Task 3: Remove the local server in `kcap uninstall`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/UninstallCommand.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/UninstallCommandTests.cs` (reuse the installed-stack setup from `LocalUninstallCommandTests` / `FakeLocalServerService`)

**Interfaces:**
- Consumes: `ILocalStackFactory.Create()`, `LocalStack.Read()`, `LocalStack.RunAsync(progress, (lease, state, ct) => …, ct, force)`, `LocalUninstall.RunAsync(lease, state, purge, force, configDir, ct)`.

- [ ] **Step 1:** Write failing tests:
  - `Yes_alone_refuses_when_a_local_server_is_installed`: installed stack, `--yes` → exit 1, stderr names `--purge-local`, stack still installed, config dir exists.
  - `Purge_local_purges_before_the_config_directory_goes`: `--yes --purge-local` → exit 0; the service log shows the purge; config dir gone.
  - `Keep_config_keeps_the_local_data`: `--yes --keep-config` → exit 0; unit removed, `data/` still present.
  - `Interactive_prompt_names_the_deletion`: no `--yes`, stdin `y` → stdout contains the server URL and "every session it recorded"; purged.
  - `Unreadable_local_registry_keeps_the_config_directory`: corrupt `install.json` → exit 1, config dir exists.
  - `No_local_server_needs_no_purge_flag`: `--yes` with an empty root → exit 0 (existing behaviour).
- [ ] **Step 2:** Run them; expected FAIL.
- [ ] **Step 3:** Implement in `HandleAsync`: create the stack (a `LocalInstallFailure` is a failure line, not a crash) and `Read()` it before the plan is printed. When `Installed`: with `--yes` and neither `--purge-local` nor `--keep-config` → stderr "A local server is installed at {url}; kcap uninstall would delete every session it recorded. Pass --purge-local to delete it, or --keep-config to keep its data." exit 1. Otherwise add plan lines (purge: "Delete the local server at {url} and every session it recorded"; keep-config: "Remove the local server at {url}, keeping its data"). After the prompt and the busy check, as the **first** mutating step, run `LocalUninstall` with `purge: !keepConfig`, `force` as given; a `LocalLifecycleException`/`LocalInstallFailure`/`LocalUpgradeFailure` prints and sets `hadFailures`. `Unreadable` sets `hadFailures` before anything else runs. `Interrupted` is left to `stack.RunAsync`'s recovery.
- [ ] **Step 4:** Run the class; expected PASS. Commit `Remove the local server in kcap uninstall (#1786)`.

### Task 4: Remove the `kcap` links that point at this install

**Files:**
- Create: `src/Capacitor.Cli.Core/Setup/KcapLinkFolders.cs`, `src/Capacitor.Cli.Core/Setup/KcapLinkRemover.cs`
- Modify: `src/Capacitor.Cli/Commands/UninstallCommand.cs`, `src/Capacitor.Cli/Commands/CommandServices.cs`, `src/Capacitor.App/Services/ApplicationReferences.cs:81`, `src/Capacitor.Cli.Core/Setup/PathShimInstaller.cs:110-116`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Setup/KcapLinkRemoverTests.cs`, `UninstallCommandTests.cs`

**Interfaces:**
- Produces:
  - `static class KcapLinkFolders { static IReadOnlyList<string> For(UserHome home); }` → `/usr/local/bin`, `/opt/homebrew/bin`, `~/.local/bin`, `~/bin` (this order).
  - `sealed class KcapLinkRemover(IReadOnlyList<string> folders, string target) { IReadOnlyList<string> Find(); IReadOnlyList<KcapLinkFailure> Remove(); }` and `record KcapLinkFailure(string Path, string Reason)`. A link qualifies when `folder/kcap` is a symlink whose fully resolved target (`ResolveLinkTarget(true)`) equals the fully resolved `target`. Regular files and links elsewhere never qualify. `Remove` re-reads the link before deleting, and turns `UnauthorizedAccessException`/`IOException` into a failure.
- DI: `KcapLinkRemover` registered with `KcapLinkFolders.For(home)` and `Environment.ProcessPath`.

- [ ] **Step 1:** Failing tests in `KcapLinkRemoverTests` (all paths via `tmp.GetResolvedPath`): `Removes_a_link_to_the_target`, `Removes_a_link_through_a_chain_to_the_target` (link → `current` link → binary), `Keeps_a_link_to_another_binary`, `Keeps_a_regular_file_named_kcap`, `Reports_a_link_it_cannot_remove` (folder mode `0555`; skip on Windows).
- [ ] **Step 2:** Run; FAIL. Implement both types; run; PASS.
- [ ] **Step 3:** Failing `UninstallCommandTests`: `Uninstall_removes_its_own_link` and `Keep_path_links_leaves_it` (helper passes a remover over a temp folder). A failure prints `sudo rm '<path>'` to stderr and sets `hadFailures`.
- [ ] **Step 4:** Implement in `UninstallCommand` (non-Windows only, after the plugin removals, before the config delete; plan line "Remove the kcap command link at {path}" for each `Find()` hit unless `--keep-path-links`). Point `ApplicationReferences` and `PathShimInstaller.StableFolders` at `KcapLinkFolders.For`. Run CLI, Core and `ApplicationReferencesTests`; PASS.
- [ ] **Step 5:** Commit `Remove the kcap link to this install on uninstall (#1786)`.

### Task 5: Document the new uninstall behaviour

**Files:** `src/Capacitor.Cli.Core/Resources/help-uninstall.txt`, `README.md` (`### Uninstalling`, and the `kcap uninstall` row if its summary changes)

- [ ] **Step 1:** Add `--force` and `--purge-local` to the help text and README examples. Add to the paragraph: the busy refusal, the local server removal (purge, or kept with `--keep-config`), the refusal of `--yes` alone when a local server is installed, and link removal (with the `sudo rm` fallback). Leave `--keep-path-links` out.
- [ ] **Step 2:** Run the publish AOT check from Global Constraints; it prints nothing. Commit `Document local server and link removal in kcap uninstall (#1786)`.

### Task 6: Find other kcap installs

**Files:**
- Create: `src/Capacitor.App/Platform/Mac/OtherKcapInstalls.cs`
- Test: `test/Capacitor.App.Tests.Unit/OtherKcapInstallsTests.cs`

**Interfaces:**
- Produces: `static class OtherKcapInstalls { static IReadOnlyList<OtherKcapInstall> Find(string? terminalPath, string bundleRoot); }`, `record OtherKcapInstall(string Path, string? RemoveCommand)`. Every executable `kcap` on `terminalPath`, deduplicated by fully resolved target, excluding targets under `bundleRoot`. `RemoveCommand` is `npm uninstall -g @kurrent/kcap` when the resolved path contains `/node_modules/@kurrent/`, else null.

- [ ] **Step 1:** Failing tests: `Ignores_links_into_the_bundle`, `Reports_an_npm_install_with_its_command`, `Reports_an_unknown_install_without_a_command`, `Reports_one_install_reached_twice`, `Null_path_finds_nothing`.
- [ ] **Step 2:** Run; FAIL. Implement; run; PASS. Commit `Find kcap installs outside the app bundle (#1786)`.

### Task 7: The uninstall confirmation

**Files:**
- Create: `src/Capacitor.App/Services/UninstallConfirmation.cs`
- Modify: `src/Capacitor.App/Services/ILifecycleSurface.cs` (`KindUninstall = "uninstall"`, optional `string? Warning = null` as the last positional), `src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs`, `src/Capacitor.App/Views/LifecyclePromptWindow.axaml`, `src/Capacitor.App/Services/DaemonLifecycleController.cs`
- Test: `test/Capacitor.App.Tests.Unit/UninstallConfirmationTests.cs`, `LifecyclePromptViewModelTests.cs`

**Interfaces:**
- Produces:
  - `DaemonLifecycleController.LatestActivity : DaemonActivity?` with `record DaemonActivity(int ActiveAgents, bool Busy)`; null before the first snapshot.
  - `static class UninstallConfirmation { static UninstallRequest Build(UninstallFacts facts); }`, `record UninstallFacts(string? LocalServerUrl, DaemonActivity? Activity, bool DaemonRunning, string? HostedServerUrl, IReadOnlyList<OtherKcapInstall> OtherInstalls)`, `record UninstallRequest(LifecyclePrompt Prompt, IReadOnlyList<string> RelaunchArgs)`. `RelaunchArgs` is `["--uninstall"]` plus `"--purge-local"` iff `LocalServerUrl` is set.
- The view model: `KindUninstall` → title "Uninstall kcap", accept "Uninstall", `AcceptIsDestructive` true; `Warning` bound in `KcapWarningBrush`, collapsed when null.

- [ ] **Step 1:** Failing tests on `Build`: `Lists_what_is_removed` (hooks, services, settings and sign-in, the app); `Local_server_sets_the_warning_and_the_purge_arg` (Warning contains the URL and "permanently deleted"); `No_local_server_has_no_warning`; `Busy_daemon_names_the_agent_count` ("2 running agents will be ended"); `Daemon_running_without_a_snapshot_says_its_agents_will_be_ended`; `Hosted_server_says_sent_sessions_stay`; `Other_install_is_named_with_its_command`. Plus `LifecyclePromptViewModelTests.Uninstall_is_destructive`.
- [ ] **Step 2:** Run; FAIL. Implement; run; PASS. Commit `Build the uninstall confirmation (#1786)`.

### Task 8: The menu item and the relaunch

**Files:**
- Modify: `src/Capacitor.App/Views/AppMenu.cs`, `src/Capacitor.App/App.axaml.cs` (beside `RestartForSetupAsync`, `:868`, `:885-905`)
- Test: `test/Capacitor.App.Tests.Unit/AppMenuBarTests.cs` or a new `AppMenuTests.cs`

**Interfaces:**
- Produces: `AppMenu.SetUninstallAction(Action? uninstall)`; item "Uninstall kcap…" after a separator below "Set up kcap…", added only when `OperatingSystem.IsMacOS()`, disabled until set.
- Consumes: `UninstallConfirmation.Build`, `OtherKcapInstalls.Find`, `ShowLifecyclePromptDialogAsync`, `Program.Host.NewCopy(params string[])`.

- [ ] **Step 1:** Failing test `Uninstall_item_is_disabled_until_an_action_is_set` (macOS only via `[RunOn(OS.MacOS)]`).
- [ ] **Step 2:** Implement `SetUninstallAction`; run; PASS.
- [ ] **Step 3:** Add `UninstallAsync(desktop)` modelled on `RestartForSetupAsync`: gather `UninstallFacts` (`LocalEndpoints.Read(_localRoot).Active?.ServerUrl`, `_lifecycle?.LatestActivity`, a live pid in `_daemonStore`, the profile's non-local server URL, `OtherKcapInstalls.Find(await probe.TerminalPathAsync(ct), InstallLocation.BundleRoot(Environment.ProcessPath))`), show the prompt, and on accept register `desktop.Exit += … Process.Start(Program.Host.NewCopy([.. request.RelaunchArgs]))` and `desktop.TryShutdown()`. Guard with the same `_shutdownStarted` / re-entry flag pattern. Wire `_appMenu.SetUninstallAction` next to `SetSetupAction` when the bundle root is non-null.
- [ ] **Step 4:** Build the app; commit `Add Uninstall kcap to the macOS app menu (#1786)`.

### Task 9: The uninstall run

**Files:**
- Create: `src/Capacitor.App/ViewModels/UninstallRunViewModel.cs`, `src/Capacitor.App/Views/UninstallWindow.axaml(.cs)`, `src/Capacitor.App/Platform/Mac/MacAppRemoval.cs`
- Modify: `src/Capacitor.App/Services/KcapCli.cs` (`IKcapCli.UninstallAsync`)
- Test: `test/Capacitor.App.Tests.Unit/UninstallRunViewModelTests.cs`, `MacAppRemovalTests.cs`

**Interfaces:**
- Produces:
  - `Task<StreamingResult> IKcapCli.UninstallAsync(bool purgeLocal, Action<StreamedLine> onLine, CancellationToken ct)` → `uninstall --yes --force --keep-path-links [--purge-local]`, no internal timeout, spawn env as `SetupLocalAsync`.
  - `interface IAppRemoval { Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct); Task<bool> TrashBundleAsync(CancellationToken ct); void RevealBundle(); }` — `RemoveLinksAsync` returns the paths it could not remove.
  - `MacAppRemoval(IProcessRunner runner, UserHome home, string bundleRoot) : IAppRemoval`: links = `KcapLinkRemover(KcapLinkFolders.For(home), <bundle>/Contents/MacOS/kcap)`; a failure retries through `osascript … with administrator privileges` running `test -L p && [ "$(readlink p)" = expected ] && rm p` (same guard as `ApplicationReferences.ReplaceLinkAsync`). Trash = `NSFileManager.defaultManager trashItemAtURL:resultingItemURL:error:` via the objc interop style of `AppKitMenus.cs`. Reveal = `open -R <bundle>`.
  - `UninstallRunViewModel(IKcapCli cli, IAppRemoval removal, bool purgeLocal, Action quit)` with `State` ∈ `Running | Failed | Finished | TrashFailed`, `Lines` (streamed output), `RetryCommand`, `CloseCommand` (→ `quit`).
  - `IAppRemoval.DeleteSupportFiles()` removes, when present, `~/Library/Caches/<bundle id>`, `~/Library/HTTPStorages/<bundle id>`, `~/Library/Saved Application State/<bundle id>.savedState` and `~/Library/Preferences/<bundle id>.plist`, with the bundle id read from `Contents/Info.plist` (`CFBundleIdentifier`). The app's own state and crash log live in `~/.config/kcap`, which the CLI already deleted. Failures here are logged, never a `Failed` state.
- Order on success: CLI exit 0 → `RemoveLinksAsync` → `DeleteSupportFiles` (non-empty → `Failed`, lines name each path) → `TrashBundleAsync` (false → `RevealBundle()`, `TrashFailed` with "drag kcap to the Trash to finish") → `quit`.

- [ ] **Step 1:** Failing VM tests with fakes: `Cli_failure_never_touches_the_bundle` (exit 1 → `Failed`, no removal calls); `Retry_runs_the_cli_again`; `Leftover_link_is_a_failure_and_keeps_the_bundle`; `Success_removes_links_then_trashes_then_quits` (call order); `Trash_failure_reveals_and_waits_for_close`; `Passes_purge_local_through`.
- [ ] **Step 2:** Failing `MacAppRemovalTests` (macOS only): `Removes_only_links_into_the_bundle` over temp folders (inject folders through an internal ctor overload).
- [ ] **Step 3:** Run; FAIL. Implement; run; PASS.
- [ ] **Step 4:** `UninstallWindow`: title "Uninstalling kcap", a read-only line list, Retry (`kcapChip`) and Close; no window chrome beyond the existing dialog style. Commit `Run the uninstall in its own window (#1786)`.

### Task 10: Start in uninstall mode

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (`StartAsync`, `:298-300`; `OnFrameworkInitializationCompleted` before the tray/menu-bar install)
- Test: `test/Capacitor.App.Tests.Unit/UninstallModeTests.cs`

**Interfaces:**
- Produces: `internal static UninstallMode? UninstallMode.Parse(string[]? args)` → `new(PurgeLocal)` when `--uninstall` is present, else null.

- [ ] **Step 1:** Failing tests: `Parse_reads_the_flags`; `Uninstall_mode_writes_nothing_under_the_config_root` — drive the uninstall-mode startup path (the method that builds the window's VM from args) against a temp config root with fakes, then assert the temp root holds no new files.
- [ ] **Step 2:** Implement: when `UninstallMode.Parse(desktop.Args)` is non-null, `StartAsync` shows `UninstallWindow` over a `KcapCli` for the bundled CLI and a `MacAppRemoval`, and returns before `RunInstallLocationGuard`, the updater, the lane and the wizard. Skip the tray and menu-bar install in that mode. Closing the window shuts down with exit 0.
- [ ] **Step 3:** Run the App test suite; PASS. Then by hand on a Mac with a throwaway local server: confirm → app relaunches → uninstall completes → `~/Library/LaunchAgents/io.kurrent.kcap.*` gone, `/usr/local/bin/kcap` gone, `~/.config/kcap` gone, app in the Trash.
- [ ] **Step 4:** Commit `Start the app in uninstall mode (#1786)`.
