# Docs screenshots harness (kcap-cli half) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An env-gated TUnit class that renders seven desktop-app screens headlessly from the real views over the test fakes, with invented Northwind data, and writes them as 2× PNGs for the kcap docs

**Architecture:** `test/Capacitor.App.Tests.Unit/DocsShots.cs` holds one `[Test]` per figure. Each composes a `MainWindow` (or `SettingsWindow`) the way the smoke tests already do, shows it in the shared headless Skia session, calls `SetRenderScaling(2)`, settles the dispatcher, and saves `window.CaptureRenderedFrame()` to `$KCAP_SHOTS_OUT/<name>-dark.png`. A scene builder per screen lives beside the tests in `DocsShotScenes.cs`. Nothing in the session, the app or the fakes changes except making two private helpers reachable

**Tech Stack:** .NET 10, TUnit on Microsoft Testing Platform, Avalonia 12 Headless with Skia (`UseHeadlessDrawing = false`, already on in `AvaloniaSession`), the existing `Fake*` services and `WorkspaceFixtures`

**Spec:** kcap-web `docs/superpowers/specs/2026-10-26-docs-screenshots-design.md`, section 3; the consuming side is kcap-web `docs/superpowers/plans/2026-10-26-docs-screenshots-web.md`, Task 7

## Global Constraints

- The class runs only when `KCAP_SHOTS_OUT` names an existing directory; otherwise every test is skipped with `Skip.Unless(...)`, the pattern in `AcpHostedAgentRuntimeFactoryLiveTests`
- Every test carries `[NotInParallel("AvaloniaSession")]` (on the class)
- Output file names are exactly the kcap-web manifest names: `desktop-main`, `desktop-permission-card`, `desktop-help-menu`, `desktop-settings`, `desktop-wizard-edition`, `desktop-wizard-sign-in`, `desktop-local-recovery`, each suffixed `-dark.png`
- Render at 2× through `window.SetRenderScaling(2)` after `Show()`; the saved bitmap's `Dpi.X / 96` is 2
- Scene data is invented in the Northwind register (team Northwind, repos `northwind/storefront`, `northwind/payments-api`, people Maya Chen, Priya Nair, Diego Alvarez); no real path under a home directory, no real profile name, server URL `https://northwind.kcap.ai`
- `System.IO.Path.GetTempPath` is banned (`test/BannedSymbols.txt`); the output directory comes from the env var only
- Rendered text needs plain `AvaloniaSession.DispatchAsync`, not the immediate Rx scheduler, which shows stale bound text
- No Linear ids in `.cs` files; comments scarce

## Review Focus

- `KCAP_SHOTS_OUT` set to a path that does not exist: the gate must skip (or fail with the path in the message), never write into the working directory. Pinned in Task 1
- A scene whose bound text has not arrived when the frame is captured (an OAPH still empty): the capture helper pumps `RunJobs` and `ForceRenderTimerTick` until a sentinel `TextBlock` the scene names is non-empty, with a bounded wait that fails naming the sentinel. Pinned in Task 1
- A renamed scene: an ungated test pins the seven output names so a rename breaks CI, not a later docs run. Pinned in Task 1
- The help flyout rendering outside the window's bounds in a headless render: the `HelpMenu` scene asserts the flyout presenter's bounds lie inside the window before saving. Pinned in Task 3
- A window left open leaking into the next scene: every scene closes its window in `finally` and pumps `RunJobs`, as `WorkspaceSwitchCoverTests` does. Pinned by the shared capture helper in Task 1

---

### Task 1: The gate, the capture helper and the name pin

**Files:**
- Create: `test/Capacitor.App.Tests.Unit/DocsShots.cs`
- Create: `test/Capacitor.App.Tests.Unit/DocsShotScenes.cs` (empty scene registry for now)

