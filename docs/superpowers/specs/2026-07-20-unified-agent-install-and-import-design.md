# Unified agent install + repo import in `kcap setup`

- **Date:** 2026-07-20
- **Status:** Approved design, pre-implementation (revised after spec-review round 1)
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
  (lines 95, 138, 172, 205, 406). `HandleAsync` is a single ~470-line method
  with signature `public static async Task<int> HandleAsync(string[] args)` and
  **no injected I/O seam** — every prompt/read/write is inline against
  `AnsiConsole` / `AppConfig` / `TokenStore` / `HttpClientExtensions`.
- **Step 4 (Coding agents)** is orchestrated by `CodingAgentsStep.RunAsync`
  (`src/Capacitor.Cli/Commands/CodingAgentsStep.cs:125-209`), which **is** built
  for testing: it takes injected `Func<string,bool> prompt` and
  `Action<string> writeLine` delegates plus installer delegates. It runs a
  dedicated `Handle*` per vendor and asks a **separate yes/no per vendor** for
  that vendor's primary (hooks) artifact:
  - Claude (1352), Codex (928), Cursor (1059), Copilot (378), Gemini (438),
    Kiro (258), Pi (502), OpenCode (622), Antigravity (684), plus a final
    shared `prompt("Install kcap agent skills?")` (870).
  - Each gate is `options.NoPrompt || prompt(...)`.
- **Per-vendor downstream artifacts are gated with deliberate distinctions**
  (verified against the code — these MUST be preserved):
  - Codex network-access requires hooks success (`:966 !codexHooksInstalled`);
    Codex MCP requires hooks success (`:1017`).
  - Cursor MCP (`:1106`), Copilot MCP (`:1140`) + instructions (`:1174`),
    OpenCode MCP (`:1277`) + instructions (`:1310`) require the primary write
    succeeding (`!*HooksInstalled` / `!*ExtensionInstalled`).
  - Gemini MCP requires hook success (`:1209`), but Gemini **instructions** use
    a `selected` out-param (`:1244`) that stays true even if the hook write
    fails (`:455`).
  - Kiro / Pi / Antigravity downstream use `selected` out-params (Kiro MCP `:293`,
    skills `:327`; Pi MCP `:550`, instructions `:578`; Antigravity MCP `:729`,
    instructions `:760`, skills `:792`) — these stay true even when the primary
    hook write fails (`:520`, `:701`).
- **Mutations that happen without/around the prompt** (verified — these are the
  reason a naive gate swap is insufficient):
  - `HandleAgentSkills` checks "already current" and early-returns **before** the
    prompt (`:853-862`), and on that fast path calls `SweepLegacyCodexSkills`
    (`:859`) which **deletes** legacy Codex skills. It also runs the sweep after
    a successful install (`:890`). The sweep is gated only on `detected.Codex`.
  - `HandleKiroHooks` handles `SkipKiro` **before** the prompt by setting
    `selected = true` and returning (`:251-256`) — so `--skip-kiro-hooks` still
    registers Kiro MCP + installs Kiro skills. Its prompt string discloses:
    `"Install Kiro CLI hooks? (clones your default agent and sets it as default)"`.
  - The shared `~/.agents/skills` install is gated only on **detection** of a
    non-Claude agent (`:842-848`), independent of any `--skip-<vendor>` flag
    (Kiro + Antigravity are excluded from that detection set — they use their
    own dirs).
- Two other prompts live in/around Step 4: **Codex network access** (an extra
  `prompt(...)` inside the Codex handler, `:966+`) and **provider API keys**
  (`SetupCommand.cs:360-401`, shown only when `ANTHROPIC_API_KEY`/`OPENAI_API_KEY`
  are set; independent of agent installs).
- **Nine supported harnesses:** claude, codex, cursor, copilot, gemini, kiro,
  pi, opencode, antigravity. Install-time detection builds a
  `CodingAgentsStep.DetectedAgents` record (config-dir presence **OR** PATH
  probe) at `SetupCommand.cs:210-233`.
