# Local-edition background intelligence, CLI half (Plan C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a local-edition kcap server run its model calls through the user's own coding agent: the daemon answers `RunHeadlessPrompt` with the existing headless runners and advertises which harnesses it can run (PR 1), and setup asks who runs background intelligence (PR 2).

**Architecture:** A daemon service owns a two-slot semaphore and a request-id table; each request runs through a per-vendor `IHeadlessHarness` adapter over `ClaudeCliRunner` / `CodexCliRunner`, and a pure mapper turns the runner's outcome into the wire response. `ServerConnection` registers the two hub methods with fail-closed defaults and puts `HeadlessVendors` on `DaemonConnect`, read off the wired handler.

**Tech Stack:** .NET 10 NativeAOT, SignalR client, TUnit.

**Spec:** kcap-server `docs/superpowers/specs/2026-10-27-ai4515-local-edition-background-intelligence-design.md` (sections 2, 5, 7). Server half: kcap-server `docs/superpowers/plans/2026-10-27-ai4515-local-agent-vendor-server-core.md`, merged as 961efd3d9.

## Global Constraints

- The wire contract is shipped in kcap-server `src/Capacitor.Api.Public.Abstractions/Agents/Headless/` and cannot change: method `RunHeadlessPrompt` (client result) taking `RunHeadlessPromptArgs(string RequestId, string Harness, string Model, string? ReasoningEffort, string? SystemPrompt, string Prompt, string? JsonSchema, int MaxTurns, double? MaxBudgetUsd, DateTimeOffset Deadline)` and returning `HeadlessPromptResponse(bool Started, string? NotStartedReason, string? Text, string? StructuredJson, long? InputTokens, long? OutputTokens, double? CostUsd = null, string? FailureKind = null, string? Error = null)`; one-way `CancelHeadlessPrompt` taking `CancelHeadlessPromptArgs(string RequestId)`
- Daemon non-start reasons: `no_harness`, `binary_missing`, `deadline_passed`. `no_daemon` and `queued` are the server's alone
- Started-run failure kinds: `process_failure`, `deadline`, `unparseable_output`, `spend_budget`, `not_signed_in`. `result_lost` is the server's alone
- A token count is null when the harness reported none; never coerce to zero
- A run whose start cannot be determined counts as started and failed
- Harness tokens are `claude` and `codex`; the server matches them case-insensitively
- At most 2 headless runs in flight per daemon
- A headless run is never recorded as a session: keep `HostedAgent.SkipVar`, the Claude helper-run markers and Codex `--ephemeral`
- `DaemonConnect.HeadlessVendors` is `string[]?`, inserted before `SupportsScratchWorkspace`; null means a daemon predating the field, an empty array means none
- Comments scarce and non-historical; no Linear ids in code
- Commit and PR titles `[AI-4515] <imperative clause>`, at most 80 characters

## Review Focus

- A request that arrives with its deadline already past answers `deadline_passed` without spawning anything (Task 4, `Arriving_past_its_deadline_never_starts`)
- A cancel for an unknown or finished request id is a no-op, never a throw on the hub's receive loop (Task 4, `Cancel_for_an_unknown_id_is_a_no_op`)
- Daemon shutdown while a run is queued or running releases the slot and answers without hanging the hub (Task 4, `Shutdown_ends_a_queued_run`)
- A harness binary removed after boot answers `binary_missing`, not a started failure (Task 4, `Binary_gone_at_start_is_a_non_start`)
- A signed-out Claude is `not_signed_in`, not `process_failure`, and an empty `modelUsage` reports null tokens (Task 2, `Signed_out_envelope_is_not_signed_in`)

## Codex verification (shipping criterion)

Verified 2026-10-31 against codex-cli 0.166.0 and Claude Code 2.1.316 on macOS arm64, using each runner's exact argument list:

- Codex: `--output-schema` produced a schema-shaped last message in one turn, exit 0; the `--json` stream reported `turn.completed` usage (input 18829, output 31). Codex has no turn cap; the deadline bounds the run
- Claude: `--json-schema` with `--max-turns 2` answered through `structured_output` in exactly 2 turns, with `modelUsage` and `total_cost_usd`; at `--max-turns 1` the schema turn has no room, which is why the server sends 2
- Claude signed out (empty `CLAUDE_CONFIG_DIR`): exit 1, `is_error: true`, `result: "Not logged in · Please run /login"`, `modelUsage: {}`

Both pass, so the daemon advertises both. The server's `LocalAgentHarnesses.All` lists only `claude`; offering Codex needs a server change (the harness plus Codex model defaults per options class), out of this repository.

---

## PR 1: the daemon runs headless prompts

### Task 1: Wire records and the connect field

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs` (records beside the eval-dispatch records; `[JsonSerializable]` beside `CancelEvalCommand`; `DaemonConnect`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/` wire round-trip test beside the existing `Models` serialization tests

