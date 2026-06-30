# Daemon honors `CLAUDE_CONFIG_DIR` for `~/.claude.json`

**Date:** 2026-06-30
**Status:** Approved
**Follows up:** `2026-06-30-harness-config-dir-env-overrides-design.md` (this closes that spec's documented daemon non-goal).

## Problem

The agent daemon launches Claude Code in per-agent worktrees. Two `ClaudeLauncher` operations read/write the **user-global** `~/.claude.json`, both hardcoded to `Path.Combine(PathHelpers.HomeDirectory, ".claude.json")`:

- **`TrustWorktreeInClaudeConfig`** (`ClaudeLauncher.cs:215`) — sets `projects[<worktreePath>].hasTrustDialogAccepted = true` so the launched agent skips the trust dialog for its fresh worktree.
- **`WriteMcpConfig`** (`ClaudeLauncher.cs:337`) — reads `projects[<sourceRepoPath>].mcpServers` to merge the user's MCP servers into the worktree's `.mcp.json`.

When a user relocates Claude's config via `CLAUDE_CONFIG_DIR`, the spawned `claude` reads and writes `$CLAUDE_CONFIG_DIR/.claude.json`, but the daemon touches the stale `$HOME/.claude.json`. Result: worktree trust is written to a file Claude ignores (agent re-prompts for trust), and the user's MCP servers fail to propagate into worktree launches.

The sibling resolver `ClaudePaths.Home(...)` already honors `CLAUDE_CONFIG_DIR` (from the prior PR), and `ClaudeLauncher`'s `.claude/projects` symlink logic already routes through `ClaudePaths.Projects`/`ProjectDir`. Only the two `.claude.json` reads were left on the hardcoded home path.

## `.claude.json` location (empirically verified — Claude Code 2.1.196)

Running `claude` with an isolated `HOME` and `CLAUDE_CONFIG_DIR` pointed at an empty temp dir created `projects/`, `sessions/`, `backups/`, **and `.claude.json` directly under `$CLAUDE_CONFIG_DIR`**; `HOME` was untouched. So:

- **Default** (no `CLAUDE_CONFIG_DIR`): `$HOME/.claude.json` — a *sibling* of the `~/.claude/` directory.
- **`CLAUDE_CONFIG_DIR` set**: `$CLAUDE_CONFIG_DIR/.claude.json` — *inside* the config dir.

This is neither `Path.Combine(Home(), ".claude.json")` (which would wrongly give `$HOME/.claude/.claude.json` in the default case) nor `Path.GetDirectoryName(Home())` (which yields `/` for an absolute override). It needs its own resolver: base = `CLAUDE_CONFIG_DIR ?? $HOME`, then `.claude.json`.

## Goal

Make the daemon's user-global `.claude.json` reads/writes target the same file the spawned `claude` uses, under `CLAUDE_CONFIG_DIR` relocation.

## Design

### 1. New resolver: `ClaudePaths.UserConfigJson`

Add to `src/Capacitor.Cli.Core/ClaudePaths.cs`, mirroring the `Home(...)` shape (env-var-first, injectable param for deterministic tests):

```csharp
/// <summary>
/// Path to Claude's user-global config FILE (account/OAuth, MCP servers,
/// per-project trust flags). NOT the same base as <see cref="Home"/>:
/// with CLAUDE_CONFIG_DIR set it lives INSIDE the config dir
/// ($CLAUDE_CONFIG_DIR/.claude.json); by default it is a SIBLING of ~/.claude
/// ($HOME/.claude.json). Verified against Claude Code 2.1.196.
/// </summary>
public static string UserConfigJson(string? home = null, string? configDir = null) {
    configDir ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    if (!string.IsNullOrWhiteSpace(configDir)) return Path.Combine(configDir, ".claude.json");

    home ??= PathHelpers.HomeDirectory;
    return Path.Combine(home, ".claude.json");
}
```

### 2. `ClaudeLauncher` uses the resolver

Replace both hardcoded computations:
- `TrustWorktreeInClaudeConfig` (`ClaudeLauncher.cs:215`): `var claudeJsonPath = ClaudePaths.UserConfigJson();`
- `WriteMcpConfig` (`ClaudeLauncher.cs:337`): `var claudeJsonPath = ClaudePaths.UserConfigJson();`

Add a one-line comment near one of these (or the spawn site) noting that the spawned `claude` inherits `CLAUDE_CONFIG_DIR` from the daemon's environment, so daemon-side reads/writes and the child resolve the same file.

## Why explicit env-forwarding is NOT added

The spawned `claude` already sees `CLAUDE_CONFIG_DIR`:
- The daemon is launched by `kcap daemon` with no environment override (`DaemonCommands`), so it inherits the user's shell environment — including `CLAUDE_CONFIG_DIR` if exported.
- The Unix PTY fork (`UnixPtyProcess`) starts the child from the daemon's inherited environment and unsets only a specific list (`CLAUDECODE`, `CLAUDE_CODE_ENTRYPOINT`, `ANTHROPIC_API_KEY`, `KCAP_*`, …) that does **not** include `CLAUDE_CONFIG_DIR`. The Windows ConPty path (`ConPtyProcess`) copies the full daemon environment then overlays the kcap-specific `extraEnv`.

So the daemon (reading its own `CLAUDE_CONFIG_DIR`) and the child `claude` (inheriting the same value) resolve the **same** `.claude.json`. Re-injecting it into the `extraEnv` dict would be redundant. (If the daemon's environment lacks the variable, both the daemon code and the child agree on `$HOME/.claude.json` — still consistent.)

## Scope / non-goals

- **Project-local and worktree `.claude` paths** (source-repo `.claude` overlay, `<worktree>/.claude/settings.local.json`) stay repo-relative — unaffected, as in the prior spec.
- **The `.claude/projects` symlink** already uses `ClaudePaths.Projects`/`ProjectDir` (honors the override) — no change.
- **Other harnesses' daemon launchers** (`CodexLauncher`) read only project-local/worktree `.codex` paths, not a user-global config file — out of scope.
- **No new env-forwarding** (see above).

## Testing

- **`ClaudePaths.UserConfigJson` unit tests** (the bug-prone logic — locks the "inside on override / sibling on default" semantics):
  1. Default via param: `UserConfigJson(home: "/fake/home")` ⇒ `/fake/home/.claude.json`.
  2. Override via param: `UserConfigJson(home: "/fake/home", configDir: "/relocated")` ⇒ `/relocated/.claude.json` (verbatim base + `.claude.json`).
  3. Env override (`[NotInParallel("HomeEnvVarMutation")]`, `try/finally` restore): set `CLAUDE_CONFIG_DIR` ⇒ `$CLAUDE_CONFIG_DIR/.claude.json`; unset ⇒ `$HOME/.claude.json`.
  4. Regression guard: assert the default-case result ends in `.claude.json` directly under home (i.e. it is NOT `<home>/.claude/.claude.json`), pinning that `UserConfigJson` does not just delegate to `Path.Combine(Home(), ".claude.json")`.

- **`ClaudeLauncher`**: there are currently no `ClaudeLauncher` tests, and `TrustWorktreeInClaudeConfig`/`WriteMcpConfig` are `static void` methods that compute the path inline and do file I/O. The change is a mechanical swap. The implementation plan will check whether a cheap seam exists (e.g. extracting the path at the top via the resolver, or an internal overload taking the config path) to add one focused launcher test asserting it targets the override path; if no cheap seam exists, coverage rests on the `UserConfigJson` unit tests plus unchanged default behavior, and the plan will say so explicitly rather than adding heavy test scaffolding.

## AOT

Plain string/path logic — no reflection or dynamic code. Verify no new IL3050/IL2026 warnings via `dotnet publish -c Release` (both the CLI and the daemon, since the daemon is the consumer).

## Docs

No user-facing CLI surface change (internal daemon behavior), so no `README.md` / `help-*.txt` update. This spec records that the prior PR's deferred daemon follow-up is now closed.
