# Unified agent install + repo import in `kcap setup`

- **Date:** 2026-07-20
- **Status:** Approved design, pre-implementation
- **Branch:** `worktree-import-and-unified-install`

## Summary

Two changes to the `kcap setup` onboarding wizard, both using the existing
default-yes `ConfirmationPrompt` style already used elsewhere in the flow:

1. **One install prompt.** Replace the current nine per-vendor "Install X
   hooks?" prompts with a single default-yes prompt that installs all kcap
   artifacts (hooks + skills + instructions + MCP) for **every detected coding
   agent harness**.
2. **A final import step.** After configuration is saved, offer (default yes)
   to import past sessions for the **current repository** across all detected
   harnesses, reusing the existing `kcap import` machinery.

## Current state (what exists today)

- The install flow **is** `kcap setup` — a five-step wizard in
  `src/Capacitor.Cli/Commands/SetupCommand.cs` (`HandleAsync`, line 21). Steps:
  1/5 Server, 2/5 Login, 3/5 Default session visibility, 4/5 Coding agents,
  5/5 Agent Daemon. Headers are `new Rule("[yellow]Step N/5 — Title[/]")`
  (lines 95, 138, 172, 205, 406).
- **Step 4 (Coding agents)** is orchestrated by
  `CodingAgentsStep.RunAsync` (`src/Capacitor.Cli/Commands/CodingAgentsStep.cs`,
  lines 125-209). It runs a dedicated `Handle*` method per vendor and asks a
  **separate yes/no per vendor** for that vendor's primary (hooks) artifact,
  e.g.:
  - Claude — `prompt("Install Claude Code plugin (hooks, skills, memory)?")` (1352)
  - Codex — `prompt("Install Codex CLI hooks?")` (928)
  - Cursor (1059), Copilot (378), Gemini (438), Kiro (258), Pi (502),
    OpenCode (622), Antigravity (684)
  - plus a final shared `prompt("Install kcap agent skills?")` (870).
  Each gate is `options.NoPrompt || prompt(...)`. MCP registration and agent
  instructions are **not** separately prompted — they are non-destructive and
  auto-applied when a vendor is selected, gated only by their `--skip-*` flags.
- Two other prompts live in/around Step 4:
  - **Codex network access** — an extra `prompt(...)` inside the Codex handler
    (`CodingAgentsStep.cs:976`).
  - **Provider API keys** — whether to retain `ANTHROPIC_API_KEY` /
    `OPENAI_API_KEY` for headless daemon spawns (`SetupCommand.cs:360-401`),
    shown only when those env vars are set.
- The yes/no helper is a local function in `SetupCommand.HandleAsync`:
  `bool PromptYesNo(string text) => AnsiConsole.Prompt(new ConfirmationPrompt(text) { DefaultValue = true });`
  (`SetupCommand.cs:352-353`), threaded into `RunAsync` as the `prompt` delegate.
  There is no shared prompt-utility module; `ConfirmationPrompt` is constructed
  inline per call.
- **Nine supported harnesses:** claude, codex, cursor, copilot, gemini, kiro,
  pi, opencode, antigravity. Install-time detection builds a
  `CodingAgentsStep.DetectedAgents` record (config-dir presence **OR** PATH
  probe) at `SetupCommand.cs:210-233`.
- **A full `kcap import` command already exists.** Orchestrator:
  `ImportCommand.HandleImport(...)`
  (`src/Capacitor.Cli/Commands/ImportCommand.cs:566-582`). It is multi-vendor
  (all nine `IImportSource` implementations, constructed in
  `Program.cs:547-557`), scope-based (`ImportScope.All` / `.Org(owner)` /
  `.Repo(owner, name)` — `ImportScope.cs:6-9`), streaming with its own Spectre
  progress UI, blocks until done, and is idempotent via a server-side
  watermark. `HandleImport` is coupled to console I/O (it renders its own
  phases and prompts), so embedding it means it draws its own progress.
- Today setup only **suggests** import at the very end via a text hint:
  `Optional: import past sessions with kcap import --org` (`SetupCommand.cs:487`).
- `kcap setup` parses a large flag set including `--no-prompt`, `--device`, and
  per-vendor / per-artifact `--skip-*` flags (`SetupCommand.cs:28-53`).

## Goals

- Ask **once** whether to install kcap agent artifacts, and on yes install them
  for every detected harness.
