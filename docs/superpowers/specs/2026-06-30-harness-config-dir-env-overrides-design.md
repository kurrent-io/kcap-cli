# Harness config-directory env-var overrides

**Date:** 2026-06-30
**Status:** Approved

## Problem

`kcap setup` (and the read paths used by `kcap import`/`hook`/`status`) resolve each
coding agent's config directory from a hardcoded `~/.<agent>` location. When a user
relocates a harness's config via that harness's own environment variable, kcap reads
and writes the *wrong* directory: setup installs hooks/plugins where the harness will
never look, and imports read transcripts from a directory the harness no longer uses.

Claude Code (`CLAUDE_CONFIG_DIR`) and Codex (`CODEX_HOME`) have no override support at
all. An audit of every other supported harness — done against each tool's own docs and
source — surfaced one latent **bug** and two gaps:

| Harness | kcap today | Verified correct var | Verdict |
|---|---|---|---|
| Claude Code | — | `CLAUDE_CONFIG_DIR` (replaces `~/.claude` wholesale) | **Add** |
| Codex | — | `CODEX_HOME` (replaces `~/.codex`; Codex requires it to pre-exist) | **Add** |
| Gemini CLI | `GEMINI_HOME` | `GEMINI_CLI_HOME` | **Bug — fix** |
| OpenCode | `XDG_CONFIG_HOME` only | `OPENCODE_CONFIG_DIR` ?? `$XDG_CONFIG_HOME/opencode` | **Gap — add** |
| Pi | — | `PI_CODING_AGENT_DIR` (the `…/agent` leaf, tilde-expanded) | **Gap — add** |
| Cursor CLI | — | `CURSOR_CONFIG_DIR` relocates `cli-config.json`, **not** `hooks.json` | **Leave (intentional)** |
| Copilot CLI | `COPILOT_HOME` | `COPILOT_HOME` | Already correct |
| Kiro CLI | `KIRO_HOME` | `KIRO_HOME` | Already correct |

### Sources

- **Claude Code** `CLAUDE_CONFIG_DIR`: single directory, replaces `~/.claude` wholesale
  (`settings.json`, `projects/`, `.claude.json`). Project-local `.claude/settings.local.json`
  is still created per-repo even when set — so project-scope paths must stay repo-relative.
- **Codex** `CODEX_HOME`: `openai/codex` `codex-rs/utils/home-dir/src/lib.rs#find_codex_home`
  and <https://developers.openai.com/codex/environment-variables>. Single directory root for
  config/auth/logs/sessions; replaces `~/.codex`.
- **Gemini CLI** `GEMINI_CLI_HOME`: `google-gemini/gemini-cli`
  `packages/core/src/utils/paths.ts#homedir` + `config/storage.ts#getGlobalGeminiDir`. A search
  for `GEMINI_HOME` returns **0 hits** in the repo — it is not a real variable. `GEMINI_CLI_HOME`
  is the **parent** directory; config lands at `$GEMINI_CLI_HOME/.gemini`, *not* at the value
  itself.
- **OpenCode** `OPENCODE_CONFIG_DIR`: `anomalyco/opencode` (formerly `sst/opencode`)
  `packages/core/src/global.ts` — `config: Flag.OPENCODE_CONFIG_DIR ?? Path.config`, where
  `Path.config = $XDG_CONFIG_HOME/opencode`. `OPENCODE_CONFIG` is a single *file* layered on top
  (not a directory) and is deliberately not used by kcap.
