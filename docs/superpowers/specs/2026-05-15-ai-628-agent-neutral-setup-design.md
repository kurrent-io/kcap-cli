# AI-628 — Agent-neutral `kapacitor setup`

## Problem

`kapacitor setup` treats Claude Code as the only first-class coding agent. Step 4/5 is hard-coded "Claude Code Plugin": it always asks where to install the Claude plugin and never mentions Codex. Codex CLI hooks are installed via a separate `kapacitor plugin install --codex` command, and the README documents them under an "Also using Codex CLI?" sub-section that reads as an afterthought. Users who only use Codex have no signal during onboarding that they need to take a second, undocumented-in-the-wizard step.

The CLI already has all the install machinery for Codex (`PluginCommand.InstallCodex` and `InstallCodexHooks`); it just isn't wired into setup.

## Goal

Make `kapacitor setup` agent-neutral: detect each supported agent on `PATH`, ask one yes/no per detected agent, and install hooks user-wide for whichever the user opts into. Stop privileging Claude in the wizard. Stop requiring a separate command to onboard a Codex user.

Non-goals:

- Project-scope installs during setup — handled by `kapacitor plugin install [--codex] --project` as today.
- Detecting agent CLI versions or running `claude --help` / `codex --help`. A presence-on-PATH probe is enough; we don't act on version info.
- Removing the standalone `kapacitor plugin install` command. It stays for follow-up, project-scope, and re-install scenarios.

## Decisions (from brainstorm)

| Decision | Choice |
|---|---|
| Detection method | `PATH` probe (cross-platform; walks `PATHEXT` on Windows). Not `--help` invocation. |
| Per-agent not-detected behaviour | Print one informational skip line, no prompt. |
| Neither detected | Print both skip lines, then a yellow warning that no supported agent was found. Continue setup; don't fail. |
| Scope in setup | Always user-wide. No `user/project/skip` picker per agent. Project scope is a follow-up `kapacitor plugin install --project` job. |
| Step count | Stays at 5. Step 4 becomes "Coding agents". |
| `--no-prompt` flags | Opt-out: `--skip-claude-hooks`, `--skip-codex-hooks`. Default installs everything detected. |
| Existing `--plugin-scope` | Soft-deprecate. Silent compatibility: `skip` → `--skip-claude-hooks`, `project` → existing project-scope Claude install, `user` → no-op. Marked legacy in help text. |
| Codex `/hooks` trust gotcha | Surface in setup output (only after a fresh Codex hook install) and in the README anywhere Codex hooks are mentioned. |

## Design

### Agent detection

New type: `kapacitor.Commands.AgentDetector` (static, no dependencies, easy to unit-test). Lives alongside `SetupCommand` and `PluginCommand` to match the existing namespace convention.

API:

```csharp
public static class AgentDetector {
    public static bool IsInstalled(string binaryName);                              // production: reads env + platform

    // Pure, testable seam. No env reads, no OS branches.
    internal static bool IsInstalled(
        string binaryName,
        IEnumerable<string> paths,
        IEnumerable<string> extensions,
        Func<string, bool> isExecutable);
}
```

Implementation:

- Read `Environment.GetEnvironmentVariable("PATH")`, split on `Path.PathSeparator`.
- Build the `extensions` list:
  - On Windows (`OperatingSystem.IsWindows()`), parse `PATHEXT` (fall back to `".EXE;.CMD;.BAT"` if unset), split on `;`, prune empties.
  - On Unix, a single empty string.
- Build the `isExecutable` predicate:
  - On Windows: `File.Exists` — the `PATHEXT` filter already gates on executable extensions.
  - On Unix: `path => File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0`. The bitmask-then-non-zero check is **any-execute** (matches shell `PATH` resolution: a 0700 binary owned by the current user is detected; `HasFlag(A|B|C)` would have required all three execute bits and would have missed 0700). On AOT/.NET 10, `File.GetUnixFileMode` is in the BCL.
