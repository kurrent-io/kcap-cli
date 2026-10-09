# Browser setup background import — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The browser first-run flow imports a capped first pass per privacy level, hands the rest to one sequential detached child, and its Done page offers the eval-watch prompt without ever claiming the import finished while the child runs.

**Architecture:** Two repositories. kcap-server (PR 1, ships first) records four new optional fields on the import outcome and teaches the Done page a "background" arm plus an eval-watch panel. kcap-cli (PR 2) caps the browser import, spawns one plan-driven child, writes the handoff file, sends the new fields, adds a `get_connection` MCP tool and tightens eval-watch.

**Tech Stack:** .NET 10, NativeAOT (CLI), Blazor Server + bUnit (server UI), TUnit on Microsoft Testing Platform, KurrentDB/Eventuous (server events).

**Spec:** `docs/superpowers/specs/2026-10-08-browser-setup-background-import-design.md` (kcap-cli repo)

**Branches:** CLI `claude-tyoung/browser-install-background-import-02c2ae`; server `claude-tyoung/browser-import-background-done`.

## Global Constraints

- Foreground cap: `SetupCommand.ForegroundImportCap` (5), applied **per chosen level**.
- Wire tokens — `background`: `not_needed`, `running`, `exited_zero`, `failed`. `handoff_suppressed`: `import_failed`, `no_new_sessions`, `nothing_landed`, `skill_not_installed`, `no_agent_detected`, `handoff_file_unwritten`.
- `handoff_prompt`: ≤ 200 chars, printable characters and `\n` only; mutually exclusive with `handoff_suppressed`. `background_remaining`: non-negative int.
- The new fields may ride on a `run_failed` outcome; a reason still forbids non-zero counts.
- Detached-only env vars: `KCAP_IMPORT_PLAN` (plan path); honoured only with `KCAP_IMPORT_DETACHED_LOG` set.
- Plan file: `<config>/import-plan-<runId>.json`, owner-only, 7-day prune; holds `server_url` and ordered levels.
- Browser-flow retry advice names `kcap setup` (same repositories), never plain `kcap import`; the terminal flow's lines are unchanged.
- Comments: scarce, per each repo's comment rules (no history, no spec coordinates, no ticket narration).
- AOT: no IL2026/IL3050 on `dotnet publish`; `JsonArray` built with its constructor/`Add`, never a collection expression.
- Toolchain: `~/.dotnet/dotnet` (the PATH `dotnet` is 8.0).
- Tests: TUnit; `--treenode-filter`, never `--filter`; every assertion awaited.

## Review Focus

1. **First run, nothing saved yet** — a first-ever `kcap setup` whose profile has no server: the child must still import to the chosen server (Task C3 test `Plan_mode_runs_with_no_configured_server`).
2. **Only one level chosen** — the merge, remaining count and cohort must not treat the missing level as unknown (Task C4 test `A_single_level_run_is_exact_not_partial`).
3. **Report arrives after the screen went quiet** — the Done page must resume reading rather than sit settled (Task S3 test `A_background_report_after_quiet_reopens_and_reads_again`).
4. **Older server receiving the new fields** — unknown members are ignored; the request must still be accepted (Task C5 test `Outcome_json_carries_new_fields_only_when_set` keeps the wire additive).
5. **Pasted into an agent under another profile** — eval-watch must close before any eval read (Task C6 skill text + `get_connection` test).

---

## Part 1 — kcap-server (PR 1)

Work on the server branch. Build: `~/.dotnet/dotnet build src/Capacitor.Server/Capacitor.Server.csproj`. Before pushing: `~/.dotnet/dotnet run --project test/Capacitor.Source.Tests/Capacitor.Source.Tests.csproj`.

### Task S1: Record the four outcome fields

**Files:**
- Create: `src/Capacitor.Server.Core/FirstRun/FirstRunImportBackground.cs`
- Create: `src/Capacitor.Server.Core/FirstRun/FirstRunHandoffSuppressedReasons.cs`
- Modify: `src/Capacitor.Server.Core/FirstRun/FirstRunImportOutcome.cs` (record gains 4 optional params; `Clean` unchanged)
- Modify: `src/Capacitor.Server.Core/Events/FirstRunEvents.cs` (`FirstRunImportOutcomeReportedEvent` gains 4 optional props)
- Modify: `src/Capacitor.Server.Services/FirstRun/FirstRunFlowStore.cs` (`ReportImportOutcomeAsync` validation + event; `ReadImportOutcome` fold)
- Modify: `src/Capacitor.Api.Public/FirstRun/FirstRunFlowEndpoints.cs` (`ImportOutcomeRequest` + pass-through + telemetry `background`)
- Test: `test/Capacitor.Server.Tests.Read/FirstRun/FirstRunFlowStoreTests.cs`, `test/Capacitor.Server.Tests.Auth/FirstRun/FirstRunFlowEndpointTests.cs`