- **A full `kcap import` command exists.** Orchestrator:
  `ImportCommand.HandleImport(...)` (`ImportCommand.cs:566-582`) — signature:
  ```csharp
  Task<int> HandleImport(string baseUrl, string? filterCwd, string? filterSession = null,
    int minLines = 15, bool generateSummaries = false, IReadOnlyList<IImportSource>? sources = null,
    bool explicitVendorSelection = false, DateOnly? since = null, ImportScope? scope = null,
    bool skipConfirmation = false, bool forcePrivate = false, string activeProfile = "default",
    (string Owner, string Name)? currentRepo = null, bool needOrgPick = false, string? storedOrg = null)
  ```
  It is multi-vendor (all nine `IImportSource`, built in `Program.cs:547-557`),
  scope-based (`ImportScope.All`/`.Org(owner)`/`.Repo(owner,name)`), streaming
  with its own Spectre progress UI, blocks until done, idempotent via a
  server-side watermark, and **returns an exit code** (several expected failures
  return `1` without throwing). It renders its own phases and, importantly, has
  its own console interactions beyond the confirmation prompt (see the
  exclusion-prompt hazard in Change 2).
- The standalone command enforces non-interactive safety via
  `ImportScopeArgs.Resolve` (`ImportScopeArgs.cs:55-148`): a non-interactive run
  (`!Console.IsInputRedirected && !Console.IsOutputRedirected` is false) requires
  **both** an explicit scope **and** `--yes`; `--repo .` requires
  `currentRepo` resolved from a parseable origin remote, else it errors with
  `"--repo . requires the current directory to be in a git repo with an origin remote."`.
- **`AppConfig` resolves server/profile ONCE, pre-dispatch.** `Program.cs:61`
  calls `AppConfig.ResolveServerUrl(args, ...)` before the dispatch switch, and
  `AppConfig.ResolvedServerUrl` / `AppConfig.ResolvedProfile` are process-global
  cached snapshots (`AppConfig.cs:53-55`). `GetActiveProfileAsync()` **prefers**
  the cached `ResolvedProfile.Profile` (`:348-352`).
  `HttpClientExtensions.CreateAuthenticatedClientAsync(string? baseUrl = null)`
  resolves auth against `baseUrl ?? AppConfig.ResolvedServerUrl ?? $KCAP_URL ?? localhost`
  (`:40`). `HandleImport` calls `CreateAuthenticatedClientAsync()` **without**
  `baseUrl` (`ImportCommand.cs:583`), so post-setup it would use the stale
  pre-setup snapshot unless that snapshot is refreshed.
- Today setup only **suggests** import at the very end via a text hint
  (`SetupCommand.cs:487`). `kcap setup` parses `--no-prompt`, `--device`, and
  per-vendor / per-artifact `--skip-*` flags (`SetupCommand.cs:28-53`).

## Goals

- Ask **once** whether to install kcap agent artifacts, and on yes install them
  for every detected harness (per each vendor's existing skip-flag semantics).
- Add a **final step** that offers to import the current repo's past sessions
  across all detected harnesses.
- Match the existing default-yes `ConfirmationPrompt` prompt style.
- Reuse the existing per-vendor installers (`CodingAgentsStep` `Handle*`) and
  the existing importer (`ImportCommand.HandleImport`) — no rewrite of either.

## Non-goals

- No change to *what* each vendor's installer writes (payloads, marker files,
  self-healing logic) beyond adding one non-interactive option to the importer.
- No change to the standalone `kcap plugin` command surface.
- No change to detection logic or the set of supported harnesses.
- No new "import everything on disk" scope — import is scoped to the current
  repo.

## Decisions (resolved during design + spec-review)

1. **Import scope on "yes" = current repo only** (`ImportScope.Repo(owner,name)`,
   i.e. `kcap import --repo .` semantics).