**Interfaces:**
- Produces:
  - `static class DocsShotScenes` with `public const string OutEnvVar = "KCAP_SHOTS_OUT";` and `public static readonly string[] Names = ["desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings", "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-local-recovery"];`
  - `static Task DocsShots.CaptureAsync(string name, Func<Window> build, string sentinelControlName)` — runs on the UI thread via `AvaloniaSession.DispatchAsync`: `build()`, `window.Show()`, `window.SetRenderScaling(2)`, pump until the named control is a `TextBlock` with non-empty `Text` (or any control that `IsVisible`, when it is not a `TextBlock`), bounded to 200 pumps of `Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout();`, then `window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{name}-dark.png"))`; closes the window in `finally`
  - `static string DocsShots.OutDir` — the env var's value, validated by the gate

- [ ] **Step 1: Write the ungated name pin and the gate test**

```csharp
[NotInParallel("AvaloniaSession")]
public sealed class DocsShots {
    [Test]
    public async Task The_scene_names_match_the_docs_manifest() {
        await Assert.That(DocsShotScenes.Names).IsEquivalentTo([
            "desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings",
            "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-local-recovery"]);
    }

    [Test]
    public async Task Every_scene_has_a_test() {
        var tests = typeof(DocsShots).GetMethods().Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => m.GetCustomAttribute<DocsShotAttribute>()?.Name).Where(n => n is not null);
        await Assert.That(tests).IsEquivalentTo(DocsShotScenes.Names);
    }
}
```

`DocsShotAttribute(string name)` is a tiny marker attribute in `DocsShots.cs` so each scene test declares the file it writes and the pin above can read it

