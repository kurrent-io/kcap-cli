# Unified agent install + repo import — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In `kcap setup`, replace the nine per-vendor install prompts with one default-yes prompt that installs artifacts for every detected harness, and add a final step that imports the current repo's past sessions.

**Architecture:** Reuse the tested `CodingAgentsStep` per-vendor installers behind a single `InstallAgents` gate; reuse `ImportCommand.HandleImport` behind a new Step 6, refreshing `AppConfig` state and threading the Step-3 visibility. Extract two pure decision helpers so the monolithic `SetupCommand.HandleAsync` logic is unit-testable.

**Tech Stack:** .NET 10 NativeAOT, TUnit + WireMock.Net, Spectre.Console prompts.

**Design spec:** `docs/superpowers/specs/2026-07-20-unified-agent-install-and-import-design.md` (approved, clean spec-review). Read it before starting.

## Global Constraints

- Build/test/publish with `~/.dotnet/dotnet` (PATH `dotnet` is 8.0 and cannot target .NET 10).
- No `IL3050`/`IL2026` AOT warnings — verify with `dotnet publish -c Release` (build does NOT surface them).
- `JsonArray` collection expressions are banned — use `new JsonArray(...)`.
- TUnit filtering uses `--treenode-filter` (glob), NOT `--filter`.
- Any user-facing CLI surface change updates `README.md` in the SAME PR (quick-start + per-command section), plus the relevant `help-*.txt`.
- Prompts use `new ConfirmationPrompt(text) { DefaultValue = true }` (default-yes) to match the existing style.
- Preserve every existing `--skip-<vendor>` / per-artifact `--skip-*` semantic exactly (see spec truth tables).
- No change to `kcap import --private` semantics (spec Non-goal).

## File structure

- `src/Capacitor.Cli/Commands/CodingAgentsStep.cs` — add `Options.InstallAgents`; ordered early-returns in `RunAsync`; route per-vendor gates through `InstallAgents`.
- `src/Capacitor.Cli/Commands/SetupCommand.cs` — detected-agent display + unified prompt; `SetResolvedState` call; Step 6; `--skip-import`; header renumber; two extracted decision helpers.
- `src/Capacitor.Cli/Commands/SetupDecisions.cs` (NEW) — pure helpers: agent-install decision + import eligibility/policy.
- `src/Capacitor.Cli.Core/.../AppConfig.cs` — `SetResolvedState(serverUrl, profile)`.
- `src/Capacitor.Cli/Commands/ImportCommand.cs` — `HandleImport` params (`autoSkipExclusions`, `defaultVisibility`); `CreateAuthenticatedClientAsync(baseUrl)`; `chainDefaultVisibility` derivation + threading through `ImportChainsAsync`/`ImportSingleSessionAsync`; chain New payload stamp; auto-skip exclusions.
- `src/Capacitor.Cli/Commands/IImportSource.cs` — `ImportContext.DefaultVisibility` (default `null`).
- `src/Capacitor.Cli/Commands/{Cursor,Copilot,Gemini,Kiro,Pi,OpenCode,Antigravity}ImportSource.cs` — guarded New-only default_visibility stamp.
- `src/Capacitor.Cli.Core/Resources/help-setup.txt`, `help-usage.txt`, `README.md` — docs.
- `test/Capacitor.Cli.Tests.Unit/` — unit tests per task.

---

## Task 1: `CodingAgentsStep.InstallAgents` gate

**Files:**
- Modify: `src/Capacitor.Cli/Commands/CodingAgentsStep.cs` (`Options` record; `RunAsync` head ~125-209; per-vendor gate expressions; `HandleAgentSkills`)
- Test: `test/Capacitor.Cli.Tests.Unit/CodingAgentsStepTests.cs` (existing or new)

**Interfaces:**
- Consumes: existing `Options`, `DetectedAgents`, `Result`, `Handle*`, injected `prompt`/`writeLine`.
- Produces: `Options.InstallAgents` (bool); `RunAsync` early-returns a zero-value `Result` when no agents detected or `!InstallAgents`.