**Interfaces — Produces:**
```csharp
namespace Capacitor.FirstRun;
public static class FirstRunImportBackground {
    public const string NotNeeded = "not_needed", Running = "running", ExitedZero = "exited_zero", Failed = "failed";
    public static readonly IReadOnlyList<string> All = [NotNeeded, Running, ExitedZero, Failed];
    public static bool IsKnown(string? token) => token is not null && All.Contains(token, StringComparer.Ordinal);
}
public static class FirstRunHandoffSuppressedReasons {
    public const string ImportFailed = "import_failed", NoNewSessions = "no_new_sessions", NothingLanded = "nothing_landed",
                        SkillNotInstalled = "skill_not_installed", NoAgentDetected = "no_agent_detected",
                        HandoffFileUnwritten = "handoff_file_unwritten";
    public static readonly IReadOnlyList<string> All = [...all six...];
    public static bool IsKnown(string? token) => ...;
    public const int MaxPromptLength = 200;
    public static bool IsValidPrompt(string? prompt) =>
        prompt is { Length: > 0 and <= MaxPromptLength } && prompt.All(c => c == '\n' || !char.IsControl(c));
}
public sealed record FirstRunImportOutcome(
    int Imported, int Skipped, int Failed, DateTimeOffset DecidedAt, DateTimeOffset ReportedAt,
    string? Reason = null, string? Background = null, int? BackgroundRemaining = null,
    string? HandoffPrompt = null, string? HandoffSuppressed = null);
```
Event props: `[JsonPropertyName("background")] string? Background`, `"background_remaining" int?`, `"handoff_prompt" string?`, `"handoff_suppressed" string?`.