**Interfaces:**
- Produces: `public readonly record struct RunHeadlessPromptArgs(...)`, `HeadlessPromptResponse(...)`, `CancelHeadlessPromptArgs(string RequestId)` with the server's exact shapes; `DaemonConnect.HeadlessVendors` (`string[]?`, default null). The reason and kind strings are constants on the daemon's `HeadlessPromptResponses` (Task 3), the only place that writes them

- [ ] Failing test: `HeadlessPromptResponse` serialized through the source-generated context round-trips with null token counts staying null; `DaemonConnect` with `HeadlessVendors: ["claude"]` serializes the field as `HeadlessVendors`
- [ ] Add the records, constants, `[JsonSerializable]` entries and the parameter; test passes
- [ ] Commit

### Task 2: Runners report usage and classify Claude's sign-in and quota failures

**Files:**
- Modify: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeCliRunner.cs`, `src/Capacitor.Cli.Core/Harness/Codex/CodexCliRunner.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/` and `.../Codex/` runner tests

**Interfaces:**
- Produces: `ClaudeCliOutcome.Usage` means "what the run reported, null when it reported nothing", set on success and failure by both runners. `ClaudeCliRunner.ReportedUsage(string stdout) -> ClaudeCliUsage?` (null when the envelope has no non-empty `modelUsage`). `ClassifyFailure` returns `NotSignedIn` for an `is_error` envelope whose `api_error_status` is 401 or whose `result` mentions `/login`, and `SpendBudget` for `error_max_budget_usd` or a `result` naming a usage limit

- [ ] Failing tests: `Signed_out_envelope_is_not_signed_in` (the verified signed-out envelope above → `NotSignedIn`, `Usage` null); `Usage_limit_envelope_is_spend_budget`; `Reported_usage_sums_model_usage` (two models → summed fields, cost from `total_cost_usd`); `Empty_model_usage_reports_null`
- [ ] Implement; the Codex success return also carries `events.Usage`, and `ClaudeCliRunner` kills through `ProcessTree.Kill` instead of the runtime's tree kill, which stops each process first and is banned in the daemon
- [ ] Commit

### Task 3: Outcome mapping

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/HeadlessPromptResponses.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/HeadlessPromptResponsesTests.cs`

**Interfaces:**
- Produces: `static HeadlessPromptResponse HeadlessPromptResponses.From(ClaudeCliOutcome outcome, bool structured)`; `NotStarted(string? reason, string? error)`; `Failed(string kind, string error)`

Mapping: success → `Started`, `StructuredJson` when `structured` else `Text`; `InputTokens` = input + cache read + cache write; tokens and cost from `outcome.Usage`, null when it is null (Claude's `CostUsd` falls back to the result's). Failure → `Timeout→deadline`, `ProcessFailure→process_failure`, `OutputUnparseable→unparseable_output`, `SpendBudget→spend_budget`, `NotSignedIn→not_signed_in`, usage kept.

- [ ] Failing tests: one per failure kind; `Success_with_schema_fills_structured_json`; `Unreported_usage_stays_null`; `Input_tokens_include_cache`
- [ ] Implement; commit

### Task 4: The headless prompt service

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/IHeadlessHarness.cs`, `src/Capacitor.Cli.Daemon/Services/HeadlessPromptRunner.cs`, `src/Capacitor.Cli.Daemon/Harness/Claude/ClaudeHeadlessHarness.cs`, `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHeadlessHarness.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/HeadlessPromptRunnerTests.cs`

**Interfaces:**
- Produces: `interface IHeadlessHarness { string Harness { get; } bool IsAvailable(); Task<ClaudeCliOutcome> RunAsync(RunHeadlessPromptArgs args, TimeSpan timeout, CancellationToken ct); }`; `HeadlessPromptRunner(IEnumerable<IHeadlessHarness>, TimeProvider, CancellationToken shutdown, ILogger)` with `Task<HeadlessPromptResponse> RunAsync(RunHeadlessPromptArgs)`, `Task CancelAsync(CancelHeadlessPromptArgs)`, `string[] AdvertisedHarnesses()` (available ones, ordinal-sorted), `const int MaxConcurrent = 2`
- Claude adapter: `ClaudeCliRunner.RunDetailedAsync` with `promptViaStdin: true`, the request's model, max turns, schema, budget and system prompt. Codex adapter: `CodexCliRunner.RunDetailedAsync` with the model, `ReasoningEffort ?? "low"`, schema and system prompt. `IsAvailable` is `harnesses.ResolveLaunchable(id) is not null`

Behaviour: unknown harness → `no_harness`; deadline already past on arrival or on taking a slot → `deadline_passed`; harness unavailable on taking a slot → `binary_missing`; the slot wait is bounded by the deadline and linked to cancel and shutdown; the run's timeout is `Deadline - now`; cancel or shutdown while queued → non-start with null reason; cancel while running → the runner kills the process, answer `process_failure` "cancelled"; the slot and the id are released in `finally`.

- [ ] Failing tests with a fake `IHeadlessHarness` and `FakeTimeProvider`: `Arriving_past_its_deadline_never_starts`, `Deadline_passing_while_queued_is_a_non_start`, `Cancel_queued_never_starts_it`, `Cancel_running_cancels_the_harness_token`, `Third_request_waits_for_a_slot`, `Cancel_for_an_unknown_id_is_a_no_op`, `Shutdown_ends_a_queued_run`, `Binary_gone_at_start_is_a_non_start`, `Unknown_harness_is_no_harness`, `Run_timeout_is_the_time_left_to_the_deadline`
- [ ] Implement; commit

### Task 5: Hub registration and advertisement

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ServerConnectionAdvertisementTests.cs`, a `ClaudeHeadlessHarness`/`CodexHeadlessHarness` availability test

