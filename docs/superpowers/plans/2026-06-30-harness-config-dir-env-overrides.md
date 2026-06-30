# Harness config-directory env-var overrides — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every kcap path that targets a coding-agent's user-global config directory honor that agent's real config-relocation environment variable, so `kcap setup` writes and `kcap import`/`hook`/`status` reads land where the agent actually looks.

**Architecture:** Each agent's `*Paths` resolver class is the single source of truth for its config root. Add an "env-var-first, fall back to home + dot-dir" override to each resolver (Claude, Codex, Gemini-fix, OpenCode, Pi), each with an injectable parameter for deterministic tests. `PluginEnvironment` — which currently *recomputes* the Claude/Codex roots itself — is changed to delegate to the resolvers so `kcap plugin install` honors the overrides too.

**Tech Stack:** .NET 10 (NativeAOT), C#, TUnit on Microsoft Testing Platform.

## Global Constraints

- **.NET 10, NativeAOT** — no reflection / dynamic code in changes. Verify no IL3050/IL2026 warnings via `dotnet publish -c Release` (build does NOT surface them).
- **Test runner:** TUnit. Run a test project as an executable: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`. Filter with `--treenode-filter` (glob `/Assembly/Namespace/Class/Test`), NOT `--filter`.
- **Env-var-mutating tests** must be `[NotInParallel("HomeEnvVarMutation")]` and restore the original value in `finally` (mirror `test/Capacitor.Cli.Tests.Unit/Codex/CodexPathsHomeIsolationTests.cs`).
- **TUnit assertion style:** `await Assert.That(actual).IsEqualTo(expected);`, methods are `async Task` marked `[Test]`.
- **Override precedence:** the env var (or non-null injected override param) always wins over the home fallback. Returned verbatim — no rooting/normalization (except Pi, which expands a leading `~`).
- **Verified env-var names (do not substitute):** Claude `CLAUDE_CONFIG_DIR` (replaces `~/.claude`), Codex `CODEX_HOME` (replaces `~/.codex`), Gemini `GEMINI_CLI_HOME` (PARENT — config is `$GEMINI_CLI_HOME/.gemini`), OpenCode `OPENCODE_CONFIG_DIR` (replaces `~/.config/opencode`), Pi `PI_CODING_AGENT_DIR` (the `…/agent` LEAF, tilde-expanded).
- **README-sync rule (CLAUDE.md):** user-facing CLI surface changes update `README.md` in the same PR. Done in Task 6.
- **Branch:** `feat/harness-config-dir-env-overrides` (already created; spec already committed).

---

### Task 1: Claude — `CLAUDE_CONFIG_DIR`

**Files:**
- Modify: `src/Capacitor.Cli.Core/ClaudePaths.cs:8-12` (convert `Home` property → method with override; update derived members)
- Modify: `src/Capacitor.Cli/Commands/PluginEnvironment.cs:28` (`ClaudeHome` delegates to resolver)
- Test: `test/Capacitor.Cli.Tests.Unit/ClaudePathsOverrideTests.cs` (create)

**Interfaces:**
- Produces: `ClaudePaths.Home(string? home = null, string? configDir = null) : string` — returns `configDir`/`$CLAUDE_CONFIG_DIR` verbatim when set, else `Path.Combine(home ?? PathHelpers.HomeDirectory, ".claude")`. `ClaudePaths.Projects`/`Plans`/`UserSettings` stay parameterless. `PluginEnvironment.ClaudeHome` ⇒ `ClaudePaths.Home(HomeDirectory)`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/ClaudePathsOverrideTests.cs`:

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudePathsOverrideTests {
    // Parallel-safe: configDir is non-null, so the CLAUDE_CONFIG_DIR env var is never read.
    [Test]
    public async Task Home_config_dir_param_wins_over_home() {
        await Assert.That(ClaudePaths.Home(home: "/fake/home", configDir: "/relocated/claude"))
            .IsEqualTo("/relocated/claude");
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Home_and_derived_members_resolve_default_then_env_override() {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try {
            // Default: no override -> ~/.claude under the injected home.
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            await Assert.That(ClaudePaths.Home(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".claude"));

            // Override via env var -> verbatim, and derived members follow.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-claude-cfg");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", relocated);
            await Assert.That(ClaudePaths.Home()).IsEqualTo(relocated);
            await Assert.That(ClaudePaths.Projects).IsEqualTo(Path.Combine(relocated, "projects"));
            await Assert.That(ClaudePaths.UserSettings).IsEqualTo(Path.Combine(relocated, "settings.json"));
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task PluginEnvironment_ClaudeHome_delegates_and_honors_override() {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var env = new PluginEnvironment("/fake/home", () => null, TextWriter.Null, TextWriter.Null);
        try {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            await Assert.That(env.ClaudeHome).IsEqualTo(Path.Combine("/fake/home", ".claude"));
            await Assert.That(env.ClaudeUserSettings)
                .IsEqualTo(Path.Combine("/fake/home", ".claude", "settings.json"));

            var relocated = Path.Combine(Path.GetTempPath(), "kcap-claude-pe");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", relocated);
            await Assert.That(env.ClaudeHome).IsEqualTo(relocated);
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePathsOverrideTests/*"`
Expected: COMPILE ERROR — `ClaudePaths.Home(...)` takes no arguments (currently a property). This confirms the test targets the new signature.

- [ ] **Step 3: Convert `ClaudePaths.Home` to an override-aware method**

In `src/Capacitor.Cli.Core/ClaudePaths.cs`, replace the `Home` property (line 8) and the three derived members (lines 10-12):

```csharp
    // Lazy: HOME may be mutated at runtime (tests inject a fake home), so
    // these must re-evaluate on every access, the same way AgentsPaths does.
    // A static-readonly initializer would bake in HOME at first touch and
    // ignore subsequent changes. CLAUDE_CONFIG_DIR (when set) replaces ~/.claude
    // wholesale — settings.json, projects/, plans/ all move under it.
    public static string Home(string? home = null, string? configDir = null) {
        configDir ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir)) return configDir;

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".claude");
    }

    public static string Projects     => Path.Combine(Home(), "projects");
    public static string Plans        => Path.Combine(Home(), "plans");
    public static string UserSettings => Path.Combine(Home(), "settings.json");
```

- [ ] **Step 4: Delegate `PluginEnvironment.ClaudeHome` to the resolver**

In `src/Capacitor.Cli/Commands/PluginEnvironment.cs`, change line 28:

```csharp
    public string ClaudeHome          => ClaudePaths.Home(HomeDirectory);
```

(`ClaudeUserSettings` on line 29 already derives from `ClaudeHome`, so it follows automatically.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePathsOverrideTests/*"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/ClaudePaths.cs src/Capacitor.Cli/Commands/PluginEnvironment.cs test/Capacitor.Cli.Tests.Unit/ClaudePathsOverrideTests.cs
git commit -m "feat: honor CLAUDE_CONFIG_DIR for Claude config location

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Codex — `CODEX_HOME`

**Files:**
- Modify: `src/Capacitor.Cli.Core/CodexPaths.cs:4-6` (convert `Home` property → method; update derived members)
- Modify: `src/Capacitor.Cli.Core/CodexConfigToml.cs:25` (`CodexPaths.Home` → `CodexPaths.Home()`)
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs:248,252` (`CodexPaths.Home` → `CodexPaths.Home()`)
- Modify: `src/Capacitor.Cli/Commands/UninstallCommand.cs:151` (`CodexPaths.Home` → `CodexPaths.Home()`)
- Modify: `src/Capacitor.Cli.Daemon/Services/CodexConfigWriter.cs:18` (`CodexPaths.Home` → `CodexPaths.Home()`)
- Modify: `src/Capacitor.Cli/Commands/PluginEnvironment.cs:30` (`CodexHome` delegates to resolver)
- Modify: `test/Capacitor.Cli.Tests.Unit/Codex/CodexPathsHomeIsolationTests.cs` (`CodexPaths.Home`/`Sessions`/`UserHooksJson` references → method calls where applicable)
- Test: `test/Capacitor.Cli.Tests.Unit/Codex/CodexPathsCodexHomeTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `CodexPaths.Home(string? home = null, string? codexHome = null) : string` — returns `codexHome`/`$CODEX_HOME` verbatim when set, else `Path.Combine(home ?? PathHelpers.HomeDirectory, ".codex")`. `CodexPaths.Sessions`/`UserHooksJson` stay parameterless. `PluginEnvironment.CodexHome` ⇒ `CodexPaths.Home(HomeDirectory)`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/Codex/CodexPathsCodexHomeTests.cs`:

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Codex;

public class CodexPathsCodexHomeTests {
    [Test]
    public async Task Home_codex_home_param_wins_over_home() {
        await Assert.That(CodexPaths.Home(home: "/fake/home", codexHome: "/relocated/codex"))
            .IsEqualTo("/relocated/codex");
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Home_and_derived_members_resolve_default_then_env_override() {
        var original = Environment.GetEnvironmentVariable("CODEX_HOME");
        try {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            await Assert.That(CodexPaths.Home(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".codex"));

            var relocated = Path.Combine(Path.GetTempPath(), "kcap-codex-cfg");
            Environment.SetEnvironmentVariable("CODEX_HOME", relocated);
            await Assert.That(CodexPaths.Home()).IsEqualTo(relocated);
            await Assert.That(CodexPaths.Sessions).IsEqualTo(Path.Combine(relocated, "sessions"));
            await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(Path.Combine(relocated, "hooks.json"));
        } finally {
            Environment.SetEnvironmentVariable("CODEX_HOME", original);
        }
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task PluginEnvironment_CodexHome_delegates_and_honors_override() {
        var original = Environment.GetEnvironmentVariable("CODEX_HOME");
        var env = new PluginEnvironment("/fake/home", () => null, TextWriter.Null, TextWriter.Null);
        try {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            await Assert.That(env.CodexHome).IsEqualTo(Path.Combine("/fake/home", ".codex"));
            await Assert.That(env.CodexConfigTomlPath)
                .IsEqualTo(Path.Combine("/fake/home", ".codex", "config.toml"));

            var relocated = Path.Combine(Path.GetTempPath(), "kcap-codex-pe");
            Environment.SetEnvironmentVariable("CODEX_HOME", relocated);
            await Assert.That(env.CodexHome).IsEqualTo(relocated);
            await Assert.That(env.CodexConfigTomlPath).IsEqualTo(Path.Combine(relocated, "config.toml"));
        } finally {
            Environment.SetEnvironmentVariable("CODEX_HOME", original);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexPathsCodexHomeTests/*"`
Expected: COMPILE ERROR — `CodexPaths.Home(...)` takes no arguments (currently a property).

- [ ] **Step 3: Convert `CodexPaths.Home` to an override-aware method**

In `src/Capacitor.Cli.Core/CodexPaths.cs`, replace lines 4-6:

```csharp
    // CODEX_HOME (when set) replaces ~/.codex wholesale — Codex's own
    // find_codex_home() reads it first. Lazy so test HOME injection re-evaluates.
    public static string Home(string? home = null, string? codexHome = null) {
        codexHome ??= Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome)) return codexHome;

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".codex");
    }

    public static string Sessions      => Path.Combine(Home(), "sessions");
    public static string UserHooksJson => Path.Combine(Home(), "hooks.json");
```

- [ ] **Step 4: Update the four production call sites (property → method)**

`src/Capacitor.Cli.Core/CodexConfigToml.cs:25`:
```csharp
    static string DefaultConfigPath => Path.Combine(CodexPaths.Home(), "config.toml");
```

`src/Capacitor.Cli/Commands/SetupCommand.cs:248` and `:252`:
```csharp
            LegacyCodexSkillsDir: Path.Combine(CodexPaths.Home(), "skills"),
```
```csharp
            CodexConfigTomlPath:  Path.Combine(CodexPaths.Home(), "config.toml"));
```

`src/Capacitor.Cli/Commands/UninstallCommand.cs:151`:
```csharp
        if (!SweepCapacitorPrefixedDirs(Path.Combine(CodexPaths.Home(), "skills"))) hadFailures = true;
```

`src/Capacitor.Cli.Daemon/Services/CodexConfigWriter.cs:18`:
```csharp
                Path.Combine(CodexPaths.Home(), "config.toml"));
```

- [ ] **Step 5: Delegate `PluginEnvironment.CodexHome` to the resolver**

In `src/Capacitor.Cli/Commands/PluginEnvironment.cs`, change line 30:
```csharp
    public string CodexHome           => CodexPaths.Home(HomeDirectory);
```
(`CodexUserHooksJson`, `CodexConfigTomlPath`, `LegacyCodexSkills` on lines 31-32, 41 already derive from `CodexHome`, so they follow.)

- [ ] **Step 6: Update the existing isolation tests (property → method)**

In `test/Capacitor.Cli.Tests.Unit/Codex/CodexPathsHomeIsolationTests.cs`, the `Home_reflects_current_HOME_env_var` test uses `CodexPaths.Home` as a property on lines 14 and 17. Change both:
```csharp
            _ = CodexPaths.Home();
```
```csharp
            await Assert.That(CodexPaths.Home()).IsEqualTo(Path.Combine(tmp.FullName, ".codex"));
```
(`Sessions` and `UserHooksJson` remain properties — no change to those two tests.)

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexPathsCodexHomeTests/*"`
Then: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexPathsHomeIsolationTests/*"`
Expected: PASS (3 + 3 tests).

- [ ] **Step 8: Build the daemon + CLI to confirm call-site updates compile**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj && dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.Cli.Core/CodexPaths.cs src/Capacitor.Cli.Core/CodexConfigToml.cs src/Capacitor.Cli/Commands/SetupCommand.cs src/Capacitor.Cli/Commands/UninstallCommand.cs src/Capacitor.Cli.Daemon/Services/CodexConfigWriter.cs src/Capacitor.Cli/Commands/PluginEnvironment.cs test/Capacitor.Cli.Tests.Unit/Codex/
git commit -m "feat: honor CODEX_HOME for Codex config location

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Gemini — fix `GEMINI_HOME` → `GEMINI_CLI_HOME` (corrected parent-dir semantics)

**Files:**
- Modify: `src/Capacitor.Cli.Core/Gemini/GeminiPaths.cs:4-16` (class doc + `Root`; rename `geminiHome` param → `geminiCliHome` in `Root`/`IsInstalled`/`SettingsJson`/`TmpDir`)
- Test: `test/Capacitor.Cli.Tests.Unit/GeminiPathsTests.cs` (create)

**Interfaces:**
- Produces: `GeminiPaths.Root(string? home = null, string? geminiCliHome = null) : string` — returns `Path.Combine(geminiCliHome, ".gemini")` when the env/param is set (PARENT-dir semantics), else `Path.Combine(home ?? UserProfile, ".gemini")`. Reads `GEMINI_CLI_HOME` (NOT `GEMINI_HOME`).

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/GeminiPathsTests.cs`:

```csharp
using Capacitor.Cli.Core.Gemini;

namespace Capacitor.Cli.Tests.Unit;

public class GeminiPathsTests {
    // Parallel-safe: the override param is non-null, so no env var is read.
    [Test]
    public async Task Root_gemini_cli_home_param_is_parent_of_dot_gemini() {
        await Assert.That(GeminiPaths.Root(home: "/fake/home", geminiCliHome: "/foo"))
            .IsEqualTo(Path.Combine("/foo", ".gemini"));
    }

    [Test]
    public async Task Root_defaults_to_dot_gemini_under_home() {
        await Assert.That(GeminiPaths.Root(home: "/fake/home", geminiCliHome: null))
            .IsEqualTo(Path.Combine("/fake/home", ".gemini"));
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Root_reads_GEMINI_CLI_HOME_and_ignores_GEMINI_HOME() {
        var originalCli = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        var originalOld = Environment.GetEnvironmentVariable("GEMINI_HOME");
        try {
            // The defunct GEMINI_HOME must NOT be honored.
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", null);
            Environment.SetEnvironmentVariable("GEMINI_HOME", "/should/be/ignored");
            await Assert.That(GeminiPaths.Root(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".gemini"));

            // GEMINI_CLI_HOME is the parent of .gemini, and SettingsJson follows.
            var parent = Path.Combine(Path.GetTempPath(), "kcap-gemini-cfg");
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", parent);
            await Assert.That(GeminiPaths.Root()).IsEqualTo(Path.Combine(parent, ".gemini"));
            await Assert.That(GeminiPaths.SettingsJson())
                .IsEqualTo(Path.Combine(parent, ".gemini", "settings.json"));
        } finally {
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", originalCli);
            Environment.SetEnvironmentVariable("GEMINI_HOME", originalOld);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GeminiPathsTests/*"`
Expected: COMPILE ERROR (`geminiCliHome` parameter does not exist) — or, after the param rename, FAIL on `Root_reads_GEMINI_CLI_HOME...` because the old code reads `GEMINI_HOME` and returns the value un-suffixed.

- [ ] **Step 3: Fix the class doc comment**

In `src/Capacitor.Cli.Core/Gemini/GeminiPaths.cs`, replace lines 4-6 of the `<summary>`:

```csharp
/// Filesystem layout for Google Gemini CLI state. Everything lives under a
/// single root: <c>$GEMINI_CLI_HOME/.gemini</c> when <c>GEMINI_CLI_HOME</c> is
/// set (it names the PARENT dir, not the .gemini dir itself), otherwise
/// <c>~/.gemini</c> on every OS. Note: <c>GEMINI_HOME</c> is NOT a real Gemini
/// CLI variable and is intentionally not honored. Unlike Copilot's dedicated
/// <c>hooks/kcap.json</c>, Gemini's hooks live in the SHARED <c>settings.json</c>
/// under a <c>hooks</c> key, so the installer must MERGE (see
/// <see cref="GeminiHooksParser"/>).
```

- [ ] **Step 4: Fix `Root` (name + semantics) and rename the param everywhere**

Replace the `Root` body (lines 11-17):
```csharp
    public static string Root(string? home = null, string? geminiCliHome = null) {
        geminiCliHome ??= Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");

        var baseDir = !string.IsNullOrEmpty(geminiCliHome)
            ? geminiCliHome
            : home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(baseDir, ".gemini");   // GEMINI_CLI_HOME is the PARENT of .gemini
    }
```

Then rename the `geminiHome` parameter to `geminiCliHome` in `IsInstalled`, `SettingsJson`, and `TmpDir` (lines 25-26, 32-33, 39-40), updating both the signatures and the `Root(home, geminiCliHome)` call inside each:
```csharp
    public static bool IsInstalled(string? home = null, string? geminiCliHome = null)
        => Directory.Exists(Root(home, geminiCliHome));
```
```csharp
    public static string SettingsJson(string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "settings.json");
```
```csharp
    public static string TmpDir(string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "tmp");
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GeminiPathsTests/*"`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the existing Gemini tests to confirm no regression**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GeminiHooksTests/*"`
Then: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GeminiImportSourceTests/*"`
Expected: PASS (no references to the renamed `geminiHome` param exist outside `GeminiPaths.cs`, so these are unaffected).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/Gemini/GeminiPaths.cs test/Capacitor.Cli.Tests.Unit/GeminiPathsTests.cs
git commit -m "fix: use GEMINI_CLI_HOME (parent of .gemini), not bogus GEMINI_HOME

GEMINI_HOME is not a real Gemini CLI variable; the CLI reads
GEMINI_CLI_HOME and treats it as the parent of .gemini.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: OpenCode — `OPENCODE_CONFIG_DIR`

**Files:**
- Modify: `src/Capacitor.Cli.Core/OpenCode/OpenCodePaths.cs:13-21` (`ConfigDir` gains `OPENCODE_CONFIG_DIR` precedence + injectable param)
- Test: `test/Capacitor.Cli.Tests.Unit/OpenCodePathsTests.cs` (create)

**Interfaces:**
- Produces: `OpenCodePaths.ConfigDir(string? home = null, string? configDir = null) : string` — precedence `OPENCODE_CONFIG_DIR` → `$XDG_CONFIG_HOME/opencode` → `~/.config/opencode`. `PluginsDir`/`KcapPlugin`/`IsInstalled` derive from it.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/OpenCodePathsTests.cs`:

```csharp
using Capacitor.Cli.Core.OpenCode;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodePathsTests {
    // Parallel-safe: override param is non-null, so no env var is read.
    [Test]
    public async Task ConfigDir_param_wins_over_home() {
        await Assert.That(OpenCodePaths.ConfigDir(home: "/fake/home", configDir: "/relocated/oc"))
            .IsEqualTo("/relocated/oc");
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task ConfigDir_precedence_OPENCODE_CONFIG_DIR_over_XDG_over_home() {
        var originalCfg = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try {
            // Default: neither set -> ~/.config/opencode under home.
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
            await Assert.That(OpenCodePaths.ConfigDir(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".config", "opencode"));

            // XDG only -> $XDG_CONFIG_HOME/opencode.
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/xdg");
            await Assert.That(OpenCodePaths.ConfigDir(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/xdg", "opencode"));

            // OPENCODE_CONFIG_DIR wins over XDG, verbatim, and KcapPlugin follows.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-oc-cfg");
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", relocated);
            await Assert.That(OpenCodePaths.ConfigDir()).IsEqualTo(relocated);
            await Assert.That(OpenCodePaths.KcapPlugin())
                .IsEqualTo(Path.Combine(relocated, "plugins", "kcap.ts"));
        } finally {
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", originalCfg);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/OpenCodePathsTests/*"`
Expected: COMPILE ERROR — `ConfigDir(...)` has no `configDir` parameter.

- [ ] **Step 3: Add `OPENCODE_CONFIG_DIR` precedence**

In `src/Capacitor.Cli.Core/OpenCode/OpenCodePaths.cs`, replace the `ConfigDir` method (lines 13-21):

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

(`PluginsDir`, `KcapPlugin`, `KcapPluginMarker`, and `IsInstalled` call `ConfigDir(home)` — the new `configDir` param defaults to null, so they compile unchanged and follow the override via the internal env read.)

- [ ] **Step 4: Update the class doc comment**

In `src/Capacitor.Cli.Core/OpenCode/OpenCodePaths.cs`, update the `<summary>` line mentioning config so it reads:
```csharp
/// plugins from <c>~/.config/opencode/plugins/</c> (honoring
/// <c>OPENCODE_CONFIG_DIR</c>, then <c>XDG_CONFIG_HOME</c>); session data lives
/// under <c>~/.local/share/opencode</c> (honoring <c>XDG_DATA_HOME</c>).
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/OpenCodePathsTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/OpenCode/OpenCodePaths.cs test/Capacitor.Cli.Tests.Unit/OpenCodePathsTests.cs
git commit -m "feat: honor OPENCODE_CONFIG_DIR for OpenCode plugin location

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Pi — `PI_CODING_AGENT_DIR`

**Files:**
- Modify: `src/Capacitor.Cli.Core/Pi/PiPaths.cs:14-20` (`AgentDir` gains `PI_CODING_AGENT_DIR` + tilde expansion + injectable param; add private `ExpandTilde`)
- Test: `test/Capacitor.Cli.Tests.Unit/PiPathsTests.cs` (create)

**Interfaces:**
- Produces: `PiPaths.AgentDir(string? home = null, string? agentDir = null) : string` — returns the tilde-expanded `agentDir`/`$PI_CODING_AGENT_DIR` (the `…/agent` LEAF, used verbatim) when set, else `Path.Combine(Root(home), "agent")`. `SessionsDir`/`ExtensionsDir`/`KcapExtension`/`IsInstalled` derive from it. `Root` is unchanged.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/PiPathsTests.cs`:

```csharp
using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Tests.Unit;

public class PiPathsTests {
    // Parallel-safe: override param is non-null, so no env var is read.
    [Test]
    public async Task AgentDir_param_is_used_verbatim_as_the_agent_leaf() {
        await Assert.That(PiPaths.AgentDir(home: "/fake/home", agentDir: "/custom/agent"))
            .IsEqualTo("/custom/agent");
    }

    [Test]
    public async Task AgentDir_expands_leading_tilde_against_home() {
        await Assert.That(PiPaths.AgentDir(home: "/fake/home", agentDir: "~/pi/agent"))
            .IsEqualTo(Path.Combine("/fake/home", "pi", "agent"));
    }

    [Test]
    public async Task AgentDir_defaults_to_dot_pi_agent_under_home() {
        await Assert.That(PiPaths.AgentDir(home: "/fake/home", agentDir: null))
            .IsEqualTo(Path.Combine("/fake/home", ".pi", "agent"));
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task AgentDir_reads_PI_CODING_AGENT_DIR_and_derived_members_follow() {
        var original = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        try {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
            await Assert.That(PiPaths.AgentDir(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".pi", "agent"));

            // Env value is the agent leaf (NO extra /agent appended); extensions follow.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-pi-agent");
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", relocated);
            await Assert.That(PiPaths.AgentDir()).IsEqualTo(relocated);
            await Assert.That(PiPaths.KcapExtension())
                .IsEqualTo(Path.Combine(relocated, "extensions", "kcap.ts"));
            await Assert.That(PiPaths.SessionsDir())
                .IsEqualTo(Path.Combine(relocated, "sessions"));
        } finally {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", original);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PiPathsTests/*"`
Expected: COMPILE ERROR — `AgentDir(...)` has no `agentDir` parameter.

- [ ] **Step 3: Add the override + tilde expansion to `AgentDir`**

In `src/Capacitor.Cli.Core/Pi/PiPaths.cs`, replace `AgentDir` (line 19):

```csharp
    /// <summary>
    /// Agent state dir. <c>PI_CODING_AGENT_DIR</c> (when set) relocates THIS leaf
    /// directly — Pi uses the env value verbatim (tilde-expanded) as the agent
    /// dir; the <c>/agent</c> suffix is appended only on the default fallback.
    /// </summary>
    public static string AgentDir(string? home = null, string? agentDir = null) {
        agentDir ??= Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        if (!string.IsNullOrEmpty(agentDir)) return ExpandTilde(agentDir, home);

        return Path.Combine(Root(home), "agent");
    }

    /// <summary>Expand a leading <c>~</c>/<c>~/</c> against <paramref name="home"/>
    /// (or the OS user profile), matching Pi's <c>expandTildePath</c>.</summary>
    static string ExpandTilde(string path, string? home) {
        if (path != "~" && !path.StartsWith("~/") && !path.StartsWith("~\\")) return path;

        var baseDir = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length <= 1 ? baseDir : Path.Combine(baseDir, path[2..]);
    }
```

(`SessionsDir`, `ExtensionsDir`, `KcapExtension`, `KcapExtensionMarker`, and `IsInstalled` call `AgentDir(home)` — the new `agentDir` param defaults to null, so they compile unchanged and follow the override.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PiPathsTests/*"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Pi/PiPaths.cs test/Capacitor.Cli.Tests.Unit/PiPathsTests.cs
git commit -m "feat: honor PI_CODING_AGENT_DIR for Pi extension location

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Documentation — README + help text

**Files:**
- Modify: `src/Capacitor.Cli.Core/Resources/help-plugin.txt:21` (fix `$GEMINI_HOME` → `$GEMINI_CLI_HOME`)
- Modify: `README.md` (per-harness env-var mentions, mirroring the existing inline "honours `$X`" style)

**Interfaces:** none (docs only). Deliverable is verifiable by `grep`.

- [ ] **Step 1: Fix the wrong env var in help-plugin.txt**

In `src/Capacitor.Cli.Core/Resources/help-plugin.txt`, line 21 currently reads `~/.gemini/settings.json (honours $GEMINI_HOME). User-authored`. Change `$GEMINI_HOME` to `$GEMINI_CLI_HOME`:
```
~/.gemini/settings.json (honours $GEMINI_CLI_HOME). User-authored
```

- [ ] **Step 2: Add Claude + Codex env-var mention to the setup detection note**

In `README.md`, find the line beginning `4. **Coding-agent hooks** — detects Claude Code and Codex CLI on `PATH`...` (around line 65). Append one sentence at the end of that bullet:
```
 Each agent's own config-relocation environment variable is honored when set: `CLAUDE_CONFIG_DIR` (Claude), `CODEX_HOME` (Codex), `GEMINI_CLI_HOME` (Gemini — names the parent of `.gemini`), `KIRO_HOME` (Kiro), `COPILOT_HOME` (Copilot), `OPENCODE_CONFIG_DIR` (OpenCode), and `PI_CODING_AGENT_DIR` (Pi). Cursor's hooks path is fixed at `~/.cursor/hooks.json` and is not relocated.
```

- [ ] **Step 3: Fix the Gemini import-section env reference (if present) and add Pi/OpenCode/Codex/Claude inline mentions**

In `README.md`, the "Loading historical sessions" section paths sentence (around line 279) lists per-agent transcript dirs. After the existing Kiro line that says "Set `KIRO_HOME` to point at a non-default location.", add an analogous note covering the rest. Insert a new paragraph after the Kiro historical-import paragraph (around line 307):
```
Claude (`CLAUDE_CONFIG_DIR`), Codex (`CODEX_HOME`), Gemini (`GEMINI_CLI_HOME`, which names the parent of `.gemini`), OpenCode (`OPENCODE_CONFIG_DIR`), and Pi (`PI_CODING_AGENT_DIR`, which names the `~/.pi/agent` leaf) historical/live paths follow each agent's own config-relocation environment variable when it is set, so a relocated config is discovered automatically.
```

- [ ] **Step 4: Verify the docs are internally consistent**

Run: `grep -n "GEMINI_HOME" README.md src/Capacitor.Cli.Core/Resources/help-plugin.txt`
Expected: NO matches for the bare `GEMINI_HOME` (only `GEMINI_CLI_HOME` should remain).
Run: `grep -rn "CLAUDE_CONFIG_DIR\|CODEX_HOME\|GEMINI_CLI_HOME\|OPENCODE_CONFIG_DIR\|PI_CODING_AGENT_DIR" README.md`
Expected: all five present.

- [ ] **Step 5: Commit**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/help-plugin.txt
git commit -m "docs: document per-harness config-dir env vars; fix GEMINI_HOME ref

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Full verification gate (build, tests, AOT)

**Files:** none (verification only).

- [ ] **Step 1: Run the full unit-test suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all tests PASS (the new path tests plus all existing tests; pay attention to `PluginCommandClaudeTests`/`PluginCommandCodexTests`, `CodexConfigWriterTests`, `CodexPathsHomeIsolationTests`, and the Gemini suites — none should regress).

- [ ] **Step 2: AOT publish and check for trimming warnings**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no IL warnings"`
Expected: `no IL warnings`.

- [ ] **Step 3: Confirm the daemon still builds (it consumed the CodexPaths.Home change)**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 4: Final commit (only if any verification fix was needed)**

If steps 1-3 surfaced a fix, commit it:
```bash
git add -A
git commit -m "fix: address verification findings for config-dir overrides

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
Otherwise, no commit — the feature is complete.

---

## Self-Review

**Spec coverage:**
- Claude `CLAUDE_CONFIG_DIR` → Task 1. ✓
- Codex `CODEX_HOME` (+ all 4 callers + daemon `CodexConfigWriter` + `PluginEnvironment`) → Task 2. ✓
- Gemini `GEMINI_HOME`→`GEMINI_CLI_HOME` parent-dir fix → Task 3. ✓
- OpenCode `OPENCODE_CONFIG_DIR` precedence → Task 4. ✓
- Pi `PI_CODING_AGENT_DIR` agent-leaf + tilde → Task 5. ✓
- `PluginEnvironment` delegation (Claude+Codex) → Tasks 1 & 2. ✓
- Cursor intentionally unchanged → no task (documented in Task 6 README note). ✓
- Coverage map (setup wizard via `*Paths` directly; `kcap plugin` via `PluginEnvironment`; import/read via `*ImportSource`/`*Paths`) → exercised by Task 1-5 derived-member assertions + Task 7 full suite. ✓
- Testing approach (param-based deterministic + env-based `[NotInParallel]`) → every task. ✓
- Docs (README + help text) → Task 6. ✓
- AOT verification → Task 7. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; every test step shows complete test code and the exact run command + expected outcome.

**Type consistency:** `Home(string? home = null, string? configDir = null)` (Claude) / `Home(string? home = null, string? codexHome = null)` (Codex) / `Root(string? home = null, string? geminiCliHome = null)` (Gemini) / `ConfigDir(string? home = null, string? configDir = null)` (OpenCode) / `AgentDir(string? home = null, string? agentDir = null)` (Pi) — names and signatures match between the implementation steps, the interface blocks, and the tests. `PluginEnvironment.ClaudeHome`/`CodexHome` delegation matches the resolver signatures (single `home` arg).