- Add a **final step** that offers to import the current repo's past sessions
  across all detected harnesses.
- Match the existing default-yes `ConfirmationPrompt` prompt style.
- Reuse the existing per-vendor installers (`CodingAgentsStep` `Handle*`) and
  the existing importer (`ImportCommand.HandleImport`) — no rewrite of either.

## Non-goals

- No change to *what* each vendor's installer writes (hooks/MCP/instructions/
  skills payloads, marker files, self-healing logic).
- No change to the standalone `kcap plugin` or `kcap import` command surfaces
  (beyond setup calling into `HandleImport`).
- No change to detection logic or the set of supported harnesses.
- No new "import everything on disk" scope — import is scoped to the current
  repo (see Decisions).

## Decisions (resolved during design)

1. **Import scope on "yes" = current repo only** (`ImportScope.Repo`, i.e.
   `kcap import --repo .` semantics). Not `--all`, not `--org`.
2. **The single install prompt covers artifacts only.** The Codex
   network-access and provider-API-key opt-ins remain as their own separate
   prompts (distinct security/privacy decisions, not install artifacts).
3. **Approach B** for restructuring Step 4: one explicit `InstallAgents`
   decision threaded into `Options`, reusing every existing `Handle*` method.
   (Rejected: Approach A — the `NoPrompt` seam alone cannot express "no";
   Approach C — a full rewrite via `PluginCommand` throws away tested seams.)
4. **Import is offered regardless** of the Step 4 answer. Importing past
   sessions is independent of installing agent configs.
5. **`--no-prompt` auto-imports** the current repo (consistent default-yes),
   subject to the import gates below. Opt out with a new `--skip-import` flag
   (mirrors the `--skip-<vendor>` convention).

## Change 1 — Step 4 "Coding agents": one install prompt

### Behavior

- After detection (`SetupCommand.cs:210-233`), print the detected harnesses in
  human-readable form, e.g.:
  ```
  Detected coding agents: Claude Code, Codex, Cursor
  ```
- Ask one default-yes prompt:
  ```
  Install kcap for these agents (hooks, skills, instructions, MCP)?
  ```
- The result becomes a single `InstallAgents` boolean.

### Implementation

- Add `bool InstallAgents` to `CodingAgentsStep.Options`.
- In each `Handle*`, change the primary-artifact gate from
  `options.NoPrompt || prompt("Install <vendor> ...?")` to `options.InstallAgents`,
  still AND-ed with that vendor's existing `--skip-<vendor>` flag. Detected but
  `--skip-<vendor>`-flagged harnesses are still not installed even when
  `InstallAgents` is true.
- The shared `prompt("Install kcap agent skills?")` gate
  (`CodingAgentsStep.cs:870`) folds into the same `InstallAgents` decision.
- **Kept as separate prompts:**
  - Codex network access (`CodingAgentsStep.cs:976`) — reached only when
    `InstallAgents` is true **and** Codex is detected and not `--skip`-ped.
  - Provider API keys (`SetupCommand.cs:360-401`) — unchanged and independent;
    still shown when the relevant env vars are set, regardless of
    `InstallAgents`.
- The `prompt` delegate stays wired for those two remaining prompts; only the
  nine per-vendor gates and the agent-skills gate stop calling it.

### Edge cases

- **No agents detected:** skip the install prompt entirely; keep the existing
  "no supported agent CLI detected" warning (`CodingAgentsStep.cs:175-177`).
- **`--no-prompt`:** `InstallAgents = true` — install for all detected harnesses
  (honoring `--skip-<vendor>`), preserving current scripted behavior.
- **User answers no:** no artifacts installed for any harness; the Codex
  network-access prompt is not reached; the provider-key prompt still shows if
  applicable.

## Change 2 — New Step 6 "Import past sessions"

### Placement & numbering

- Insert after config save + `PingCliSetupAsync` (`SetupCommand.cs:423-446`) and
  **before** the completion summary (`SetupCommand.cs:448-465`).
- Renumber the five existing headers from `/5` to `/6` (lines 95, 138, 172,
  205, 406) and add `Step 6/6 — Import past sessions`.

### Gate (all must hold to offer the step)

- A git repository was detected (`gitRoot != null` — already resolved in
  `HandleAsync`).
- The user is authenticated (auth provider ≠ `None`, i.e. login was not
  skipped) — import uploads to the server.