**Interfaces:**
- Consumes: Task 4's `HeadlessPromptRunner`
- Produces: `ServerConnection.RunHeadlessPromptHandler` (`Func<RunHeadlessPromptArgs, Task<HeadlessPromptResponse>>?`, default answers a non-start with null reason), `CancelHeadlessPromptHandler` (`Func<CancelHeadlessPromptArgs, Task>?`), `HeadlessVendors` (`Func<string[]>?`). `BuildDaemonConnect` sends `HeadlessVendors` as `[]` when no handler is wired, else the advertised list

- [ ] Failing tests: `DaemonConnect_without_a_headless_handler_advertises_none` (empty, not null); `DaemonConnect_advertises_the_headless_harnesses_once_wired`; `Headless_harness_is_available_only_when_its_binary_resolves` (registry over `TestBinaries.Searching(tmp, "claude")` vs `TestBinaries.None`)
- [ ] Implement; register the two adapters and the runner in `DaemonRunner`, instantiate it beside `EvalRunner`
- [ ] `CodexHeadlessHarnessTests`: an empty `Model` (the server's tier default for Codex) passes no `-m`; a null effort runs at `low`
- [ ] `ServerConnectionHeadlessWireTests`: over a real hub, a `CancelHeadlessPrompt` reaches the daemon while its `RunHeadlessPrompt` is pending, and the answer round-trips
- [ ] Run the Core and Daemon unit suites, `dotnet publish` for IL warnings; commit

### Task 6: End-to-end check

Done 2026-10-31: the published v1.4.1-beta.2 local bundle run in the foreground (isolated `KCAP_LOCAL_ROOT`, `Evals__ServerRunner__LocalAgent__Harness=claude`) with a daemon built from this branch; one imported Claude session was titled through three `RunHeadlessPrompt` client results, and the server logged no `did not start`. A root path must stay short: the Postgres socket path is capped at 103 bytes on macOS

- [x] Run a local-edition kcap-server with `Evals:ServerRunner:LocalAgent:Harness=claude`, a daemon from this branch against it, request a manual eval or title, and confirm the log no longer repeats `EvalQueue: the local agent did not start <runId>: no_daemon`. Record the result, or why it could not be run, in the PR

## PR 2: setup asks who runs background intelligence

**Server contract:** `POST /api/admin/intelligence`, kcap-server `src/Capacitor.Api.Public.Abstractions/Setup/`. Request `{mode, harness?, vendor?, model?, api_key?, import_outcomes?}`; response `{mode, harness, vendor, import_outcomes, backlog_session_touches_per_day, resolving}`; a refusal is 400 with `{"error": "<code>"}` (`unknown_mode`, `not_offered`, `unknown_harness`, `unknown_vendor`, `key_required`, `model_required`, `key_rejected: …`, `key_pinned_above_store: …`, `import_outcomes_pinned_above_store`). The local edition authenticates nobody, so the CLI posts over the loopback lane with no bearer.

**Answers:** one token names the whole answer: `claude` or `codex` (the local agent), `anthropic`, `openai`, `openrouter` or `cohere` (a provider), `none`. A provider's key is read from its own variable (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, `COHERE_API_KEY`) or a hidden prompt, never from argv. OpenAI has no server default model, so without `--model` the CLI sends `gpt-5-mini`; the other vendors send no model and run the server's default.

### Task 7: Core contract and probe

**Files:** Create `src/Capacitor.Cli.Core/Setup/Intelligence/` (`IntelligenceAnswer.cs`, `IntelligenceSetupRequest.cs`, `IntelligenceSetupResponse.cs`, `IntelligenceSetupOutcome.cs`, `IntelligenceSetupClient.cs`, `HeadlessHarnessProbe.cs`); modify `Models.cs` (`[JsonSerializable]`). Tests beside them in `test/Capacitor.Cli.Core.Tests.Unit/Setup/Intelligence/`.

- [ ] Failing tests: an answer token parses to its mode, harness or vendor and refuses anything else; the request serializes snake_case; the client maps 200 to applied, 400 `{"error"}` to refused with the code, anything else to failed (WireMock); the probe lists `claude`/`codex` only where `LaunchCommand` resolves, ordinal-sorted
- [ ] Implement; commit

### Task 8: The daemon reports its headless harnesses locally

**Files:** `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs` (`DaemonInfoDto.HeadlessVendors`, trailing), `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs`, `ServerConnection.cs` (`AdvertisedHeadlessVendors()`, shared with `BuildDaemonConnect`), `LocalControlCapabilities.cs` (`headless/1`). Tests: the status snapshot carries the list once the handler is wired and `[]` before; the capability pin.

- [ ] Failing tests, implement, commit

### Task 9: `kcap local intelligence`

**Files:** Create `src/Capacitor.Cli/Commands/LocalIntelligenceCommand.cs`, `LocalIntelligenceArgs.cs`; modify `LocalCommand.cs`, `help-local.txt`.

`kcap local intelligence <answer> [--model <model>] [--import-outcomes | --no-import-outcomes] [--json]` posts to the active local server. Exit 0 applied, 2 refused by the server or by the arguments, 1 unreachable or no local server. `--json` prints one line: `{"ok":true,"mode",…,"import_outcomes","backlog_session_touches_per_day","resolving"}` or `{"ok":false,"error"}`. A provider with no key in its variable prompts when interactive and not `--json`, else refuses naming the variable.

- [ ] Failing tests: argument parsing (unknown answer, `--model` without a value, both outcome flags, a key-looking argument refused); key resolution (variable, prompt, refusal); the JSON line for each outcome
- [ ] Implement; commit

### Task 10: `kcap setup --local` asks

**Files:** `LocalSetupArgs.cs` (`--intelligence`, `--intelligence-model`, `--import-outcomes`, `--no-import-outcomes`), `SetupCommand.cs` (a step between the daemon and the import), a `LocalIntelligenceStep.cs` holding the question; `help-setup.txt`.

- Interactive: one choice per harness the machine runs headlessly (the daemon's `headless_vendors` when it is connected and advertises `headless/1`, the local probe before), "A provider, with my API key", "Not now". A provider asks the vendor, the model (defaulted, required for OpenAI) and the key. Any answer but not now then asks "Also summarise what each imported session achieved?" (default yes). A refusal is shown and the question asked again; an unreachable server warns and moves on
- `--no-prompt` (or `--json --no-prompt`) without `--intelligence` is refused at parse time, before anything runs
- The import step drops the naming question once a model resolves titles, and prints the cost line: "Background work on N sessions runs up to B a day: about D days"

- [ ] Failing tests: parse refusals; the step's decision table (flag answer, interactive, cannot ask); the import step skips naming when titles resolve; the cost line's arithmetic
- [ ] Implement; commit

### Task 11: The desktop wizard step

**Files:** `WizardStep.cs` (`Intelligence` between `Harnesses` and `Import`), create `ViewModels/Onboarding/IntelligenceStepViewModel.cs`, `IntelligenceOption.cs`, `Views/Onboarding/IntelligencePaneView.axaml`; `KcapCli.cs` (`LocalIntelligenceAsync`, the key in the child's environment), `WizardComposition.cs`, `OnboardingPaneView.axaml`.

- Passes through off the local path. Radios: one per harness ticked on the Harnesses page that the machine runs headlessly, a provider (vendor, model defaulted, key), not now. Next posts; a refusal stays on the page with the server's reason, an unreachable server too
- [ ] Failing tests: the offered harnesses (ticked ∩ headless, daemon list over probe); Next posts the answer and holds on a refusal; the key never reaches the argument list
- [ ] Implement; commit

### Task 12: The Import step follows the answer

**Files:** `HistoryStepViewModel.cs`, `HistoryPaneView.axaml`, `WizardComposition.cs`.

- Titling is no longer offered once the answer resolves titles; "Also summarise what each session achieved" shows when a model is configured, on by default, and is written through the same route before the import starts; a cost line gives days at the daily budget for the selected sessions
- [ ] Failing tests; implement; commit

### Task 13: Docs, suites, publish, end-to-end

- [ ] README (`## Getting started` and the `setup` and `local` sections), help text
- [ ] Core, Cli and App suites; `dotnet publish -c Release` with no IL2026/IL3050
- [x] End-to-end against an isolated local server (`KCAP_LOCAL_ROOT` under a short `/private/tmp` path, `Local__Port`): `kcap local intelligence claude --json` and `none`, and a refused provider key; record the result in the PR