- [ ] **Step 1 — Failing test: unified-no ⇒ zero mutations.** Add a test constructing `Options` with `InstallAgents = false` and all agents detected, injecting installer delegates that throw if invoked; assert `RunAsync` returns a zero-value `Result` and no installer/`prompt` delegate is called.

```csharp
[Test]
public async Task RunAsync_InstallAgentsFalse_InstallsNothing() {
    var detected = new CodingAgentsStep.DetectedAgents(Claude:true, Codex:true, Cursor:true);
    var opts = TestOptions() with { InstallAgents = false };
    var installers = ThrowingInstallers(); // every delegate throws
    var result = await CodingAgentsStep.RunAsync(opts, detected, TestPaths(), installers,
        prompt: _ => throw new Exception("must not prompt"), writeLine: _ => {});
    await Assert.That(result).IsEqualTo(new CodingAgentsStep.Result());
}
```

- [ ] **Step 2 — Run, verify fail.** `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "*RunAsync_InstallAgentsFalse*"` → FAIL (member `InstallAgents` missing).

- [ ] **Step 3 — Add `Options.InstallAgents`.** Add `bool InstallAgents = true` to the `Options` record (default true keeps existing callers/tests source-compatible; `SetupCommand` sets it explicitly).

- [ ] **Step 4 — Ordered early-returns in `RunAsync`.** At the top of `RunAsync`, before any `Handle*`/`HandleAgentSkills`/`selected`:
  1. If no supported agent detected (existing check at ~175-177), emit the existing warning and `return new Result();`.
  2. Else if `!options.InstallAgents`, `writeLine("  [dim]· Skipping kcap agent setup[/]")` and `return new Result();`.

- [ ] **Step 5 — Run, verify pass.** Same filter → PASS.

- [ ] **Step 6 — Failing test: no-agents warning.** Assert that with no agents detected and `InstallAgents=true`, `RunAsync` writes the "no supported agent CLI detected" line and returns zero-value `Result`, without prompting.

- [ ] **Step 7 — Run, verify pass** (the relocated warning already satisfies it) — adjust ordering if needed.

- [ ] **Step 8 — Route per-vendor gates.** Replace each primary gate `options.NoPrompt || prompt("Install <vendor> ...?")` with proceeding unconditionally (installation now runs because we passed the early-returns), preserving each `--skip-<vendor>`/per-artifact check unchanged. Fold the `HandleAgentSkills` prompt at ~870 into the same flow (install when reached). Leave the Codex network-access prompt calling `prompt` (it now uses `options.NoPrompt || prompt`, reached only when `codexHooksInstalled`). Keep `SweepLegacyCodexSkills`, `selected` out-params, and all downstream success/`selected` gates exactly as-is — they are now unreachable under `InstallAgents=false` because of Step 4.

- [ ] **Step 9 — Kiro truth-table tests.** Add tests (all with `InstallAgents=true`, Kiro detected, kcap "on PATH" via installer stubs): (a) `SkipKiro` only ⇒ hooks not installed, Kiro MCP + skills installed; (b) `SkipKiro`+`SkipKiroMcp` ⇒ nothing Kiro (skills suppressed via `selected=false`); (c) `SkipKiro`+`SkipKiroSkills` ⇒ MCP installed, skills not.

```csharp
[Test] public async Task Kiro_SkipHooksOnly_KeepsMcpAndSkills() { /* assert result.KiroMcpRegistered && result.KiroSkillsInstalled && !result.KiroHooksInstalled */ }
[Test] public async Task Kiro_SkipHooksAndMcp_InstallsNothing()  { /* assert !KiroMcpRegistered && !KiroSkillsInstalled */ }
[Test] public async Task Kiro_SkipHooksAndSkills_KeepsMcpOnly()  { /* assert KiroMcpRegistered && !KiroSkillsInstalled */ }
```

- [ ] **Step 10 — Shared-skills eligibility tests.** Kiro-only detected ⇒ no `~/.agents/skills` install; Antigravity-only detected ⇒ no `~/.agents/skills` install; Codex-only detected ⇒ shared skills install attempted.