- The pure internal overload then iterates `(dir, ext)` pairs, builds `Path.Combine(dir, name + ext)`, and returns `true` on the first `isExecutable(path) == true`.
- No subprocess. No reflection. AOT-clean.

This is a presence-and-executability probe, not an "actually runs" probe — a binary that's installed but broken (e.g. wrong arch, missing dynamic lib) will pass detection and fail the first time the user invokes it. AI-628 originally suggested `claude --help` / `codex --help`, but the extra correctness isn't worth the subprocess cost: a broken local CLI is the user's problem, not setup's.

Probes:

- `"claude"` — Claude Code CLI.
- `"codex"` — Codex CLI.

### Setup step 4 layout

Rule banner: `Step 4/5 — Coding agents`.

Detection runs once before any prompts. Output (interactive, both detected):

```
── Step 4/5 — Coding agents ──────────────────────────────────

  Kapacitor records sessions by installing hooks into your coding agent CLIs.

  ✓ Claude Code detected
  ✓ Codex CLI detected

  Install Claude Code plugin (hooks, skills, memory)? [Y/n]
  Install Codex CLI hooks (and skills)? [Y/n]

  ✓ Claude Code plugin installed (user: ~/.claude/settings.json)
  ✓ Codex hooks installed (user: ~/.codex/hooks.json)
  ✓ Codex skills installed (user: ~/.codex/skills/)
    Next: run /hooks inside Codex and trust each kapacitor entry — Codex
    won't execute hooks until each is explicitly trusted.
```

Variants:

- Not detected for an agent: replace the `✓` line with `· <Agent> not found on PATH — skipping` (dim style) and skip the prompt.
- User answers "no": print `· <Agent> hooks not installed (you can run kapacitor plugin install [--codex] later)`.
- Neither detected: print both skip lines, then `⚠ No supported agent CLI detected. Install Claude Code or Codex CLI to start capturing sessions.` in yellow. Setup proceeds to step 5 normally so the profile, auth, and daemon name still get persisted (a user setting up a daemon-host machine may legitimately have neither CLI installed).
- The Codex "Next: run /hooks…" line is printed **only** when we just successfully wrote `~/.codex/hooks.json`. Skipped, declined, and "Codex not detected" cases do not print it.

### Refactor for testability

Today `SetupCommand.HandleAsync` is a 240-line monolith. The change pulls step 4 out into:

```csharp
namespace kapacitor.Commands;

internal static class CodingAgentsStep {
    internal record Options(bool SkipClaude, bool SkipCodex, bool NoPrompt, bool LegacyProjectScope);
    internal record DetectedAgents(bool Claude, bool Codex);
    internal record Paths(string ClaudeSettingsPath, string? PluginDir, string CodexHooksPath, string CodexSkillsDir);
    internal record Installers(
        Func<string /*settings*/, string /*pluginDir*/, bool> InstallClaudePlugin,
        Func<string /*hooksPath*/, bool>                       InstallCodexHooks,
        Func<string /*src*/, string /*dst*/, bool>             InstallCodexSkills);
    internal record Result(bool ClaudeInstalled, bool CodexHooksInstalled, bool CodexSkillsInstalled);

    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,   // arg: prompt text; return: yes/no (default Y assumed by callers)
        Action<string> writeLine);   // Spectre markup-aware sink
}
```

**No I/O is hard-coded inside the step.** All target file/dir paths come in via `Paths`; the actual install side-effects come in via `Installers`. Production wires them as follows:

- `paths.ClaudeSettingsPath` = `options.LegacyProjectScope ? <repo>/.claude/settings.local.json : ClaudePaths.UserSettings`.
- `paths.PluginDir` = `SetupCommand.ResolvePluginPath()` (nullable).
- `paths.CodexHooksPath` = `CodexPaths.UserHooksJson` (setup is always user-wide for Codex — no project-scope path here).
- `paths.CodexSkillsDir` = `CodexPaths.UserSkillsDir`.
- `installers.InstallClaudePlugin` = `SetupCommand.InstallPlugin` (existing).
- `installers.InstallCodexHooks` = `PluginCommand.InstallCodexHooks` (existing).
- `installers.InstallCodexSkills` = `(src, dst) => PluginCommand.InstallCodexSkills(src, dst)` with `src = Path.Combine(pluginDir, "codex-skills")` (existing).