2. **The single install prompt covers artifacts only.** The Codex network-access
   and provider-API-key opt-ins remain separate prompts.
3. **Approach B** for restructuring Step 4: one explicit `InstallAgents`
   decision threaded into `Options`, reusing every existing `Handle*`.
4. **Import is offered regardless** of the Step 4 answer.
5. **`--no-prompt` auto-imports the current repo — a deliberate behavior
   change.** Existing unattended `kcap setup --no-prompt` invocations will begin
   uploading current-repo history. This is intentional; mitigations: prominent
   help/README warnings, rollout notes (below), a `--skip-import` opt-out, and an
   acceptance test pinning the new scripted behavior. (Considered and rejected:
   opt-in `--import`.)
6. **`InstallAgents = false` performs ZERO artifact mutations**, and the
   no-agents case is a distinct path. Ordering inside `RunAsync` (see Change 1):
   (1) no supported agent detected ⇒ existing warning + return; (2) else
   `!InstallAgents` ⇒ "skipping agent setup" note + return with a zero-value
   `CodingAgentsStep.Result` (before any `Handle*` / `HandleAgentSkills` /
   `SweepLegacyCodexSkills` / `selected` assignment); (3) else install. The
   consent prompt is only shown by `SetupCommand` when at least one agent is
   detected. (The return type is `CodingAgentsStep.Result`, not "InstallResult".)