- [ ] **Step 11 — Run all CodingAgentsStep tests, verify pass.**

- [ ] **Step 12 — Commit.** `feat(setup): gate coding-agent install behind single InstallAgents decision`

---

## Task 2: `SetupDecisions` helper + Step 4 unified prompt

**Files:**
- Create: `src/Capacitor.Cli/Commands/SetupDecisions.cs`
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (~204-233 display; build `Options`)
- Test: `test/Capacitor.Cli.Tests.Unit/SetupDecisionsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  static class SetupDecisions {
      // Returns the human-readable detected list (with Kiro annotation) or null when none detected.
      public static string? DetectedAgentsSummary(CodingAgentsStep.DetectedAgents d);
      // The unified install decision. promptYesNo is null in --no-prompt mode.
      public static bool DecideInstallAgents(CodingAgentsStep.DetectedAgents d, bool noPrompt, Func<string,bool> promptYesNo);
  }
  ```

- [ ] **Step 1 — Failing test: no agents ⇒ null summary, no prompt.**

```csharp
[Test] public async Task DecideInstallAgents_NoAgents_NoPromptFalse() {
    var d = new CodingAgentsStep.DetectedAgents();
    var installed = SetupDecisions.DecideInstallAgents(d, noPrompt:false, promptYesNo:_ => throw new Exception());
    await Assert.That(installed).IsFalse();
    await Assert.That(SetupDecisions.DetectedAgentsSummary(d)).IsNull();
}
```

- [ ] **Step 2 — Run, verify fail** (type missing).

- [ ] **Step 3 — Implement `SetupDecisions`.** `DetectedAgentsSummary` returns null when no bool set; else comma-joins friendly names (`Claude Code`, `Codex`, `Cursor`, `Copilot`, `Gemini`, `Kiro`, `Pi`, `OpenCode`, `Antigravity`), appending to Kiro `" (installing sets kcap as your default Kiro agent)"`. `DecideInstallAgents`: if no agent detected return false; else `noPrompt || promptYesNo("Install kcap for these agents (hooks, skills, instructions, MCP)?")`.

- [ ] **Step 4 — Run, verify pass.**

- [ ] **Step 5 — Failing tests: `--no-prompt` and interactive.** `noPrompt:true` + ≥1 detected ⇒ true, `promptYesNo` never called; `noPrompt:false` ⇒ returns the prompt result; Kiro summary contains the annotation.

- [ ] **Step 6 — Run, verify pass.**

- [ ] **Step 7 — Wire into `SetupCommand`.** In Step 4 (~204-233): after building `detected`, print `SetupDecisions.DetectedAgentsSummary(detected)` when non-null; compute `bool installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt, PromptYesNo);` (uses the LOCAL `noPrompt`, before `Options` is constructed). Construct `CodingAgentsStep.Options` with `InstallAgents = installAgents` and `NoPrompt = noPrompt`. Remove the now-dead per-vendor prompt strings' reliance (handled in Task 1).

- [ ] **Step 8 — Build + run setup unit tests, verify pass.**

- [ ] **Step 9 — Commit.** `feat(setup): single detected-agents prompt replaces per-vendor prompts`

---

## Task 3: `AppConfig.SetResolvedState`

**Files:**
- Modify: `src/Capacitor.Cli.Core/.../AppConfig.cs` (near `ResolvedServerUrl`/`ResolvedProfile` ~53-55, 114-151)
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (after save ~439)
- Test: `test/Capacitor.Cli.Tests.Unit/AppConfigResolvedStateTests.cs`

**Interfaces:**
- Produces: `AppConfig.SetResolvedState(string serverUrl, Profile profile)` — sets the exact `ResolvedServerUrl` (normalized, absolute) and `ResolvedProfile` without precedence re-resolution.

- [ ] **Step 1 — Failing test.** Call `SetResolvedState("http://example.test", profile)`; assert `ResolvedServerUrl == "http://example.test"` and `await GetActiveProfileAsync()` returns that profile (not a disk/env value). Include a case with `KCAP_URL` set to a different value in the environment to prove the setter wins.

- [ ] **Step 2 — Run, verify fail.**