Tests pass fakes for `Installers` that record their arguments and return scripted results — no real `~/.claude` or `~/.codex` writes, no `ClaudePaths.UserSettings` static state to fight, no env scoping.

**Install behaviour when `paths.PluginDir == null`:**

- Claude plugin: cannot install (marketplace source is the plugin dir itself). Print the existing warning, set `ClaudeInstalled = false`.
- Codex hooks: **still install**. The hooks.json content is the literal string `"kapacitor codex-hook"` plus a timeout — it never references the plugin dir. This preserves the AI-628 goal for Codex-only users even when plugin path resolution fails.
- Codex skills: cannot install (skills source lives under the plugin dir). Print a warning that skills were skipped but hooks were installed, set `CodexSkillsInstalled = false`.

**Install behaviour when an installer returns `false`:**

| Installer | Returns false → |
|---|---|
| `InstallClaudePlugin` | Emit `⚠ Could not update Claude settings file. Install manually inside Claude Code: /plugin install <pluginPath>` (matches existing wording in `SetupCommand.HandleAsync`). `Result.ClaudeInstalled = false`. |
| `InstallCodexHooks` | Emit `⚠ Could not write Codex hooks file.` `Result.CodexHooksInstalled = false`. **Do not attempt `InstallCodexSkills`** (skills are pointless without hooks). **Do not print the Codex `/hooks` trust hint** (no hooks to trust). |
| `InstallCodexSkills` | Hooks succeeded but skills failed — emit `⚠ Codex hooks installed but skills could not be copied to ~/.codex/skills`. `Result.CodexSkillsInstalled = false`. The Codex trust hint still prints (hooks are what need trusting). |

These are the only failure modes the installers report, so this table exhausts them.

The `prompt` callback always uses `ConfirmationPrompt` with `DefaultValue = true` in production. Tests inject a callback that records the prompt text and returns scripted answers.

This keeps `SetupCommand` slim and gives tests a way to drive every branch without instantiating `AnsiConsole` or stubbing `Environment.PATH`.

### CLI flags

New `--no-prompt` flags on `kapacitor setup`:

- `--skip-claude-hooks` — don't install Claude Code plugin even if `claude` is detected.
- `--skip-codex-hooks` — don't install Codex hooks/skills even if `codex` is detected.

Default `--no-prompt` behaviour: install hooks for every detected agent. Detection still runs and still emits the skip one-liners, so a CI machine without Codex doesn't see Codex output.

Back-compat for the existing `--plugin-scope` arg:

| Old value | New behaviour |
|---|---|
| `--plugin-scope user` | No-op (matches new default). |
| `--plugin-scope project` | **Legacy exception** to the "setup is always user-wide" rule, kept indefinitely until anyone scripting it migrates to `kapacitor plugin install --project`. Claude plugin written to `<repo>/.claude/settings.local.json` instead of user settings; surfaces via `Options.LegacyProjectScope`. Codex is unaffected (no symmetric Codex legacy flag exists; `setup` never wrote project-scope Codex hooks). |
| `--plugin-scope skip` | Alias for `--skip-claude-hooks`. |

Help text gains: *"`--plugin-scope` is retained for backwards compatibility. New scripts should use `--skip-claude-hooks` and `--skip-codex-hooks`. For project-scope installs run `kapacitor plugin install [--codex] --project`."* No deprecation warning printed at runtime — silent compatibility.

The `CodingAgentsStep` test list (below) includes one explicit case asserting the legacy `--plugin-scope project` mapping continues to write to the project-scope Claude settings path.

### Standalone command (unchanged)