- [ ] **Step 2: Run the two tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/*"`
Expected: FAIL to compile (types missing), then after a stub `Names = []`, `Every_scene_has_a_test` fails on the empty set

- [ ] **Step 3: Implement the gate and `CaptureAsync`**

```csharp
static string Gate() {
    var dir = Environment.GetEnvironmentVariable(DocsShotScenes.OutEnvVar);
    Skip.Unless(dir is not null && Directory.Exists(dir), $"Docs screenshot harness: set {DocsShotScenes.OutEnvVar} to an existing directory to render the figures");
    return dir!;
}
```

Each scene test calls `var outDir = Gate();` first, then `await CaptureAsync(outDir, name, build, sentinel)`. Fill `Names` with the seven names

- [ ] **Step 4: Add a throwaway scene** `desktop-main` that builds `new MainWindow { DataContext = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System) }` with sentinel `"SessionRail"` (replaced in Task 2), and run gated

Run: `mkdir -p /tmp/shots && KCAP_SHOTS_OUT=/tmp/shots dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/*"`
Expected: `desktop-main-dark.png` exists, 2400×1520; the ungated run (no env var) reports the scene tests as skipped and the two pins as passed

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.App.Tests.Unit/DocsShots.cs test/Capacitor.App.Tests.Unit/DocsShotScenes.cs
git commit -m "Add the env-gated docs screenshot harness"
```

---

### Task 2: Northwind fixtures and the Main scene

**Files:**
- Modify: `test/Capacitor.App.Tests.Unit/DocsShotScenes.cs`
- Modify: `test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs:52,514` (`NewWorkspace`, `RailWindow` from `static` private to `internal static`, or lift them into `DocsShotScenes` if they need different seed data; prefer lifting a copy with Northwind data and leaving the smoke test untouched)
- Modify: `test/Capacitor.App.Tests.Unit/DocsShots.cs`

**Interfaces:**
- Consumes: `FakeDaemonClientService`, `FakeDaemonClientService.Snap(...)`, `AgentDirectory`, `SessionRailViewModel`, `MainWindowViewModel(... rail:, workspaceFactory:, tenantName:)`, `WorkspaceViewModel`, `ChatTabViewSmokeTests`'s JSONL line constants (`UserLine`, `AssistantLinkLine`, `ToolCallLine`; make them `internal`)
- Produces:
  - `DocsShotScenes.Northwind(FakeDaemonClientService service)` — seeds the snapshot (`Snap(daemon: "maya-mbp", serverUrl: "https://northwind.kcap.ai", connection: "connected", active: 3, max: 5)`), status Connected, and three agents: `a1` "Add retry to the payments gateway" (claude, `/Users/maya/dev/northwind/payments-api`, `SessionId "s1"`, Running), `a2` "Fix the checkout total rounding" (codex, `/Users/maya/dev/northwind/storefront`, `SessionId "s2"`, Running, `AwaitingInput: true`), `a3` "Tidy the README" (claude, storefront, ended two hours ago)
  - `DocsShotScenes.PullRequests()` — a `BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>` with `s1` → one open PR "Retry transient gateway failures" and `s2` → one merged PR "Round totals at the line level"
  - `DocsShotScenes.MainWindow(IReadOnlySet<string>? pendingIds = null, Action<FeedbackCategory>? openFeedback = null, Action? requestSetup = null, bool openSession = true)` → `(MainWindowViewModel Vm, MainWindow Window, FakePermissionService Permissions)` — one `FakePermissionService` shared by every workspace the factory builds (passed where `WorkspaceSwitchCoverTests` passes `new FakePermissionService()`), the rail over the Northwind directory, the window with `tenantName: "northwind"`, and when `openSession` a call to `vm.OpenSession("a1")` so the chat tab shows a transcript written from the JSONL constants into a `TempDir` (a prompt about gateway retries, a reply, one Bash tool call)

- [ ] **Step 1: Write the `desktop-main` scene test**

```csharp
[Test, DocsShot("desktop-main")]
public async Task Main_window_with_rail_and_a_working_chat_tab() {
    var outDir = Gate();
    await CaptureAsync(outDir, "desktop-main", () => DocsShotScenes.MainWindow().Window, sentinelControlName: "ChatTranscript");
}
```

The sentinel is the chat transcript control's `x:Name` in `ChatTabView.axaml` (read it; if the transcript list has no name, give it one, that change is allowed)

- [ ] **Step 2: Implement `Northwind`, `PullRequests` and `MainWindow` in `DocsShotScenes.cs`**, mirroring `RailWindow` and `NewWorkspace` in `MainWindowSmokeTests.cs` but with the Northwind data above. The transcript file is written with `TempDir.WithPathTo("transcript.jsonl", out var path)` and attached by `service.Agents.AddOrUpdate(agent with { TranscriptPath = path })`, then awaited through the workspace's chat tab `PendingReadForTesting` as `ChatTabViewSmokeTests.Host.LoadAsync` does

- [ ] **Step 3: Render and inspect**

Run: `KCAP_SHOTS_OUT=/tmp/shots dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/Main_window_with_rail_and_a_working_chat_tab"` then open `/tmp/shots/desktop-main-dark.png`
Expected: the rail lists the three Northwind sessions with their status words and PR chips; the right pane shows the chat transcript with the prompt, the reply and the tool call; no `/Users/tony`, no "daemon-a", no "localhost"

- [ ] **Step 4: Run the whole App test suite once** to prove the lifted helpers broke nothing

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindowSmokeTests/*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.App.Tests.Unit
git commit -m "Render the desktop main window for the docs"
```

---

### Task 3: Permission card and Help menu scenes

**Files:**
- Modify: `test/Capacitor.App.Tests.Unit/DocsShots.cs`

**Interfaces:**
- Consumes: `DocsShotScenes.MainWindow(...)`, `FakePermissionService.Add`, `PermissionEntries.Entry(requestId, agentId, toolName:)`, `SessionRailView` → `RailHelpButton` and its `Flyout`

- [ ] **Step 1: Write the two scene tests**

```csharp
[Test, DocsShot("desktop-permission-card")]
public async Task A_permission_request_waits_in_the_tab() {
    var outDir = Gate();
    await CaptureAsync(outDir, "desktop-permission-card", () => {
        var scene = DocsShotScenes.MainWindow(pendingIds: new HashSet<string> { "a1" });
        scene.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Bash"));
        return scene.Window;
    }, sentinelControlName: "PermissionCard");
}

[Test, DocsShot("desktop-help-menu")]
public async Task The_help_and_support_flyout_is_open() {
    var outDir = Gate();
    await CaptureAsync(outDir, "desktop-help-menu", () => {
        var scene = DocsShotScenes.MainWindow(openFeedback: _ => { }, requestSetup: () => { }, openSession: false);
        var help = scene.Window.FindDescendantOfType<SessionRailView>()!.FindControl<Button>("RailHelpButton")!;
        ((Flyout)help.Flyout!).ShowAt(help);
        return scene.Window;
    }, sentinelControlName: "RailHelpDocsButton");
}
```

`PermissionCard` is the root `Border`'s `x:Name` in `PermissionCardTemplate` (`PendingCardTemplates.axaml`); name it if it has none. The rail's "Needs you" word comes from `pendingIds`

- [ ] **Step 2: Add the flyout-bounds assertion** inside the help scene before capture: the `FlyoutPresenter`'s bounds translated to the window lie within `window.Bounds`; otherwise fail naming the overflow

- [ ] **Step 3: Render and inspect both**

Run: `KCAP_SHOTS_OUT=/tmp/shots dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/*"`
Expected: the permission figure shows the Bash request card with Deny, Allow always and Allow, and the rail row for `a1` reads "Needs you"; the help figure shows the flyout with Open dashboard, Documentation, Report a bug…, Send feedback…, Set up kcap…

- [ ] **Step 4: Commit**

```bash
git add test/Capacitor.App.Tests.Unit
git commit -m "Render the permission card and help menu for the docs"
```

---

### Task 4: Settings scene

**Files:**
- Modify: `test/Capacitor.App.Tests.Unit/DocsShots.cs`
- Modify: `test/Capacitor.App.Tests.Unit/DocsShotScenes.cs`

**Interfaces:**
- Consumes: `SettingsWindowSmokeTests`'s recipe: `[TempConfigRoot] Config`, `ConfigMutator.Mutate`, `SettingsProfileStore(root, "northwind", "https://northwind.kcap.ai")`, `SettingsViewModel(...)` with the nine positional arguments from that test, daemon status with capability `["settings/1"]`
- Produces: `DocsShotScenes.Settings(TempConfigRoot config)` → `SettingsWindow`

- [ ] **Step 1: Write the scene test** with `[TempConfigRoot] public required TempConfigRoot Config { get; init; }` on the class, the profile named `northwind` with daemon name `maya-mbp` and `MaxAgents = 5`, and sentinel `"SettingsTabs"`

- [ ] **Step 2: Implement `DocsShotScenes.Settings`**, copying the construction from `SettingsWindowSmokeTests` and changing only the names and URL

- [ ] **Step 3: Render and inspect**

Run: `KCAP_SHOTS_OUT=/tmp/shots dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/Settings*"`
Expected: a 1200×1160 PNG (600×580 at 2×) with the profile, the daemon section and the notifications section; the profile reads `northwind`

- [ ] **Step 4: Commit**

```bash
git add test/Capacitor.App.Tests.Unit
git commit -m "Render the settings window for the docs"
```

---

### Task 5: Wizard scenes

**Files:**
- Modify: `test/Capacitor.App.Tests.Unit/DocsShots.cs`
- Modify: `test/Capacitor.App.Tests.Unit/DocsShotScenes.cs`
- Modify: `test/Capacitor.App.Tests.Unit/WizardStartupTests.cs:101` (`GraphHarness` is already `internal`; expose `Options()` if it is private)

**Interfaces:**
- Consumes: `OnboardingViewModel(steps, startAt:)`, `EditionStepViewModel(CliTelemetry.Disabled(TimeProvider.System).Funnel, () => null)`, `FakeWizardStep(WizardStepId, title)`, `WizardFixtures.GraphHarness(Config.Root)` + `WizardComposition.BuildGraph(harness.Options())`, `new MainWindow { Onboarding = vm, Width = 760, Height = 760 }`, `vm.PendingEnterForTesting`
- Produces: `DocsShotScenes.WizardEdition()` and `DocsShotScenes.WizardSignIn(TempConfigRoot config)` → `MainWindow`

- [ ] **Step 1: Write the two scene tests**: `desktop-wizard-edition` with sentinel the Edition step's title `TextBlock` (read `OnboardingPaneView.axaml` for its name; name it if needed), `desktop-wizard-sign-in` with sentinel the sign-in step's primary button

- [ ] **Step 2: Implement `WizardEdition`**: steps = a real `EditionStepViewModel` plus `FakeWizardStep` for `Install, SignIn, Harnesses, Import, Privacy, Done`; `startAt: WizardStepId.Edition`; await `PendingEnterForTesting` inside the build via `DispatchAsync<Task>` (CaptureAsync accepts a `Func<Task<Window>>` overload for this; add it)

- [ ] **Step 3: Implement `WizardSignIn`** from the full graph (`GraphHarness` + `BuildGraph`) with `startAt: WizardStepId.SignIn`, so the step shows its `Start` phase with the real copy

- [ ] **Step 4: Render and inspect**

Run: `KCAP_SHOTS_OUT=/tmp/shots dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/*wizard*"`
Expected: two 1520×1520 PNGs; the edition step shows the cloud and local choices; the sign-in step shows the sign-in prompt with no account name filled in

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.App.Tests.Unit
git commit -m "Render the setup wizard steps for the docs"
```

---

### Task 6: Local recovery scene and the run recipe

**Files:**
- Modify: `test/Capacitor.App.Tests.Unit/DocsShots.cs`
- Modify: `test/Capacitor.App.Tests.Unit/DocsShotScenes.cs`
- Modify: `test/Capacitor.App.Tests.Unit/AGENTS.md` or `CLAUDE.md` (whichever lists the gated suites; one line pointing at the harness and the env var)

**Interfaces:**
- Consumes: `HomeViewModel(...)` as `LauncherPaneViewSmokeTests.Build` constructs it, `HomeViewModel.AttachLocalServerRecovery(LocalServerRecoveryViewModel)`, `LocalServerRecoveryViewModel(status, isLocalServer: true, start, reconnect, shutdown)` with `status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying, "Connection refused"))`, `MainWindowViewModel(... home:, laneStatus:)`
- Produces: `DocsShotScenes.LocalRecovery()` → `MainWindow`

- [ ] **Step 1: Write the scene test** `desktop-local-recovery` with sentinel `"StartLocalServerButton"`

- [ ] **Step 2: Implement `LocalRecovery`**: Northwind daemon, a `HomeViewModel` with the recovery attached, a `MainWindowViewModel` with `home:` and `laneStatus:` set to the same Retrying observable, no session open, `tenantName: "local"`

- [ ] **Step 3: Render the full set and check every file**

Run: `rm -rf /tmp/shots && mkdir /tmp/shots && KCAP_SHOTS_OUT=/tmp/shots dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/*" && ls /tmp/shots`
Expected: exactly the seven `desktop-*-dark.png` files; `sips -g pixelWidth -g pixelHeight` reports 2400×1520 for the five main-window figures, 1200×1160 for settings, 1520×1520 for the two wizard steps

- [ ] **Step 4: Scan the PNG set for leaks** by eye: no path under `/Users/tony`, no real daemon or profile name, no `localhost`

- [ ] **Step 5: Write the run recipe** (one line in the test project's agent notes): `KCAP_SHOTS_OUT=<dir> dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DocsShots/*"` renders the docs figures; the consumer is kcap-web `scripts/docs-shots/capture-desktop.sh`

- [ ] **Step 6: Run the source and unit gates the repo expects before a push** (the CLAUDE.md there names them), then commit

```bash
git add test/Capacitor.App.Tests.Unit
git commit -m "Render the local recovery screen for the docs"
```

- [ ] **Step 7: Open the PR** against kcap-cli `main`, titled `Add a headless harness that renders the docs' desktop figures`, body per the repo's PR template; note that the first docs capture runs from `main` and later captures from the first release tag carrying this change