- **Pi** `PI_CODING_AGENT_DIR`: `earendil-works/pi` `packages/coding-agent/src/config.ts#getAgentDir`
  — env value is tilde-expanded and used as the agent directory **verbatim** (the `~/.pi/agent`
  leaf). The `/agent` suffix is appended only on the default fallback. `PI_CONFIG_DIR` does *not*
  relocate the agent dir (issue #2390); `PI_HOME`/XDG are not honored.
- **Cursor CLI** `CURSOR_CONFIG_DIR`: relocates `cli-config.json` only; the Cursor hooks docs do
  not document any relocation for `hooks.json` (fixed at `~/.cursor/hooks.json`). kcap only writes
  `hooks.json`, so honoring `CURSOR_CONFIG_DIR` would point kcap at a directory Cursor does not read
  hooks from. Left unchanged on purpose.
- **Copilot** `COPILOT_HOME` and **Kiro** `KIRO_HOME` are documented and already implemented
  correctly; no change.

## Goal

Make every kcap path that targets a harness's **user-global** config directory honor that
harness's real relocation variable, so setup writes and kcap reads land in the same place the
harness actually uses.

## Non-goals

- **Daemon launchers** (`ClaudeLauncher` / `CodexLauncher`). `ClaudeLauncher` reads the user-global
  `~/.claude.json` via a hardcoded `PathHelpers.HomeDirectory + ".claude.json"` when overlaying
  config into remote-agent worktrees. Making that honor `$CLAUDE_CONFIG_DIR/.claude.json` is a
  separate concern (the daemon's runtime environment may not even carry the variable) and is
  deferred to a follow-up.
- **Project-local paths** — `<repo>/.claude/settings.local.json`, `<repo>/.codex/hooks.json`, and
  worktree overlays are intentionally repo-relative and must remain unaffected by these variables.
  This matches Claude Code's own hybrid behavior (it still writes project-local settings even with
  `CLAUDE_CONFIG_DIR` set).
- **Cursor** — see rationale above.
- **Codex `CODEX_SQLITE_HOME`, Copilot `COPILOT_CACHE_HOME`, OpenCode `XDG_DATA_HOME`** and other
  per-subtree overrides — kcap does not write to those subtrees during setup, and existing
  `XDG_DATA_HOME` handling in `OpenCodePaths.DataDir` is already correct and unchanged.

## Design

### Principle: the `*Paths` resolver is the single source of truth

Every config-directory location is computed in exactly one place — the harness's `*Paths` class.
`PluginEnvironment` currently *recomputes* the Claude and Codex roots itself
(`Path.Combine(HomeDirectory, ".claude" / ".codex")`), bypassing the resolvers. That second source
of truth is the reason a resolver-only fix would be incomplete. `PluginEnvironment` must **delegate**
to the resolvers instead.

### Override resolution shape

Each touched resolver follows the established Gemini/Copilot/Kiro pattern: **env var wins; fall back
to home + dot-dir.** Each gains an optional injectable override parameter (defaulting to the
`Environment.GetEnvironmentVariable(...)` read) so the override branch is testable without mutating
process environment.

#### Claude — `CLAUDE_CONFIG_DIR`

`ClaudePaths.Home` changes from a private property to:

```csharp
internal static string Home(string? home = null, string? configDir = null) {
    configDir ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    if (!string.IsNullOrWhiteSpace(configDir)) return configDir;   // replaces ~/.claude wholesale
    home ??= PathHelpers.HomeDirectory;
    return Path.Combine(home, ".claude");
}
```

`Projects`, `Plans`, `UserSettings`, and `ProjectDir` stay parameterless and call `Home()` (no args
→ `PathHelpers.HomeDirectory`), so all existing external callers are unchanged. `Home` is widened to
`internal` so `PluginEnvironment` can delegate.

#### Codex — `CODEX_HOME`

`CodexPaths.Home` changes from a public property to:

```csharp
public static string Home(string? home = null, string? codexHome = null) {
    codexHome ??= Environment.GetEnvironmentVariable("CODEX_HOME");
    if (!string.IsNullOrWhiteSpace(codexHome)) return codexHome;
    home ??= PathHelpers.HomeDirectory;
    return Path.Combine(home, ".codex");
}
```

`Sessions` and `UserHooksJson` stay parameterless and call `Home()`. The four production callers of
the old property become method calls (`CodexPaths.Home` → `CodexPaths.Home()`):
`CodexConfigToml.DefaultConfigPath`, `SetupCommand` (×2: `LegacyCodexSkillsDir`, `CodexConfigTomlPath`),
`UninstallCommand`, and the daemon's `CodexConfigWriter`. The `CodexPathsHomeIsolationTests` references
update the same way.

> Note: these callers are user-global Codex paths (`config.toml`, `~/.codex/skills`) and *should*
> follow `CODEX_HOME`. The daemon's `CodexConfigWriter.config.toml` write is user-global (not a
> worktree overlay), so it correctly follows too — unlike `ClaudeLauncher`'s `.claude.json`, which is
> out of scope.

#### Gemini — fix `GEMINI_HOME` → `GEMINI_CLI_HOME` (corrected semantics)

```csharp
public static string Root(string? home = null, string? geminiCliHome = null) {
    geminiCliHome ??= Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
    var baseDir = !string.IsNullOrEmpty(geminiCliHome)
        ? geminiCliHome
        : home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(baseDir, ".gemini");   // ALWAYS ends in .gemini
}
```

`GEMINI_CLI_HOME` is the *parent*: `GEMINI_CLI_HOME=/foo` ⇒ `/foo/.gemini`. The `geminiHome`
parameter on `Root`/`IsInstalled`/`SettingsJson`/`TmpDir` is renamed to `geminiCliHome` (all callers
are internal to `GeminiPaths`; `PluginEnvironment` passes only `home`). The class doc comment is
updated to reference `$GEMINI_CLI_HOME/.gemini`.

#### OpenCode — add `OPENCODE_CONFIG_DIR`