`kapacitor plugin install [--codex] [--project]` and `kapacitor plugin remove [--codex] [--project]` stay as-is. They remain the path for:

- Project-scope installs.
- Re-installs after a CLI upgrade refreshes the hook timeout/command.
- Installing hooks for an agent the user added after running `kapacitor setup`.

The standalone `plugin install --codex` path also gains the same "run /hooks inside Codex and trust each entry" reminder line in its stdout, so the message is identical whether the user installs Codex hooks via setup or via the standalone command.

### README changes

1. **`## Getting started` / prereqs** — add bullet: *"To capture sessions, kapacitor needs at least one supported coding agent CLI on `PATH` (Claude Code or Codex CLI). Setup itself runs to completion without one — it'll configure your profile, auth, and daemon — and you can install hooks later with `kapacitor plugin install [--codex]` once you have an agent installed."* (Phrased as a hook-install prereq, not a setup prereq — matches the design's "neither detected, setup proceeds" behaviour.)
2. **"Setup wizard walks you through"** — rewrite item 4 from *"Claude Code plugin — installs hooks, skills, and collaborative memory…"* to *"Coding-agent hooks — detects Claude Code and Codex CLI on `PATH` and offers to install hooks/skills for each."*
3. **Delete the "Also using Codex CLI?" sub-section.** Replace with one short follow-up note at the end of the quick-start: *"To install hooks for an agent you added after running setup, or to scope an install to a single repo, run `kapacitor plugin install [--codex] [--project]`. Codex doesn't execute hooks until you run `/hooks` inside Codex and trust each entry."*
4. **`## Hosted Codex agents`** — keep the existing Codex install reminder (daemon hosts may need hooks even without interactive use), but trim the language that implied Codex was always a separate follow-up step.
5. **Per-command `kapacitor setup` section** under `## CLI commands` — document the new flags (`--skip-claude-hooks`, `--skip-codex-hooks`) and mark `--plugin-scope` as legacy with a back-compat note.
6. **`kapacitor status`** — no change to output. Already reports both surfaces.

### Help-text files

- `src/Kapacitor.Core/Resources/help-setup.txt` — rewrite to document `--skip-claude-hooks`, `--skip-codex-hooks`, and mark `--plugin-scope` as legacy. Other commands' help text untouched.

## Testing

**`AgentDetector` unit tests** (drive the pure internal overload with synthetic `paths`, `extensions`, and `isExecutable` predicate — these tests run identically on every host OS because the predicate is injected, not derived from `OperatingSystem.IsWindows()`):

- Name found in one PATH dir, predicate returns true → detected.
- Name found in one PATH dir, predicate returns false → not detected. Models a file present but not executable on Unix, or a present file with the wrong extension on Windows.
- Name not in any PATH dir → not detected.
- Empty PATH list → not detected.
- "Windows-shaped" inputs: `extensions = [".EXE", ".CMD"]`, `claude.cmd` exists per predicate → detected.
- "Windows-shaped" inputs: `extensions = [".EXE", ".CMD"]`, bare `claude` (no extension) per predicate → not detected (matches Windows shell behaviour: `cmd.exe` won't run `claude` without an extension in `PATHEXT`).
- "Windows-shaped" inputs: `extensions` is the fallback `[".EXE", ".CMD", ".BAT"]` when env unset (covered by a separate one-line test of the public overload's fallback construction logic; skipped on non-Windows hosts to avoid asserting on real env behaviour).
- PATH list contains an empty entry → handled without throwing, that entry skipped.

**Unix executable-bit assertion** — separate test, marked `[OS=Unix]` (skipped on Windows hosts): create a temp file, `chmod 0700`, call the production `IsInstalled(name)`; create a second temp file `chmod 0644`, assert not detected. This is the only test that touches a real filesystem and exercises the real `isExecutable` predicate; it guards against regression on the "any of UGO execute bits is enough" rule.

**`CodingAgentsStep` unit tests** (inject fake `Installers` recording arguments and returning scripted bools; inject `Func<string,bool> prompt` and `Action<string> writeLine` sinks; no AnsiConsole, no real filesystem):

- Both detected, user says yes to both → both Claude and Codex-hook installers called with the expected paths, Codex-skill installer called with `<plugin>/codex-skills` → `~/.codex/skills`, both `✓` lines, Codex trust hint printed.
- Both detected, user says yes to Claude only → Claude installer called, Codex installers not called, no Codex trust hint.
- Claude only detected → only Claude prompt asked; Codex skip line emitted; no Codex installer called.
- Codex only detected → symmetric: only Codex prompt asked; Claude skip line emitted.
- Neither detected → no prompts, two skip lines, yellow warning emitted, no installers called.
- `paths.PluginDir == null` and both detected, user says yes to both → Claude installer NOT called (plugin dir warning emitted); Codex-hook installer called (still works); Codex-skill installer NOT called (skills warning emitted); Codex trust hint still printed since hooks succeeded.
- `--no-prompt` with both detected and no skip flags → both install paths called, prompt callback never invoked.
- `--no-prompt` with `--skip-codex-hooks` → Claude installed, Codex installers not called, message distinguishes "skipped by flag" from "not detected".
- `--plugin-scope skip` legacy mapping → behaves identical to `--skip-claude-hooks`.
- `--plugin-scope project` legacy mapping → `Options.LegacyProjectScope = true`, Claude installer called with the project-scope settings path (`<cwd>/.claude/settings.local.json`), not the user one.

**Installer-failure unit tests** (each fake installer scripted to return false):

- `InstallClaudePlugin` returns false → Claude warning text emitted, `Result.ClaudeInstalled = false`, Codex installers still attempted independently.
- `InstallCodexHooks` returns false → Codex hooks warning emitted, `Result.CodexHooksInstalled = false`, `InstallCodexSkills` **not called**, Codex trust hint **not printed**, `Result.CodexSkillsInstalled = false`.
- `InstallCodexSkills` returns false (hooks succeeded) → skills warning emitted, `Result.CodexSkillsInstalled = false`, **Codex trust hint still printed** (assertion is explicit), `Result.CodexHooksInstalled = true`.

**`PluginCommand.InstallCodex` extension:** add one assertion that the trust-hint line appears in stdout after a successful install.

**Manual verification:**

- macOS: with both CLIs installed, full interactive setup.
- macOS: with `mv $(which codex) /tmp/` (simulate missing Codex) → skip line + Claude-only prompt.
- Windows: `claude.cmd` shim from npm is detected, hooks file written to `%USERPROFILE%\.claude\settings.json`.
- AOT publish: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` is empty.

## Rollout

Single PR. No data migration. No server-side change. Anyone re-running `kapacitor setup` after upgrade gets the new flow; existing installs continue to work unchanged because the hook JSON files we'd write are identical to what `PluginCommand.InstallCodex` already produces.

## Risk & mitigation

| Risk | Mitigation |
|---|---|
| PATH probe misses an agent installed via an unusual mechanism (e.g. shell alias, function, or a non-PATH wrapper script). | User can re-run setup with the right binary on `PATH`, or use the standalone `kapacitor plugin install [--codex]` command. The skip lines tell the user exactly what we looked for. |
| Windows `PATHEXT` edge cases. | Cover with unit tests; default fallback matches what `cmd.exe` uses. |
| Users scripted around current `--plugin-scope` flag. | Silent compatibility map preserves behaviour; legacy values keep working with no error. |
| Codex `/hooks` trust step still surprises users despite the printed hint. | Hint is printed both in setup and after standalone `plugin install --codex`, and added to README. Same wording in all three places so users see a consistent string. |

## Out of scope

- Adding support for additional agents (e.g. Cursor, Aider) — when added, `AgentDetector.IsInstalled` gets another probe and `CodingAgentsStep` gets another branch. The data model and prompt loop generalise cleanly.
- Reworking the `kapacitor status` output to summarise per-agent installation state across user/project scopes. Today's status output is unchanged.
