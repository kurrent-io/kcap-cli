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
    public static bool IsInstalled(string binaryName);                            // production: reads env
    internal static bool IsInstalled(string binaryName, IEnumerable<string> paths, // testable seam
                                     IEnumerable<string> extensions);
}
```

Implementation:

- Read `Environment.GetEnvironmentVariable("PATH")`, split on `Path.PathSeparator`.
- On Windows (`OperatingSystem.IsWindows()`), read `PATHEXT`; fall back to `".EXE;.CMD;.BAT"` if unset; split on `;`. Empty entries are pruned.
- On Unix, the extension list is a single empty string.
- For each `(dir, ext)` pair, check `File.Exists(Path.Combine(dir, name + ext))`. Return `true` on first hit.
- No subprocess. No reflection. AOT-clean.

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
    internal record Options(bool SkipClaude, bool SkipCodex, bool NoPrompt);
    internal record DetectedAgents(bool Claude, bool Codex);
    internal record Result(bool ClaudeInstalled, bool CodexInstalled);

    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Func<string, bool> prompt,   // arg: prompt text; return: yes/no (default Y assumed by callers)
        Action<string> writeLine,    // Spectre markup-aware sink
        string? pluginPath);         // null = plugin dir not resolved; emit warning, skip Claude install
}
```

`SetupCommand` calls `AgentDetector` once, wraps the detection result, and invokes `CodingAgentsStep.RunAsync`. The step does its own install work via the existing `SetupCommand.InstallPlugin` and `PluginCommand.InstallCodexHooks` / `PluginCommand.InstallCodexSkills` helpers (no duplication).

`pluginPath == null` (plugin directory not found, today's "Re-install kapacitor via npm" path) means we can install neither Claude nor Codex hooks (Codex skills also live under the plugin dir). The step emits the existing warning and returns `Result(false, false)`.

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
| `--plugin-scope project` | Preserved: Claude plugin written to `<repo>/.claude/settings.local.json` instead of user settings. Codex unaffected. |
| `--plugin-scope skip` | Alias for `--skip-claude-hooks`. |

Help text gains: *"`--plugin-scope` is retained for backwards compatibility. New scripts should use `--skip-claude-hooks` and `--skip-codex-hooks`."* No deprecation warning printed at runtime — silent compatibility.

### Standalone command (unchanged)

`kapacitor plugin install [--codex] [--project]` and `kapacitor plugin remove [--codex] [--project]` stay as-is. They remain the path for:

- Project-scope installs.
- Re-installs after a CLI upgrade refreshes the hook timeout/command.
- Installing hooks for an agent the user added after running `kapacitor setup`.

The standalone `plugin install --codex` path also gains the same "run /hooks inside Codex and trust each entry" reminder line in its stdout, so the message is identical whether the user installs Codex hooks via setup or via the standalone command.

### README changes

1. **`## Getting started` / prereqs** — add bullet: *"At least one supported coding agent CLI on `PATH`: Claude Code or Codex CLI. Setup will detect installed agents and only configure hooks for those."*
2. **"Setup wizard walks you through"** — rewrite item 4 from *"Claude Code plugin — installs hooks, skills, and collaborative memory…"* to *"Coding-agent hooks — detects Claude Code and Codex CLI on `PATH` and offers to install hooks/skills for each."*
3. **Delete the "Also using Codex CLI?" sub-section.** Replace with one short follow-up note at the end of the quick-start: *"To install hooks for an agent you added after running setup, or to scope an install to a single repo, run `kapacitor plugin install [--codex] [--project]`. Codex doesn't execute hooks until you run `/hooks` inside Codex and trust each entry."*
4. **`## Hosted Codex agents`** — keep the existing Codex install reminder (daemon hosts may need hooks even without interactive use), but trim the language that implied Codex was always a separate follow-up step.
5. **Per-command `kapacitor setup` section** under `## CLI commands` — document the new flags (`--skip-claude-hooks`, `--skip-codex-hooks`) and mark `--plugin-scope` as legacy with a back-compat note.
6. **`kapacitor status`** — no change to output. Already reports both surfaces.

### Help-text files

- `src/Kapacitor.Core/Resources/help-setup.txt` — rewrite to document `--skip-claude-hooks`, `--skip-codex-hooks`, and mark `--plugin-scope` as legacy. Other commands' help text untouched.

## Testing

**`AgentDetector` unit tests:**

- Bare name found in one PATH dir (Unix) → detected.
- Name not in any PATH dir → not detected.
- Empty PATH env → not detected.
- Windows: `claude.cmd` present, `PATHEXT=".EXE;.CMD"` → detected.
- Windows: bare `claude` file with no extension, `PATHEXT=".EXE;.CMD"` → not detected (matches Windows shell behaviour).
- Windows: `PATHEXT` unset → fallback `.EXE;.CMD;.BAT` applied.
- PATH contains an empty entry (`::` on Unix) → handled without throwing.

**`CodingAgentsStep` unit tests** (drives `Func<string, bool> prompt` and an `Action<string> writeLine` sink so no AnsiConsole is needed):

- Both detected, user says yes to both → both installs called, both `✓` lines, Codex trust hint printed.
- Both detected, user says yes to Claude only → Claude installed, Codex declined, no Codex trust hint.
- Claude only detected → one prompt only, Codex skip line emitted.
- Codex only detected → symmetric.
- Neither detected → no prompts, two skip lines, yellow warning emitted.
- `--no-prompt` with both detected and no skip flags → both install paths called, no prompt invoked.
- `--no-prompt` with `--skip-codex-hooks` → Claude installed, Codex skipped via flag (distinct message from "not detected").
- `--plugin-scope skip` legacy mapping → behaves identical to `--skip-claude-hooks`.

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