```csharp
public static string ConfigDir(string? home = null, string? configDir = null) {
    configDir ??= Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
    if (!string.IsNullOrEmpty(configDir)) return configDir;
    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "opencode");
    home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".config", "opencode");
}
```

Precedence: `OPENCODE_CONFIG_DIR` → `$XDG_CONFIG_HOME/opencode` → `~/.config/opencode`. `PluginsDir`,
`KcapPlugin`, `KcapPluginMarker`, and `IsInstalled` derive from `ConfigDir` and follow automatically.
`DataDir` is unchanged.

#### Pi — add `PI_CODING_AGENT_DIR` (agent-leaf semantics + tilde expansion)

The variable points at the **agent directory leaf** (the `~/.pi/agent` equivalent), so the override
lands on `AgentDir`, not `Root`:

```csharp
public static string AgentDir(string? home = null, string? agentDir = null) {
    agentDir ??= Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
    if (!string.IsNullOrEmpty(agentDir)) return ExpandTilde(agentDir, home);
    return Path.Combine(Root(home), "agent");
}
```

`ExpandTilde` resolves a leading `~` / `~/` against `home ?? UserProfile` to match Pi's
`expandTildePath`. `Root` (`~/.pi`) is unchanged (the variable does not relocate it). `SessionsDir`,
`ExtensionsDir`, `KcapExtension`, `KcapExtensionMarker`, and `IsInstalled` derive from `AgentDir`.

### PluginEnvironment delegation

```csharp
public string ClaudeHome => ClaudePaths.Home(HomeDirectory);
public string CodexHome  => CodexPaths.Home(HomeDirectory);
```

`ClaudeUserSettings`, `CodexUserHooksJson`, `CodexConfigTomlPath`, and `LegacyCodexSkills` already
derive from `ClaudeHome`/`CodexHome`, so they follow. Passing the injected `HomeDirectory` preserves
the AI-741 seam used by `PluginCommand*Tests` (which inject a fake home rather than mutating `HOME`);
the env var, read internally when the override param is null, still wins when set. Gemini, OpenCode,
and Pi already delegate to their `*Paths` classes via `HomeDirectory` and read their env var
internally, so no `PluginEnvironment` change is required for them.

### Coverage map

Both setup entry points and all read paths are covered by fixing the resolvers + `PluginEnvironment`:

- **Setup wizard** (`SetupCommand`): uses `ClaudePaths.UserSettings`, `CodexPaths.Home()`,
  `GeminiPaths.SettingsJson`, `OpenCodePaths.KcapPlugin`, `PiPaths.KcapExtension` directly → honor the
  overrides via internal env reads.
- **`kcap plugin install`** (`PluginCommand` via `PluginEnvironment`): honors via delegation.
- **`kcap import` / `hook` / `status`** (`ClaudeImportSource`, `CodexImportSource`,
  `GeminiImportSource`, `StatusCommand`, etc.): all resolve through the `*Paths` classes → follow
  automatically.
- **`CodexConfigToml` network opt-in** (`config.toml`): resolves via `CodexPaths.Home()` → follows.

## Testing

Per-resolver unit tests (mirroring `CodexPathsHomeIsolationTests`):

1. **Default** — no override ⇒ `~/.claude`, `~/.codex`, `~/.gemini`, `~/.config/opencode`,
   `~/.pi/agent`.
2. **Override via injected param** (deterministic, no env mutation) — and assert derived members
   follow (`Projects`/`UserSettings`; `Sessions`/`UserHooksJson`/`config.toml`; `PluginsDir`;
   `ExtensionsDir`/`KcapExtension`).
3. **Override via env var** — set the real env var, `[NotInParallel("HomeEnvVarMutation")]`,
   restore in `try/finally` — proving the production env read works.

Harness-specific assertions:

- **Gemini:** `GEMINI_CLI_HOME=/foo` ⇒ `Root == /foo/.gemini` (locks the corrected parent-dir
  semantics), and assert `GEMINI_HOME` is *not* honored.
- **OpenCode:** precedence — `OPENCODE_CONFIG_DIR` overrides `XDG_CONFIG_HOME`.
- **Pi:** env points at the `…/agent` leaf (no extra `/agent` appended) and leading `~` expands.
- **PluginEnvironment:** `ClaudeHome`/`CodexHome`/`CodexConfigTomlPath` track the resolver under
  override.

## Documentation

Per the repo's README-sync rule, the same PR updates:

- `README.md` — document the supported per-harness config-relocation environment variables (and that
  Cursor's hooks path is fixed).
- The relevant `src/Capacitor.Cli.Core/Resources/help-*.txt` (setup help) if it enumerates harness
  config locations.

## AOT

All changes are plain string/path logic — no reflection, no dynamic code. Verify no new IL3050/IL2026
warnings via `dotnet publish -c Release` after implementation.