If either fails: skip the step and keep the existing `kcap import` text hint
(`SetupCommand.cs:487`).

### Prompt

Default-yes:
```
Import past sessions from this repository?
```
Offered **regardless** of the Step 4 install answer.

### On "yes" — call the existing importer

Invoke `ImportCommand.HandleImport(...)` with:

- `sources`: all nine `IImportSource` instances (as constructed in
  `Program.cs:547-557`).
- `scope: ImportScope.Repo(owner, name)` for the current repo — reusing the same
  current-repo resolution the `kcap import --repo .` path uses (derive
  owner/name from the git remote; do not reimplement).
- `currentRepo: (owner, name)` to match.
- `skipConfirmation: true` — the yes/no already served as confirmation.
- `explicitVendorSelection: false` — import every available harness (the
  importer's own `IsAvailable` session-data probe naturally restricts to
  harnesses with data on disk).
- `baseUrl` and `activeProfile` from the values already resolved in Steps 1-3.
  Imported sessions inherit the profile's default visibility chosen in Step 3
  (no extra visibility argument).

`HandleImport` renders its own Discovering / Plan / Importing / Done progress UI.

### Non-interactive behavior

- **`--no-prompt`:** auto-import the current repo (default-yes), subject to the
  gates above (git repo present + authenticated).
- **`--skip-import`:** new flag to opt out of the import step in scripted
  contexts. Parsed alongside the other `--skip-*` flags in
  `SetupCommand.cs:28-53`.

### Error handling

Import is **best-effort**, like the existing `PingCliSetupAsync` call: wrap the
`HandleImport` invocation in try/catch. On failure, print a warning plus the
manual `kcap import` hint and continue to the completion summary — never fail
`kcap setup` because import failed.

## Testability

- **Change 1** is directly unit-testable: `CodingAgentsStep` already takes
  injected I/O delegates (`prompt`, `writeLine`, installer funcs). Add tests:
  - unified-yes installs all detected harnesses;
  - unified-no installs none;
  - `--skip-<vendor>` honored under unified-yes;
  - Codex network-access prompt reached only when Codex detected + unified-yes;
  - no agents detected → warning shown, no install prompt.
- **Change 2:** inject the import invocation behind a delegate (mirroring the
  existing installer delegates) so the **gating logic** — git repo present,
  authenticated, `--no-prompt`, `--skip-import` — is unit-testable without
  running a real import. The real `HandleImport` sits behind that seam.
- Verify no `IL3050`/`IL2026` AOT warnings via
  `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release`
  (build alone does not surface these).

## Documentation (same PR)

Per `CLAUDE.md` and the `vendor-surface-sync` memory, update user-facing docs in
the same PR:

- `README.md` — the getting-started/quick-start walkthrough and the `setup`
  entry under CLI commands: describe the single agent-install prompt and the
  new import step + `--skip-import` flag.
- `src/Capacitor.Cli.Core/Resources/help-setup.txt` — reflect the single agent
  prompt, the import step, and `--skip-import`.
- `src/Capacitor.Cli.Core/Resources/help-usage.txt` — if it documents setup
  flags, add `--skip-import`.

## Files touched

- `src/Capacitor.Cli/Commands/SetupCommand.cs` — detected-harness display +
  single install prompt; `InstallAgents` wiring; new Step 6 (gate, prompt,
  `HandleImport` call behind an injectable seam); `--skip-import` parsing;
  renumber step headers to `/6`.
- `src/Capacitor.Cli/Commands/CodingAgentsStep.cs` — add `Options.InstallAgents`;
  collapse the nine per-vendor gates and the agent-skills gate into that single
  decision; leave the Codex network-access prompt and its reachability intact.
- `src/Capacitor.Cli.Core/Resources/help-setup.txt`,
  `src/Capacitor.Cli.Core/Resources/help-usage.txt`, `README.md` — docs.
- `test/Capacitor.Cli.Tests.Unit/` — unit tests for both changes (and
  integration coverage if a natural seam exists).

## Rollout / compatibility notes

- `kcap plugin install|remove --<vendor>` and `kcap import` command surfaces are
  unchanged.
- Existing `--skip-<vendor>` and per-artifact `--skip-*` flags continue to work
  and now compose with the single `InstallAgents` decision.
- The npm postinstall refresh path (`plugin install --if-installed`) is
  unaffected — it does not run full `kcap setup`.