7. **Under `InstallAgents = true`, existing per-vendor `--skip-*` semantics are
   preserved unchanged** — including the Kiro flag coupling detailed in the
   truth table (`--skip-kiro-hooks` alone still registers Kiro MCP + skills, but
   `--skip-kiro-hooks --skip-kiro-mcp` suppresses Kiro **skills** too via
   `selected=false`), and shared `~/.agents/skills` installing whenever a
   non-Claude agent is detected. (Corrects the round-1 spec's incorrect "a
   skipped harness is not installed at all.")
8. **Import eligibility gates on a resolved `currentRepo` tuple** (owner+name from
   origin), not merely on being inside a working tree.
9. **Import eligibility gates on "auth requirements satisfied"** (provider `None`,
   or an authenticated provider with a usable token) — not `provider != None`.
10. **Embedded import auto-skips excluded repos/paths** (we intentionally scoped
    to the current repo) and never blocks on `Console.ReadLine()`.
11. **Imported historical sessions honor the Step 3 default visibility**, sent
    client-side exactly like live recording does (see Change 2 → Visibility).
12. **AppConfig is refreshed with the EXACT saved server URL + profile** (a
    setter, not a precedence re-resolution), and `HandleImport` binds auth to its
    explicit `baseUrl` (see Change 2 → Refresh).

## Change 1 — Step 4 "Coding agents": one install prompt

### Behavior

- After detection (`SetupCommand.cs:210-233`), print the detected harnesses in
  human-readable form, **annotating harnesses whose install makes a material
  change** so consent stays informed — specifically Kiro:
  ```
  Detected coding agents: Claude Code, Codex, Kiro (installing sets kcap as your default Kiro agent)
  ```
- Ask one default-yes prompt:
  ```
  Install kcap for these agents (hooks, skills, instructions, MCP)?
  ```
- The result becomes a single `InstallAgents` boolean.

### Implementation (Approach B, with a hard consent gate)

- Add `bool InstallAgents` to `CodingAgentsStep.Options`.
- **Two ordered early-returns at the top of `RunAsync`** (the return type is
  `CodingAgentsStep.Result`; there is no "InstallResult"):
  1. **No agents detected** ⇒ emit the existing "no supported agent CLI detected"
     warning (`CodingAgentsStep.cs:175-177`, relocated to the top) and return a
     zero-value `Result`.
  2. **`!options.InstallAgents`** (agents detected but user declined) ⇒ print a
     one-line "skipping agent setup" note and return a zero-value `Result`
     **before** any `Handle*`, `HandleAgentSkills` / `SweepLegacyCodexSkills`, or
     `selected` assignment. This guarantees a "no" answer mutates nothing on disk.
  Only after both pass does installation run. (`SetupCommand` still proceeds to
  the provider-API-key prompt and Steps 5-6 afterward regardless.)
- **`SetupCommand` shows the unified prompt only when at least one agent is
  detected** — the no-agents warning is owned by early-return (1), so no prompt
  is shown and `InstallAgents` is effectively false in that case.
- When `InstallAgents` is true, each `Handle*` primary-artifact gate changes from
  `options.NoPrompt || prompt("Install <vendor> ...?")` to simply proceeding
  (equivalent to today's `NoPrompt` path), **still honoring each vendor's
  existing `--skip-<vendor>` / per-artifact `--skip-*` semantics unchanged**
  (Decision 7). The shared `"Install kcap agent skills?"` prompt (`:870`) folds
  into the same `InstallAgents` decision (it now installs when reached, i.e. when
  `InstallAgents` is true and a non-Claude agent is detected).
- **Preserve all downstream gating distinctions** listed under Current state:
  Codex network/MCP require `codexHooksInstalled`; Cursor/Copilot/OpenCode
  downstream require their primary `*Installed` bool; Gemini MCP requires
  `geminiHooksInstalled` while Gemini instructions use `geminiSelected`;
  Kiro/Pi/Antigravity downstream use their `selected` out-params. Collapsing the
  prompts must not flatten these into "detected ⇒ install everything."
- **Kept as separate prompts:** Codex network access (reached only when
  `InstallAgents` is true and `codexHooksInstalled`) and provider API keys
  (unchanged, independent of `InstallAgents`).

### Per-vendor truth table (the contract to implement + test)

| `InstallAgents` | vendor `--skip-<vendor>` | per-artifact `--skip-*` | Result |
|---|---|---|---|
| false | (any) | (any) | Nothing installed; no sweeps; no `selected`; no shared skills. |
| true  | not set | not set | Primary artifact installed; downstream installed subject to its existing success/`selected` gate. |
| true  | not set | set | Primary installed; that specific downstream artifact skipped. |
| true  | set (Codex) | (any) | Hooks skipped ⇒ Codex network-access + MCP not reached (both require `codexHooksInstalled`). |
| true  | all detected vendors set | (any) | Shared `~/.agents/skills` still installs when a non-Claude agent is detected (documented, accepted). |

**Kiro flag coupling (verified — must be preserved exactly; assumes Kiro
detected, kcap on PATH):**

| Kiro flags | `kiroSelected` | Hooks | MCP | Skills |
|---|---|---|---|---|
| none | true | installed | installed | installed |
| `--skip-kiro-hooks` only | true (`CodingAgentsStep.cs:253`) | not installed | **installed** | **installed** |
| `--skip-kiro-hooks --skip-kiro-mcp` | **false** (short-circuit `:233-237`) | not installed | not installed | **not installed** (blocked by `!kiroSelected`, even though `--skip-kiro-skills` was not passed) |
| `--skip-kiro-hooks --skip-kiro-skills` | true | not installed | **installed** | not installed |

The naive collapse must not disturb this: the `SkipKiro && SkipKiroMcp`
short-circuit leaves `selected=false`, transitively suppressing skills. Add a
test per row.

## Change 2 — New Step 6 "Import past sessions"

### Placement & numbering

- Insert after config save + `PingCliSetupAsync` (`SetupCommand.cs:423-446`) and
  **before** the completion summary (`:448-465`).
- Renumber the five existing headers `/5` → `/6` (lines 95, 138, 172, 205, 406)
  and add `Step 6/6 — Import past sessions`.

### Refresh AppConfig before importing (fixes stale profile/URL/auth)

`AppConfig.ResolvedServerUrl` / `ResolvedProfile` are captured pre-dispatch
(`Program.cs:61`), and `GetActiveProfileAsync()` / `LoadProfileConfig()` /
`CreateAuthenticatedClientAsync()` prefer those snapshots. Two concrete fixes are
**required** (a plain re-resolution is NOT sufficient):

- **Exact refresh, not re-resolution.** Do **not** re-run
  `AppConfig.ResolveServerUrl(args, ...)` — that re-applies CLI/env/repo
  precedence and can produce a *different* result than setup just saved (e.g. a
  raw `--server-url localhost:5108` that setup normalized to
  `http://localhost:5108` would re-resolve back to the scheme-less form; `KCAP_URL`
  / `KCAP_PROFILE` / repo-profile matching could pick a different server/profile).
  Add an explicit setter — `AppConfig.SetResolvedState(normalizedServerUrl, savedProfile)`
  — that stores the **exact normalized server URL and the just-saved active
  profile object**, and call it immediately after the save (`SetupCommand.cs:439`).
- **Bind auth to the explicit `baseUrl`.** Change `HandleImport`'s auth call from
  `CreateAuthenticatedClientAsync()` to `CreateAuthenticatedClientAsync(baseUrl)`
  (`ImportCommand.cs:583`) so discovery uses the passed, normalized URL rather
  than the process-global fallback. (Standalone `kcap import` is unaffected — it
  passes the same value it would have resolved.) Note `DiscoverProviderAsync`
  calls `EnsureAbsolute` which `Environment.Exit(2)`s on a scheme-less URL, so the
  normalized URL must be absolute — another reason the exact setter matters.

Regression tests: raw scheme-less `--server-url`, a conflicting `KCAP_URL` /
`KCAP_PROFILE` in the environment, and a server-URL + visibility change in one
process — the import seam must observe the exact saved values in every case.

### Eligibility gate (all must hold to offer the step)

- A **`currentRepo` tuple** resolves via
  `RepositoryDetection.DetectRepositoryAsync(cwd)` with both owner and name from a
  parseable origin remote (mirroring `Program.cs:567-570`). Resolve it **before**
  showing the prompt. Being inside a working tree (`gitRoot != null`) is not
  sufficient.
- **Auth requirements are satisfied:** provider `None`, or an authenticated
  provider with a usable token (reuse the login state setup already tracks).

If either fails: skip the step and keep the existing `kcap import` text hint
(`SetupCommand.cs:487`), with a one-line reason (e.g. "no origin remote — skipping
import").

### Prompt

Default-yes, offered **regardless** of the Step 4 install answer:
```
Import past sessions from this repository?
```

### On "yes" — call the existing importer with a pinned contract

Invoke `ImportCommand.HandleImport(...)` with every argument pinned (no reliance
on defaults changing under us):

- `baseUrl`: the normalized post-setup server URL.
- `filterCwd: null` (required positional — no default).
- `filterSession: null`, `minLines: 15`, `generateSummaries: false`,
  `since: null`, `forcePrivate: false`, `needOrgPick: false`, `storedOrg: null`.
- `sources`: all nine `IImportSource` instances (as in `Program.cs:547-557`).
- `scope: ImportScope.Repo(owner, name)` from the resolved `currentRepo`;
  `currentRepo: (owner, name)` to match.
- `skipConfirmation: true` (the yes/no already served as confirmation).
- `explicitVendorSelection: false` (import every available harness).
- `activeProfile`: the profile name setup just saved (used only to persist org
  selection — irrelevant here since scope is Repo, but pass it correctly anyway).
- `defaultVisibility`: the Step 3 visibility (new param — see Visibility below).
- `autoSkipExclusions: true` (new param — see below).

**Auto-skip exclusions:** `skipConfirmation` only suppresses the final
"Continue?" prompt; the excluded-repo/path loop (`ImportCommand.cs:956-975`)
still calls `Console.ReadLine()` whenever stdout is a TTY. Add an explicit
importer option (e.g. `autoSkipExclusions` / a non-interactive flag on
`HandleImport`) that **auto-skips** excluded repos/paths without prompting, and
pass it for the embedded call. This keeps interactive setup from sprouting
"include repo X?" questions (we deliberately scoped to the current repo) and
prevents `--no-prompt` setup from blocking on `ReadLine`.

**Visibility — imported sessions honor the Step 3 choice (Decision 11).**
Verified mechanics: live recording sends `default_visibility` (from
`activeProfile.DefaultVisibility`) client-side in every session-start hook
payload (e.g. `CodexHookCommand.cs:193-203`, `ClaudeHookCommand.cs:451-456`, and
the other seven vendors), and the server only falls back to org visibility when
that field is **absent**. The import path currently omits `default_visibility`
entirely — its session-start payloads (`ImportCommand.cs:2671-2726` for the
Claude/Codex chain, plus the routed sources `AntigravityImportSource.cs`,
`PiImportSource.cs`, `OpenCodeImportSource.cs`, and the Cursor post-hoc path)
only set `"private"` under `forcePrivate`. `PingCliSetupAsync` does **not** carry
visibility (its body is `{"cliVersion": ...}`, `SetupCommand.cs:632-635`), so the
server default is not updated by setup.

Fix (symmetric with the live path): add an optional
`string? defaultVisibility` parameter to `HandleImport` (alongside `forcePrivate`)
and stamp it into the import session-start payloads exactly as the live hooks do
(`node["default_visibility"] = defaultVisibility` when non-null), threaded through
the chain path and each routed source. Setup passes the Step 3 visibility
(read from the **refreshed** profile — see Refresh above, so it is the just-saved
value, not the stale snapshot). `forcePrivate` continues to take precedence when
set. Standalone `kcap import` passes `null` and is unchanged. Test that an
imported session-start payload carries the chosen visibility.

### Non-interactive behavior (deliberate change — Decision 5)

- **`--no-prompt`:** auto-import the current repo, subject to the eligibility
  gate above, using the auto-skip-exclusions option so it never blocks.
- **`--skip-import`:** new flag to opt out, parsed alongside the other `--skip-*`
  flags (`SetupCommand.cs:28-53`).
- **Rollout notes:** this changes the behavior of existing unattended
  `kcap setup --no-prompt` scripts (they will start uploading current-repo
  history). Call this out in `help-setup.txt`, `help-usage.txt`, `README.md`, and
  the PR description, and cover it with an acceptance test.

### Error handling (return code AND exceptions)

Import is **best-effort**. Wrap the `HandleImport` call in try/catch **and**
inspect its return value: a non-zero exit code (import returns `1` on several
expected failures without throwing) is treated the same as a thrown exception —
print a warning plus the manual `kcap import` hint and continue to the completion
summary. Never fail `kcap setup` because import failed. Test both the thrown and
the non-zero-return paths.

## Testability

`SetupCommand.HandleAsync` is a monolithic method with no injectable seam, so the
new logic must be extracted into **pure/injectable helpers** to be unit-testable
without running the whole login/config wizard:

- **Agent-install decision helper** — given `DetectedAgents` (+ flags), returns
  the display list (with the Kiro annotation) and the `InstallAgents` gate. Test:
  no agents ⇒ no prompt; unified-no ⇒ zero-value `CodingAgentsStep.Result` and (via
  `CodingAgentsStep`) zero mutations; all-detected-vendors-skipped ⇒ shared-skills
  behavior per the truth table.
- **Import eligibility/execution policy helper** — given `currentRepo`, auth
  state, `--no-prompt`, `--skip-import`, and (injected) an import-runner delegate
  returning an exit code, decides whether/what to import and how to react. Test:
  no origin ⇒ skip + hint; provider `None` ⇒ eligible; `--skip-import` ⇒ skip;
  `--no-prompt` ⇒ auto-import; importer returns non-zero ⇒ warn + continue;
  importer throws ⇒ warn + continue; stale-vs-fresh profile/URL after save.
- **`CodingAgentsStep`** already takes injected delegates; add tests for: the
  no-agents early-return (warning emitted, no prompt), the `InstallAgents=false`
  early-return (no `Handle*`/sweep/`selected` reached, zero-value `Result`), and
  that `InstallAgents=true` preserves each downstream gate — including the three
  Kiro flag-coupling rows from the truth table.
- **Import seam** tests: an imported session-start payload carries the chosen
  `defaultVisibility`; the exact-refresh setter makes the seam observe the saved
  normalized URL + profile under raw scheme-less `--server-url` and conflicting
  `KCAP_URL`/`KCAP_PROFILE`; `autoSkipExclusions` prevents any `Console.ReadLine`.
- Verify no `IL3050`/`IL2026` AOT warnings via
  `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release`.

## Documentation (same PR)

Per `CLAUDE.md` and the `vendor-surface-sync` memory:

- `README.md` — getting-started walkthrough + `setup` under CLI commands:
  the single agent-install prompt, the import step, `--skip-import`, and a
  **prominent warning** that `kcap setup --no-prompt` now imports current-repo
  history.
- `src/Capacitor.Cli.Core/Resources/help-setup.txt` — single agent prompt,
  import step, `--skip-import`, and the `--no-prompt` behavior-change warning.
- `src/Capacitor.Cli.Core/Resources/help-usage.txt` — add `--skip-import` if it
  documents setup flags.

## Files touched

- `src/Capacitor.Cli/Commands/SetupCommand.cs` — detected-harness display (with
  Kiro annotation) + single install prompt; `InstallAgents` wiring; AppConfig
  resolved-state refresh after save; new Step 6 (currentRepo + auth eligibility,
  prompt, pinned `HandleImport` call behind an injectable runner seam, return-code
  + exception handling); `--skip-import` parsing; renumber headers to `/6`;
  extract the two decision helpers.
- `src/Capacitor.Cli/Commands/CodingAgentsStep.cs` — add `Options.InstallAgents`;
  top-of-`RunAsync` ordered early-returns (no-agents warning; zero-value
  `CodingAgentsStep.Result` on `InstallAgents=false`);
  route the per-vendor gates through the single decision while preserving every
  downstream success/`selected` distinction and existing `--skip-*` semantics.
- `src/Capacitor.Cli/Commands/ImportCommand.cs` — add `autoSkipExclusions` (never
  `Console.ReadLine()` when set) and `defaultVisibility` params to `HandleImport`;
  bind auth to `CreateAuthenticatedClientAsync(baseUrl)`; stamp
  `default_visibility` into the chain-path session-start payload
  (`~:2671-2726`). Routed sources `AntigravityImportSource.cs`,
  `PiImportSource.cs`, `OpenCodeImportSource.cs` (and the Cursor path) — thread
  `defaultVisibility` into their session-start payloads.
- `src/Capacitor.Cli.Core/.../AppConfig.cs` — add
  `SetResolvedState(serverUrl, profile)` (exact setter — no precedence
  re-resolution) and call it after the setup save.
- `src/Capacitor.Cli.Core/Resources/help-setup.txt`, `help-usage.txt`,
  `README.md` — docs + `--no-prompt` warning.
- `test/Capacitor.Cli.Tests.Unit/` — decision-helper + `CodingAgentsStep` gate
  tests; acceptance test for the `--no-prompt` import behavior change.

## Rollout / compatibility notes

- **Behavior change:** `kcap setup --no-prompt` now imports current-repo history
  (Decision 5). Documented in help text, README, and the PR; opt out with
  `--skip-import`.
- `kcap plugin install|remove --<vendor>` and the standalone `kcap import`
  command surfaces are unchanged (the new importer option defaults off there).
- Existing `--skip-<vendor>` and per-artifact `--skip-*` flags keep today's
  semantics and compose with the single `InstallAgents` decision (truth table).
- The npm postinstall refresh path (`plugin install --if-installed`) is
  unaffected — it does not run full `kcap setup`.
```