- [ ] **Step 3 — Implement `SetResolvedState`.** Assign the static `ResolvedServerUrl` and `ResolvedProfile` fields directly (wrap `profile` in the same `ResolvedProfile` record shape used elsewhere). Do not re-run `ResolveServerUrl`.

- [ ] **Step 4 — Run, verify pass.**

- [ ] **Step 5 — Call after save.** In `SetupCommand` right after the profile config is saved (~439), call `AppConfig.SetResolvedState(serverUrl, savedProfile)` with the normalized `serverUrl` from Step 1 and the just-saved active profile.

- [ ] **Step 6 — Build, verify.**

- [ ] **Step 7 — Commit.** `fix(setup): refresh AppConfig resolved state with exact saved URL + profile`

---

## Task 4: `HandleImport` params, auth baseUrl, auto-skip, chain visibility

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ImportCommand.cs` (`HandleImport` ~566-582; auth call ~583; exclusion loop ~956-975; `ImportChainsAsync`; `ImportSingleSessionAsync`; chain New payload ~2671-2726)
- Test: `test/Capacitor.Cli.Tests.Unit/ImportVisibilityTests.cs`, existing `ImportCommand` tests

**Interfaces:**
- Produces: `HandleImport(..., bool autoSkipExclusions = false, string? defaultVisibility = null)`; `ImportChainsAsync(..., string? defaultVisibility = null)`; `ImportSingleSessionAsync(..., string? defaultVisibility = null)`.

- [ ] **Step 1 — Failing test: chain New stamps default.** Using WireMock, drive an import of a New file-based (Codex/Claude) session with `defaultVisibility:"org_public"`; assert the captured session-start POST body contains `"default_visibility":"org_public"`.

- [ ] **Step 2 — Run, verify fail.**

- [ ] **Step 3 — Add params + thread.** Add `autoSkipExclusions`, `defaultVisibility` to `HandleImport`. Derive `var chainDefaultVisibility = forcePrivate ? null : defaultVisibility;` and pass it into both `ImportChainsAsync` calls (add `string? defaultVisibility = null` param there and on `ImportSingleSessionAsync`). In the New session-start payload (~2671-2726), if `defaultVisibility is not null` set `node["default_visibility"] = defaultVisibility;`.

- [ ] **Step 4 — Run, verify pass.**

- [ ] **Step 5 — Failing test: force-private + failure-after-start.** Drive a New chain session with `forcePrivate:true` + `defaultVisibility:"org_public"`, WireMock returning 500 on the transcript batch (fail after session-start); assert the session-start body has NO `default_visibility` field (orchestrator zeroed it).

- [ ] **Step 6 — Run, verify pass** (already satisfied by `chainDefaultVisibility` derivation).

- [ ] **Step 7 — Bind auth to baseUrl.** Change `CreateAuthenticatedClientAsync()` → `CreateAuthenticatedClientAsync(baseUrl)` at ~583.

- [ ] **Step 8 — Auto-skip exclusions.** In the exclusion loop (~956-975), when `autoSkipExclusions` is true, skip the `Console.ReadLine()` branch entirely and take the auto-skip path (print the "Auto-skipping … (non-interactive)" line). Add a test that with `autoSkipExclusions:true` and excluded repos present, `Console.ReadLine` is never reached (inject/stub or assert via a redirected input that would hang otherwise — prefer a seam: pass a `bool` and assert the auto-skip log line is produced).

- [ ] **Step 9 — Run, verify pass.**

- [ ] **Step 10 — Commit.** `feat(import): default-visibility + auto-skip-exclusions + baseUrl auth on HandleImport`

---

## Task 5: `ImportContext.DefaultVisibility` + routed sources

**Files:**
- Modify: `src/Capacitor.Cli/Commands/IImportSource.cs` (`ImportContext` record)
- Modify: `CursorImportSource.cs`, `CopilotImportSource.cs`, `GeminiImportSource.cs`, `KiroImportSource.cs`, `PiImportSource.cs`, `OpenCodeImportSource.cs`, `AntigravityImportSource.cs`
- Modify: `ImportCommand.cs` (construct `ImportContext` at ~1339 with `DefaultVisibility`)
- Test: `test/Capacitor.Cli.Tests.Unit/ImportVisibilityTests.cs`

**Interfaces:**
- Consumes: `HandleImport.defaultVisibility` (Task 4).
- Produces: `ImportContext.DefaultVisibility` (default `null`).

- [ ] **Step 1 — Failing test (per routed source, New).** For each routed source, drive a New session via its `ImportSessionAsync` with a context `DefaultVisibility:"org_public"`, `ForcePrivate:false`; assert the session-start payload carries `"default_visibility":"org_public"`. Include Cursor explicitly.

- [ ] **Step 2 — Run, verify fail.**

- [ ] **Step 3 — Add record member.** `ImportContext` gains `string? DefaultVisibility = null` (record member with default → existing 3-arg constructions stay source-compatible).

- [ ] **Step 4 — Guarded stamp in each source.** In each source's `ImportSessionAsync` session-start builder, add:
  ```csharp
  if (!ctx.ForcePrivate && classification.Status == ImportStatus.New && ctx.DefaultVisibility is not null)
      node["default_visibility"] = ctx.DefaultVisibility;
  ```
  Do NOT alter each source's existing `ForcePrivate` handling.

- [ ] **Step 5 — Construct with value.** At `ImportCommand.cs:~1339` set `DefaultVisibility = defaultVisibility` on the routed `ImportContext`.

- [ ] **Step 6 — Run, verify pass.**

- [ ] **Step 7 — Failing tests: Partial/AlreadyLoaded ⇒ no default; forcePrivate preserved.** Per routed source: Partial and AlreadyLoaded reassertions carry NO `default_visibility` (with `ForcePrivate:false`); with `ForcePrivate:true`, assert each source's EXISTING behavior is unchanged (Pi/OpenCode/Antigravity still emit `"private"`; Copilot/Gemini/Kiro/Cursor unchanged) and the default is never stamped.

- [ ] **Step 8 — Run, verify pass.**

- [ ] **Step 9 — Commit.** `feat(import): thread Step-3 default visibility through routed sources (New-only, forcePrivate-preserving)`

---

## Task 6: Step 6 import + eligibility + `--skip-import` + header renumber

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (flags ~28-53; headers 95/138/172/205/406; new Step 6 after save/ping; hints ~486-487)
- Modify/extend: `src/Capacitor.Cli/Commands/SetupDecisions.cs` (import eligibility/policy helper)
- Test: `test/Capacitor.Cli.Tests.Unit/SetupDecisionsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // In SetupDecisions:
  public enum ImportOutcome { Skip, Run }
  public record ImportDecision(ImportOutcome Outcome, string? SkipReason);
  // repo resolved? auth satisfied? user opted out? interactive vs no-prompt?
  public static ImportDecision DecideImport(bool hasCurrentRepo, bool authSatisfied,
      bool skipImport, bool noPrompt, Func<bool> promptYesNo);
  ```

- [ ] **Step 1 — Failing tests for `DecideImport`.** No repo ⇒ Skip("no origin remote…"); auth not satisfied ⇒ Skip; `skipImport:true` ⇒ Skip; `noPrompt:true` + repo + auth ⇒ Run (no prompt); interactive + user-yes ⇒ Run; interactive + user-no ⇒ Skip.

- [ ] **Step 2 — Run, verify fail.**

- [ ] **Step 3 — Implement `DecideImport`.** Guard order: `!hasCurrentRepo` → Skip; `!authSatisfied` → Skip; `skipImport` → Skip; `noPrompt` → Run; else `promptYesNo("Import past sessions from this repository?") ? Run : Skip`.

- [ ] **Step 4 — Run, verify pass.**

- [ ] **Step 5 — Parse `--skip-import`.** Add to the flag block (~28-53): `bool skipImport = args.Contains("--skip-import");`.

- [ ] **Step 6 — Renumber headers.** Change the five `Step N/5` rules to `Step N/6` (95, 138, 172, 205, 406).

- [ ] **Step 7 — Add Step 6.** After the save + `PingCliSetupAsync` and before the completion summary: print `Step 6/6 — Import past sessions`. Resolve `currentRepo` via `RepositoryDetection.DetectRepositoryAsync(cwd)` → `(Owner,Name)?`. Compute `authSatisfied` from the login state (provider `None` OR a usable token). Call `SetupDecisions.DecideImport(...)`. On `Run`, call the import runner (Step 8); on `Skip`, print the reason + keep the existing `kcap import` hint.

- [ ] **Step 8 — Import runner behind a seam.** Introduce a `Func<ImportArgs, Task<int>>` (default → real `ImportCommand.HandleImport`) so the decision+invocation is testable. Real call passes: `baseUrl` (normalized), `filterCwd:null`, `filterSession:null`, `minLines:15`, `generateSummaries:false`, `sources:` all nine, `explicitVendorSelection:false`, `since:null`, `scope: ImportScope.Repo(owner,name)`, `skipConfirmation:true`, `forcePrivate:false`, `activeProfile`, `currentRepo:(owner,name)`, `needOrgPick:false`, `storedOrg:null`, `autoSkipExclusions:true`, `defaultVisibility: <Step 3 visibility>`.

- [ ] **Step 9 — Best-effort error handling.** Wrap in try/catch AND inspect the return code: on throw OR non-zero, print a warning + the manual `kcap import` hint, and continue to the summary. Test both (runner throws; runner returns 1).

- [ ] **Step 10 — Run, verify pass.**

- [ ] **Step 11 — Commit.** `feat(setup): Step 6 imports current-repo history (auto on --no-prompt, --skip-import to opt out)`

---

## Task 7: Docs — README + help text

**Files:**
- Modify: `README.md` (getting-started + `setup` under CLI commands)
- Modify: `src/Capacitor.Cli.Core/Resources/help-setup.txt`
- Modify: `src/Capacitor.Cli.Core/Resources/help-usage.txt`

- [ ] **Step 1 — README quick-start + setup section.** Describe: the single agent-install prompt (installs hooks/skills/instructions/MCP for all detected harnesses), the new import step, `--skip-import`, and a prominent warning that `kcap setup --no-prompt` now imports current-repo history.

- [ ] **Step 2 — help-setup.txt.** Same content: single agent prompt, Step 6 import, `--skip-import`, `--no-prompt` behavior-change warning.

- [ ] **Step 3 — help-usage.txt.** Add `--skip-import` where setup flags are listed.

- [ ] **Step 4 — Grep check.** `grep -rn "skip-import\|Step 6\|import past sessions" README.md src/Capacitor.Cli.Core/Resources/help-setup.txt` — confirm present.

- [ ] **Step 5 — Commit.** `docs: README + help text for unified agent install + import step`

---

## Task 8: Full verification

- [ ] **Step 1 — Build.** `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` → 0 errors.
- [ ] **Step 2 — Unit tests.** `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` → all pass.
- [ ] **Step 3 — Integration tests.** `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj` → all pass (re-run any flaky timing test in isolation per memory).
- [ ] **Step 4 — AOT publish.** `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.
- [ ] **Step 5 — Exercise the flow.** Run the setup wizard against a stub/WireMock server (or a dry path) to confirm: single agent prompt appears with the detected list; "no" installs nothing; Step 6 offers import and honors `--skip-import`; `--no-prompt` path does not block.
- [ ] **Step 6 — Commit** any fixups.

## Self-review notes (author)

- Spec coverage: Change 1 → Tasks 1-2; AppConfig refresh → Task 3; import params/auth/auto-skip/chain visibility → Task 4; routed visibility → Task 5; Step 6 + eligibility + flags → Task 6; docs → Task 7; verification (incl. AOT) → Task 8. All spec Decisions 1-12 mapped.
- Types consistent: `InstallAgents`, `SetResolvedState`, `DecideInstallAgents`, `DecideImport`, `ImportContext.DefaultVisibility`, `chainDefaultVisibility`, `autoSkipExclusions` used consistently across tasks.
- The exact line numbers are guides; confirm against the file when editing (the branch may have shifted lines).