- [ ] **Step 1: Failing store tests.** In `FirstRunFlowStoreTests` add (following the file's existing `ReportImportOutcomeAsync` tests for setup/decision helpers):
  - `An_outcome_carries_background_and_prompt_through_the_fold` — report `Background=running, BackgroundRemaining=12, HandoffPrompt="Follow my kcap import\n(run: 0123…)"`, re-read state, assert all three round-trip.
  - `A_run_failed_outcome_may_carry_background_fields` — `Reason=run_failed`, zero counts, `Background=running` → accepted.
  - `Rejects_unknown_background_token`, `Rejects_negative_remaining`, `Rejects_prompt_and_suppression_together`, `Rejects_control_characters_in_the_prompt`, `Rejects_unknown_suppression_token` — each `ArgumentException`.
  - `A_stored_unknown_background_token_reads_as_absent` — append the raw event with `Background="paused"`, fold drops it to null while keeping counts.
- [ ] **Step 2: Run, expect FAIL** — `~/.dotnet/dotnet run --project test/Capacitor.Server.Tests.Read/Capacitor.Server.Tests.Read.csproj -- --treenode-filter "/*/*/FirstRunFlowStoreTests/*"`.
- [ ] **Step 3: Implement.** Store validation, after the existing reason checks:
```csharp
if (outcome.Background is not null && !FirstRunImportBackground.IsKnown(outcome.Background))
    throw new ArgumentException($"Unknown background status '{outcome.Background}'.", nameof(outcome));
if (outcome.BackgroundRemaining is < 0)
    throw new ArgumentException("A remaining count cannot be negative.", nameof(outcome));
if (outcome.HandoffPrompt is not null && outcome.HandoffSuppressed is not null)
    throw new ArgumentException("A handoff is either offered or suppressed, not both.", nameof(outcome));
if (outcome.HandoffPrompt is not null && !FirstRunHandoffSuppressedReasons.IsValidPrompt(outcome.HandoffPrompt))
    throw new ArgumentException("Unusable handoff prompt.", nameof(outcome));
if (outcome.HandoffSuppressed is not null && !FirstRunHandoffSuppressedReasons.IsKnown(outcome.HandoffSuppressed))
    throw new ArgumentException($"Unknown handoff suppression '{outcome.HandoffSuppressed}'.", nameof(outcome));
```
  Event: copy the four values. Fold (`ReadImportOutcome`): keep each only if valid (unknown token → null, negative → null, invalid prompt → null, both prompt and suppression → drop both); pass into the record.
- [ ] **Step 4: Endpoint.** Extend `ImportOutcomeRequest` with `[property: JsonPropertyName("background")] string? Background = null`, `"background_remaining" int? BackgroundRemaining`, `"handoff_prompt" string? HandoffPrompt`, `"handoff_suppressed" string? HandoffSuppressed`; pass into `new FirstRunImportOutcome(...)`. The existing `catch (ArgumentException)` already returns 400 — update its message to "Unusable counts, reason or background fields." Telemetry: add `["background"] = landed.Background ?? "absent"` to the branch properties.
- [ ] **Step 5: Endpoint tests** in `FirstRunFlowEndpointTests` following the file's outcome tests: `Outcome_with_background_fields_is_accepted_and_returned`, `Outcome_with_unknown_background_is_400`, `Outcome_with_reason_and_background_is_accepted`.
- [ ] **Step 6: Run** both suites (`Tests.Read` store filter, `Tests.Auth` `/*/*/FirstRunFlowEndpointTests/*`); expect PASS.
- [ ] **Step 7: Commit** — `git commit -m "Record background status and handoff on the import outcome"`.

### Task S2: Carry the fields to the Done view

**Files:**
- Modify: `src/Capacitor.Api.Web.Abstractions/FirstRun/FirstRunImportOutcomeView.cs`
- Modify: `src/Capacitor.Server.Services/FirstRun/FirstRunDoneScreen.cs` (add `BackgroundRunning`)
- Modify: `src/Capacitor.Api.Web.Abstractions/FirstRun/FirstRunDoneScreenView.cs` (add `BackgroundRunning`)
- Modify: `src/Capacitor.Api.Web/FirstRun/FirstRunFlowMapper.cs`
- Test: `test/Capacitor.Server.Services.Tests/FirstRun/FirstRunDoneModelTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record FirstRunImportOutcomeView(
    int Failed, FirstRunImportOutcomeReasonView? Reason,
    FirstRunImportBackgroundView? Background = null, int? BackgroundRemaining = null,
    string? HandoffPrompt = null, FirstRunHandoffSuppressedView? HandoffSuppressed = null);
public enum FirstRunImportBackgroundView { NotNeeded, Running, ExitedZero, Failed }
public enum FirstRunHandoffSuppressedView { ImportFailed, NoNewSessions, NothingLanded, SkillNotInstalled, NoAgentDetected, HandoffFileUnwritten }
// FirstRunDoneScreen:
public bool BackgroundRunning => Outcome?.Background == FirstRunImportBackground.Running;
// FirstRunDoneScreenView:
public required bool BackgroundRunning { get; init; }
```
(Each enum in its own file under `Api.Web.Abstractions/FirstRun/`.)

- [ ] **Step 1: Failing model tests** in `FirstRunDoneModelTests`: `Background_running_is_read_from_the_outcome`, `No_outcome_is_not_background_running`, `Exited_zero_is_not_background_running`.
- [ ] **Step 2: Run, expect FAIL** — `~/.dotnet/dotnet run --project test/Capacitor.Server.Services.Tests/Capacitor.Server.Services.Tests.csproj -- --treenode-filter "/*/*/FirstRunDoneModelTests/*"`.
- [ ] **Step 3: Implement** the property, the view enums and the mapper (`Background` token → enum via a switch; unknown → null; `HandoffSuppressed` likewise; `BackgroundRunning = screen.BackgroundRunning`).
- [ ] **Step 4: Run, expect PASS.** Build `src/Capacitor.Server` to catch every `new FirstRunDoneScreenView` / `FirstRunImportOutcomeView` construction (tests included — fix the bUnit test helpers that build views by adding `BackgroundRunning = false`).
- [ ] **Step 5: Commit** — `Carry the import's background status to the Done view`.

### Task S3: Done page background arm (settle, poll, announce)

**Files:**
- Modify: `src/Capacitor.Ui/Components/FirstRun/DoneStep.razor`
- Test: `test/Capacitor.Server.Tests.UI/UnitTests/FirstRunDoneStepTests.cs`

**Behaviour (exact):**
- `OnParametersSetAsync`: `_settled |= (View.Reported && !View.BackgroundRunning) || View.NothingImported || (_framed && _figures is null);`
- After that line: `if (View.BackgroundRunning && !_backgroundArmed) await EnterBackgroundAsync();`
- `EnterBackgroundAsync()`:
```csharp
_backgroundArmed = true;
if (_polling is { } previous) { await previous.CancelAsync(); previous.Dispose(); }
if (_pollTask is { } running) { try { await running; } catch (OperationCanceledException) { } }
_quiet = false; _quietTicks = 0;
if (_settled && !View.NothingImported && _figures is not null) { _settled = false; _gaveUp = false; _announced = null; }
_pollDeadline = _clock.GetUtcNow() + PollBudget;
Announce?.Invoke("Your newest sessions are in. The rest is importing in the background.");
if (_rendered && Observable) StartPoll();
```
  `StartPoll()` creates `_polling`, sets `_pollTask = PollAsync(_polling.Token)` and observes it. `OnAfterRenderAsync(firstRender)` sets `_rendered = true` and calls `StartPoll()` under its existing guard. `PollAsync` takes its deadline from `_pollDeadline ?? now + PollBudget`.
- `PollAsync` loop: `(read, owed) = (_quiet && !View.BackgroundRunning, _quiet && View.OutcomeOwed);` — quiet never ends reads in the background arm.
- `RefreshAsync`: `_quiet = !View.BackgroundRunning && (_quietTicks >= QuietTicks || Stalled);`
- `_mayLeave` stays true once set (leaving is never withheld).
- End of `PollAsync` (budget): unchanged assignments, plus `_gaveUp = View.OutcomeOwed || View.BackgroundRunning;`.
- `AnnounceSettled`: new `Ending.Background` computed first: `var ending = View.BackgroundRunning ? Ending.Background : View.Reported ? Ending.Reported : _gaveUp ? Ending.GaveUp : Ending.Unobserved;` and for `Ending.Background` announce "The rest is still importing in the background — it keeps landing in your workspace." then return.
- `Fraction`: `Math.Clamp(..., 0, View.BackgroundRunning ? 99 : 100)`.
- Header (`StepHeader` body): add an arm before `_settled && _coverage is { Sessions: > 0 }`: `else if (View.BackgroundRunning && _settled)` → "The rest is still importing in the background — it keeps landing in your workspace."

- [ ] **Step 1: Failing bUnit tests** (use the file's existing render helpers, `FakeTimeProvider` and `Coverage` fake; build the view with `Reported = true`, `Outcome = new(0, null, FirstRunImportBackgroundView.Running, 7, "Follow my kcap import\n(run: …)")`, `BackgroundRunning = true`):
  - `A_background_report_does_not_settle_or_announce_finished` — announcements contain "Your newest sessions are in" and none contains "Import finished"; the progress frame is present.
  - `Quiet_does_not_end_reads_in_the_background_arm` — unchanged coverage for 10 ticks past the floor; `Coverage.Reads` keeps rising.
  - `A_background_report_after_quiet_reopens_and_reads_again` — render with no outcome (owed), let it go quiet, then re-render with the background outcome; reads resume.
  - `The_bar_never_reaches_full_in_the_background_arm` — coverage above expected; `aria-valuenow` is 99.
  - `Budget_exhaustion_in_the_background_arm_says_it_is_still_importing` — advance past 30 min from the report; announcement and header say "still importing in the background"; no "Import finished".
  - `A_report_without_background_settles_as_before` — `Background = NotNeeded`: settles on report (existing behaviour).
- [ ] **Step 2: Run, expect FAIL** — `~/.dotnet/dotnet run --project test/Capacitor.Server.Tests.UI/Capacitor.Server.Tests.UI.csproj -- --treenode-filter "/*/*/FirstRunDoneStepTests/*"`.
- [ ] **Step 3: Implement** the behaviour above (new fields `_backgroundArmed`, `_pollTask`, `_pollDeadline`, `_rendered`; `Ending.Background`).
- [ ] **Step 4: Run the whole `FirstRunDoneStepTests` class, expect PASS** (existing tests guard the non-background paths).
- [ ] **Step 5: Commit** — `Keep the Done page reading while the import runs in the background`.

### Task S4: Eval-watch panel and retry copy

**Files:**
- Modify: `src/Capacitor.Ui/Components/FirstRun/DoneStep.razor`
- Test: `test/Capacitor.Server.Tests.UI/UnitTests/FirstRunDoneStepTests.cs`

**Behaviour:**
- Panel shown when `View.Outcome is { } o` and:
  - `o.HandoffPrompt is { } prompt` → card: "Paste this into a coding agent on <mono>@View.MachineLabel</mono> to watch your newest sessions' evals", a `<pre class="kcap-first-run__prompt">@prompt</pre>` with `<CopyButton Text="@prompt"/>`; when `o.Background is Running`: `o.BackgroundRemaining is { } n` → "@Sessions(n) still to import in the background" else "The rest of your history is importing in the background".
  - `o.HandoffSuppressed is SkillNotInstalled or NoAgentDetected` → link `<a href="/sessions?status=ended">See your sessions as they are evaluated</a>`.
  - otherwise no panel.
- `o.Background is Failed` → `<Warning>` "The rest did not start importing" / "Run `kcap setup` again and choose the same repositories to finish it."
- Retry copy: every `kcap import` mention in DoneStep becomes `kcap setup` with "again and choose the same repositories" phrasing:
  - NothingImported decline line: "<mono>kcap setup</mono> again brings your existing history over whenever you want."
  - Failed-count row: "Run <mono>kcap setup</mono> again with the same repositories to try again."
  - `ReasonText(RunFailed)` and its announcement: when `View.BackgroundRunning` → "Part of the first import failed — the rest, including what failed, is being retried in the background."; otherwise "Your import stopped partway — what has landed is here. Run kcap setup again with the same repositories to bring over the rest."
  - Failed-count announcement: "Import finished. N did not upload — run kcap setup again with the same repositories to try again."
- [ ] **Step 1: Failing tests:** `The_prompt_panel_renders_with_a_copy_button`, `The_panel_says_how_many_are_still_to_import`, `A_skill_not_installed_suppression_links_to_sessions`, `A_nothing_landed_suppression_shows_no_panel`, `A_failed_background_warns_and_names_kcap_setup`, `No_line_on_the_page_names_kcap_import` (render each arm; assert markup lacks "kcap import"), `A_run_failed_with_background_running_says_it_is_retried`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** markup + scoped CSS for `.kcap-first-run__prompt` (`white-space: pre-wrap; font-family: var(--font-mono)` per neighbouring `__mono` class).
- [ ] **Step 4: Run class, expect PASS;** run `Capacitor.Source.Tests`.
- [ ] **Step 5: Commit** — `Offer the eval-watch prompt on the Done page`.

---

## Part 2 — kcap-cli (PR 2)

Work on the CLI branch. Build: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`. Unit suites run as `~/.dotnet/dotnet run --project test/<Suite>/<Suite>.csproj -- --treenode-filter "/*/*/<Class>/*"`.

### Task C1: Capped visibility work covers only the selection

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ImportCommand.cs` (the `forcePrivate` preflight `existing` list ~L1552 and the `shareWithOrg` loop ~L1598)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ImportCommandCappedVisibilityTests.cs` (new; WireMock server like neighbouring import tests)

- [ ] **Step 1: Failing tests** — with 3 file-based Claude sessions on disk, the server reporting all three as `Partial`, `maxSessions: 1`:
  - `Private_preflight_under_a_cap_touches_only_the_selected_session` — WireMock records visibility PUTs; exactly one, for the newest session.
  - `Share_write_under_a_cap_touches_only_the_selected_session`.
  - `Without_a_cap_every_existing_session_is_made_private` (guards unchanged behaviour).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** — add `&& (selectedIds is null || selectedIds.Contains(c.SessionId))` to both filters.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `Bound a capped import's visibility writes to its selection`.

### Task C2: Lane result, per-level capped passes and the shared pass runner

**Files:**
- Create: `src/Capacitor.Cli.Core/FirstRun/FirstRunImportResult.cs`
- Create: `src/Capacitor.Cli.Core/FirstRun/FirstRunImportBackground.cs` (same tokens as S1)
- Modify: `src/Capacitor.Cli.Core/FirstRun/IFirstRunImportLane.cs` (`ImportAsync` returns `Task<FirstRunImportResult>`)
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (`SetupImportLane`: `Pass.MaxSessions`, static `RunPassAsync`, per-level outcomes)
- Modify: `src/Capacitor.Cli.Core/FirstRun/BrowserFirstRunFlow.cs` (consume `Totals`)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/SetupImportLaneTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/FirstRun/BrowserFirstRunFlowTests.cs` (fake lane)

**Interfaces — Produces:**
```csharp
namespace Capacitor.Cli.Core.FirstRun;
public sealed record FirstRunImportResult(
    FirstRunImportTotals? Totals,
    string?               Background          = null,
    int?                  BackgroundRemaining = null,
    string?               HandoffPrompt       = null,
    string?               HandoffSuppressed   = null) {
    public static FirstRunImportResult Lost { get; } = new((FirstRunImportTotals?)null);
}
// SetupImportLane
internal sealed record Pass(FirstRunImportLevel Level, IReadOnlyList<FirstRunImportChoice> Repos, DateOnly? Since,
                            bool SkipTitle, IReadOnlyList<HarnessId>? Vendors, int? MaxSessions);
internal static Task<SetupImportRun> RunPassAsync(
    ConfigRoot config, ProfileContext profiles, UserHome home, ICapacitorHttpClient http,
    HarnessRegistry harnesses, GitProviderRouter router, TimeProvider time, Pass pass);
internal IReadOnlyList<(FirstRunImportLevel Level, SetupImportRun Run)> Runs { get; }   // per pass, in order
```
`RunPassAsync` wraps the existing `HandleImport` call (adds `maxSessions: pass.MaxSessions`, `onSelected`) in try/catch exactly as `SetupImportRunner.RunAsync` does, returning `SetupImportRun(exit, selection, outcome, fault)`; it rethrows `OperationCanceledException`. The lane's test seam becomes `Func<Pass, Task<SetupImportRun>>? runner`.

- [ ] **Step 1: Update existing lane tests** to the new seam (`Clean()` returns `new SetupImportRun(0, Selection(...), new(Counts(), 0), null)`; throwing seams stay throwing) and add failing:
  - `Each_chosen_level_runs_capped` — both passes have `MaxSessions == SetupCommand.ForegroundImportCap`.
  - `A_throwing_pass_keeps_the_other_levels_run` — `Runs` has both entries; OnlyMe's carries the fault.
  - `Totals_are_null_when_any_pass_is_lost` and `Totals_sum_passes_that_reported`.
- [ ] **Step 2: Update `FakeImportLane`** in `BrowserFirstRunFlowTests`: `public FirstRunImportResult Moved { get; set; } = new(new(3, 1, 0));` returning it; replace `new FirstRunImportTotals(7, 2, 1)` with `new FirstRunImportResult(new(7, 2, 1))`; a `null` totals case uses `FirstRunImportResult.Lost`.
- [ ] **Step 3: Run both suites, expect FAIL (compile).**
- [ ] **Step 4: Implement.** In `BrowserFirstRunFlow.ActOnImportDecisionAsync`: `FirstRunImportResult? moved = null; … state.Outcome = moved is { Totals: { } totals } ? Outcome(answer.DecidedAt, totals, null, moved) : ReasonOnly(answer.DecidedAt, FirstRunImportOutcomeReasons.RunFailed, moved);` (the extra parameter is used in C5; here pass it through unused).
- [ ] **Step 5: Run, expect PASS.**
- [ ] **Step 6: Commit** — `Cap each browser import level and keep every pass's run`.

### Task C3: The import plan and its detached child

**Files:**
- Create: `src/Capacitor.Cli/Commands/ImportPlan.cs`
- Create: `src/Capacitor.Cli/Commands/DetachedImportPlanRunner.cs`
- Modify: `src/Capacitor.Cli/Commands/BackgroundImportRequest.cs` (add `string? PlanPath = null`)
- Modify: `src/Capacitor.Cli/Commands/BackgroundImportSpawner.cs` (argv + env via a pure `BuildStartInfo`)
- Modify: `src/Capacitor.Cli/Program.cs` (plan dispatch before the no-server gate)
- Modify: `src/Capacitor.Cli/Commands/CommandServices.cs` (register `DetachedImportPlanRunner`)
- Test: `ImportPlanTests.cs`, `DetachedImportPlanRunnerTests.cs`, `BackgroundImportSpawnerTests.cs` (extend) under `test/Capacitor.Cli.Tests.Unit/Commands/`

**Interfaces — Produces:**
```csharp
internal sealed record ImportPlanLevel(FirstRunImportLevel Level, IReadOnlyList<(string Owner, string Name)> Repos,
                                       DateOnly? Since, IReadOnlyList<HarnessId>? Vendors, bool SkipTitle);
internal sealed record ImportPlan(string ServerUrl, IReadOnlyList<ImportPlanLevel> Levels) {
    public const string EnvVar = "KCAP_IMPORT_PLAN";
    public static string PathFor(ConfigRoot config, string runId) => config.Path($"import-plan-{runId}.json");
    public string ToJson();                                   // JsonObject/JsonArray built AOT-safely
    public static ImportPlan? Parse(string json);             // null on any malformation
    public static ImportPlan? Read(string path);              // null when missing/unreadable/invalid
    public void Write(string path);                           // OwnerOnlyFile.CreateNew; throws IOException on failure
    public static bool IsUsableServer(string url);            // absolute https, or http with loopback host
    public static void Prune(ConfigRoot config, DateTimeOffset now); // import-plan-*.json older than 7 days
}
sealed class DetachedImportPlanRunner(ConfigRoot config, ProfileContext profiles, UserHome home, HarnessRegistry harnesses,
                                      ChosenServerHttp http, GitProviderRouter router, TimeProvider time) {
    public async Task<int> RunAsync(string planPath);         // 0 when every level reported; 1 otherwise
}
```
JSON shape: `{"schema_version":1,"server_url":"…","levels":[{"level":"only_me"|"shared","repos":["owner/name",…],"since":"yyyy-MM-dd"|null,"vendors":["claude",…]|null,"skip_title":bool}]}`. Vendor ids via the existing `HarnessId` ↔ vendor-id mapping used by `ReportFirstRunImportRequest.Vendors`.

`RunAsync`: read plan (fail → log "Could not read the import plan" and return 1); `var context = SetupCommand.ImportContext(profiles, plan.ServerUrl); await using var scoped = http.For(plan.ServerUrl, context);` then for each level in order `await SetupImportLane.RunPassAsync(config, context, home, scoped.GetRequiredService<ICapacitorHttpClient>(), harnesses, router, time, new Pass(level..., MaxSessions: null))`; log each fault; delete the plan file in `finally` (best-effort).

Program.cs, immediately before `if (baseUrl is null && !offlineCommands…)`:
```csharp
if (command == "import" && detachedImport is not null
 && Environment.GetEnvironmentVariable(ImportPlan.EnvVar) is { Length: > 0 } planPath
 && ImportPlan.Read(planPath) is { } plan && ImportPlan.IsUsableServer(plan.ServerUrl))
    return await Run<DetachedImportPlanRunner>().RunAsync(planPath);
```
Spawner: when `request.PlanPath` is set, argv is `import --yes` and env adds `KCAP_IMPORT_PLAN=<path>`; otherwise unchanged (`import --all --yes --skip-title`).

- [ ] **Step 1: Failing tests:**
  - `ImportPlanTests`: `Round_trips_levels_in_order`, `Parse_rejects_missing_server`, `Parse_rejects_unknown_level`, `IsUsableServer_accepts_https_and_loopback_http_only`, `Write_creates_an_owner_only_file` (Unix), `Prune_removes_only_old_plans`.
  - `BackgroundImportSpawnerTests`: `A_plan_request_runs_import_yes_with_the_plan_variable`, `A_plain_request_keeps_the_all_arguments`.
  - `DetachedImportPlanRunnerTests` (runner seam: add `Func<ImportPlanLevel, ProfileContext, Task<SetupImportRun>>? passRunner = null` ctor param to the runner): `Runs_levels_in_plan_order`, `Each_pass_gets_the_plans_server_not_the_profiles` (profile resolution has `ServerUrl = "https://old.example"`, plan has `https://new.example`; seam sees `new`), `Plan_mode_runs_with_no_configured_server` (profile with null server), `An_unreadable_plan_fails_without_importing`, `The_plan_file_is_deleted_after_the_run`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** `ImportPlan`, the runner, spawner `BuildStartInfo` (pure, returns `ProcessStartInfo`), Program dispatch, DI registration.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `Run a browser import's remainder from a detached plan`.

### Task C4: Lane spawns the child and writes the handoff

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (`SetupImportLane` ctor gains `IBackgroundImportSpawner spawner`, `string serverUrl`, `string profileName`, `string defaultVisibility`, `string workingDirectory`, `CodingAgentsStep.Paths paths`; construction site in `RunBrowserFlowStepAsync`)
- Modify: `src/Capacitor.Cli/Commands/ImportHandoffFile.cs` (`Compose` gains `HandoffCohort? cohortOverride = null`, `string scope = "all"`; `ToJson` writes `Scope`)
- Create: `src/Capacitor.Cli/Commands/ForegroundImportMerge.cs`
- Test: `SetupImportLaneTests.cs`, `ImportHandoffFileTests.cs`, `ForegroundImportMergeTests.cs`, `SetupImportLaneConstructionGuardTests.cs` (update expected args)

**Interfaces — Produces:**
```csharp
internal static class ForegroundImportMerge {
    /// Levels in run order. Null candidates on a level that ran = unknown.
    public static (ForegroundImportOutcome Merged, HandoffCohort? CohortOverride, int? Remaining)
        Merge(IReadOnlyList<ForegroundImportOutcome> levels);
}
```
Merge rules: candidates concatenated (known levels only), null when all unknown; succeeded concatenated; `Certainty` Complete only if all; counts summed; `RemainderExists` OR; `CohortOverride = PartialExact` when some known and some unknown, else null; `Remaining = Σ(candidates.Count − succeeded.Count)` when all known, else null.

`ImportAsync` after the passes (when at least one level ran and not cancelled):
1. Fold each run with `ForegroundImportOutcome.From`.
2. `Merge`.
3. Write the plan (`ImportPlan` from the answer: levels in run order with `Since`, vendors, skip-title; `ServerUrl = serverUrl`); on write failure `launch = new BackgroundImportLaunch(Failed, null, null, msg)`; else `launch = spawner.Spawn(new BackgroundImportRequest(runId, profileName, defaultVisibility, workingDirectory, planPath))`. `ImportPlan.Prune` after.
4. `PrintBackground(launch, browser: true)` (Task C7 adds the flag).
5. `HandoffDecision.Decide(merged, launch.Status, eligible.Count, detected)`; write `ImportHandoffFile.Compose(..., cohortOverride, scope: "repos")`; on IO failure → suppressed `handoff_file_unwritten`.
6. Return `FirstRunImportResult(totals, launch.Status.Wire(), remaining, prompt, suppressed)` where `prompt = decision.Offered ? SetupCommand.HandoffPromptText(runId) : null`, `suppressed = decision.Offered ? null : decision.Reason!.Value.Wire()`.

Add `BackgroundImportStatus.Wire()` extension (`not_needed`/`running`/`exited_zero`/`failed`) beside the enum and reuse it in `ImportHandoffFile.ToJson`.

- [ ] **Step 1: Failing tests:**
  - `ForegroundImportMergeTests`: `Concatenates_only_me_first`, `One_unknown_level_is_partial_exact`, `All_unknown_is_null_candidates`, `A_single_level_run_is_exact_not_partial`, `Remaining_counts_failed_selected_sessions`, `Remaining_is_null_when_any_level_is_unknown`.
  - `ImportHandoffFileTests`: `A_cohort_override_wins_over_the_count_rule`, `Scope_is_written`.
  - `SetupImportLaneTests` (fake spawner `FakeBackgroundImportSpawner` already exists): `Spawns_exactly_one_child_with_a_plan_when_a_level_was_chosen`, `A_decline_spawns_nothing`, `The_plan_carries_the_server_and_levels_in_order`, `The_result_offers_the_prompt_when_the_decision_does`, `A_handoff_write_failure_reports_handoff_file_unwritten` (make the config dir read-only for the handoff path — or inject a writer seam `Func<ImportHandoffFile, bool>`), `A_lost_pass_still_spawns_and_still_offers_for_the_other_level`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.** Update the construction guard test's expected argument text to include the new arguments.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `Hand a browser import's remainder to one child and write its handoff`.

### Task C5: Send the new outcome fields

**Files:**
- Modify: `src/Capacitor.Cli.Core/FirstRun/FirstRunFlowModels.cs` (`ReportFirstRunImportOutcomeRequest` gains 4 optional `[JsonIgnore(Condition = WhenWritingNull)]` props)
- Modify: `src/Capacitor.Cli.Core/FirstRun/BrowserFirstRunFlow.cs` (`Outcome`/`ReasonOnly` copy the result's fields)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/FirstRun/BrowserFirstRunFlowTests.cs`

```csharp
[JsonPropertyName("background")]           [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Background { get; init; }
[JsonPropertyName("background_remaining")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?    BackgroundRemaining { get; init; }
[JsonPropertyName("handoff_prompt")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HandoffPrompt { get; init; }
[JsonPropertyName("handoff_suppressed")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HandoffSuppressed { get; init; }
```
(Check the source-generated `JsonSerializerContext` that serializes this type picks the attributes up; no reflection.)

- [ ] **Step 1: Failing tests:** `The_outcome_carries_background_and_prompt` (fake lane returns `new FirstRunImportResult(new(1,0,0), "running", 4, "Follow my kcap import\n(run: …)")`; assert the reported request), `A_run_failed_outcome_still_carries_background` (`Totals = null, Background = "running"`), `Outcome_json_carries_new_fields_only_when_set` (serialize via the same context; absent fields are absent).
- [ ] **Step 2: Run, expect FAIL. Step 3: Implement. Step 4: PASS.**
- [ ] **Step 5: Commit** — `Report the background child and the eval-watch prompt to the browser`.

### Task C6: `get_connection` and eval-watch binding

**Files:**
- Create: `src/Capacitor.Cli/Commands/ConnectionTool.cs`
- Modify: `src/Capacitor.Cli/Commands/McpSessionsServer.cs` (list + dispatch before any HTTP)
- Modify: `kcap/skills/eval-watch/SKILL.md` (sections 3 and 4)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ConnectionToolTests.cs`

```csharp
static class ConnectionTool {
    internal const string Name = "get_connection";
    internal static McpTool Definition => new(Name,
        "The Capacitor server URL and kcap profile this MCP server uses for every other tool. Makes no network call.",
        new("object", new(), []), McpToolAnnotations.Read);
    internal static JsonObject Result(string serverUrl, string profile) =>
        new() { ["server_url"] = serverUrl.TrimEnd('/'), ["profile"] = profile };
}
```
Dispatch in `HandleToolCallAsync` ahead of the `search_sessions` branch: `if (toolName == ConnectionTool.Name) return BuildToolResult(id, ConnectionTool.Result(baseUrl, profiles.Name).ToJsonString());` (check the method is reached before any client/auth work — if not, short-circuit earlier where `tools/call` is routed).

Skill edits:
- Section 3 `import_failed`: "name `kcap import --all --yes` when the file's `scope` is `all`; when it is `repos`, say to run `kcap setup` again and choose the same repositories (plain `kcap import` would not keep their privacy levels); and the `background_log` when displayable."
- Layer B: `scope` ∈ {all, repos} (absent reads as `all`).
- Section 4 rewritten: (1) if `get_connection` is not among your tools → say kcap is older than this skill, update it and restart the agent, CLOSE, no lookup; (2) call `get_connection`; normalize both URLs as today; compare `server_url` and `profile` to the file's; mismatch → no lookup, CLOSE with remediation "start your agent from a shell with `KCAP_PROFILE=<profile>`, `KCAP_URL` unset (and `KCAP_CONFIG_DIR` if custom), restarting it so its kcap-sessions server picks that up, then prompt again"; (3) `kcap whoami` failing (not logged in) → say run `kcap login`, CLOSE.

- [ ] **Step 1: Failing tests:** `Lists_get_connection`, `Returns_the_resolved_server_and_profile`, `Answers_without_any_http_request` (handler invoked with a `FixedCapacitorHttpClient` that throws on use).
- [ ] **Step 2: FAIL. Step 3: Implement + skill text. Step 4: PASS.**
- [ ] **Step 5: Commit** — `Let eval-watch check the MCP connection before reading evals`.

### Task C7: Terminal copy in the browser flow

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (`SpectreFirstRunFlowProgress.Importing`, `SetupImportLane` failure lines, `PrintBackground(launch, bool browser = false)`, `BrowserImportSummary(answer, failed, pasteBlock)`, `RunImportStepAsync` browser branch)
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` `BrowserFlowAnswers` gains `string? HandoffPrompt`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs`

Copy:
- `Importing`: "Importing your newest sessions from {what}, as chosen in the browser. The rest will continue in the background."
- Lane per-pass failure lines: "… Run [cyan]kcap setup[/] again and choose the same repositories to retry it."
- `PrintBackground(browser: true)` Failed arm: "… Run [cyan]kcap setup[/] again and choose the same repositories to import the rest."
- `BrowserImportSummary`: failed line → "Partly imported {subject}. Run [cyan]kcap setup[/] again and choose the same repositories to finish it."; `NoReadableVendors` → "… Run 'kcap update', then 'kcap setup' to bring them in."; `Unreadable` → "… Run 'kcap update', then 'kcap setup' to import them."; when a prompt was offered, the caller appends the paste block via the existing `WriteNextSteps(…, pasteBlock)` path (`ImportStepResult.PasteBlock = HandoffPromptText(runId)` built from `BrowserFlowAnswers.HandoffPrompt`).

- [ ] **Step 1: Failing tests:** `Browser_summary_never_names_kcap_import` (every arm), `Browser_summary_hands_back_the_prompt_as_a_paste_block`, `Terminal_flow_background_failure_still_names_kcap_import_all`.
- [ ] **Step 2: FAIL. Step 3: Implement. Step 4: PASS.**
- [ ] **Step 5: Commit** — `Point browser-flow retries at kcap setup`.

### Task C8: Verification

- [ ] Full CLI unit suites one at a time (`Capacitor.Cli.Core.Tests.Unit`, `Capacitor.Cli.Tests.Unit`); compare failures against pristine main (known environmental install/config failures).
- [ ] `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output (or rely on the CI publish job if the memory watchdog kills it).
- [ ] README: no CLI surface change (plan variable is detached-only); confirm the README's MCP tools list (if any) gains `get_connection`.

---

## Delivery

1. Server PR (Part 1) → address Qodo/CodeRabbit → Codex review → CI green → admin squash merge.
2. CLI PR (Part 2) → same loop → admin squash merge.
3. Server follow-up: bump `src/cli` submodule only if needed (not required by this change).
