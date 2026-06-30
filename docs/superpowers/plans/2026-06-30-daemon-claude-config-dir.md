# Daemon `CLAUDE_CONFIG_DIR` for `~/.claude.json` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the daemon's `ClaudeLauncher` read/write the user-global `~/.claude.json` at the location Claude Code actually uses under `CLAUDE_CONFIG_DIR` relocation, so worktree trust and MCP-server propagation work for relocated configs.

**Architecture:** Add a single resolver `ClaudePaths.UserConfigJson(...)` that encodes the empirically-verified `.claude.json` location (`$CLAUDE_CONFIG_DIR/.claude.json` when set, else `$HOME/.claude.json`), then point `ClaudeLauncher`'s two hardcoded `Path.Combine(PathHelpers.HomeDirectory, ".claude.json")` computations at it. No env-forwarding is added — the spawned `claude` already inherits `CLAUDE_CONFIG_DIR` from the daemon.

**Tech Stack:** .NET 10 (NativeAOT), C#, TUnit on Microsoft Testing Platform.

## Global Constraints

- **.NET 10, NativeAOT** — no reflection / dynamic code. AOT warnings only surface on `dotnet publish -c Release` (not `dotnet build`); verify both the CLI and the **daemon** (the consumer).
- **Test runner:** TUnit. Run a test project as an executable: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`. Filter with `--treenode-filter` (glob `/Assembly/Namespace/Class/Test`), NOT `--filter`.
- **Env-var-mutating tests** must be `[NotInParallel("HomeEnvVarMutation")]` and restore the original value in a `finally` block (mirror the existing tests in `ClaudePathsOverrideTests.cs`).
- **TUnit assertion style:** `await Assert.That(actual).IsEqualTo(expected);`, methods `async Task` marked `[Test]`.
- **Verified `.claude.json` location (Claude Code 2.1.196, empirical):** `CLAUDE_CONFIG_DIR` set ⇒ `$CLAUDE_CONFIG_DIR/.claude.json` (INSIDE the config dir); unset ⇒ `$HOME/.claude.json` (SIBLING of `~/.claude`). Base = `CLAUDE_CONFIG_DIR ?? $HOME`, then `.claude.json`. This is deliberately NOT `Path.Combine(Home(), ".claude.json")`.
- **`ClaudePaths` is `internal`** with `InternalsVisibleTo` for `kcap`, `kcap-daemon`, and the test assemblies — a `public` member on it is reachable from the daemon and tests.
- **No env-forwarding to the child** — the Unix PTY fork keeps the daemon env and does not unset `CLAUDE_CONFIG_DIR`; Windows ConPty copies the full env; the daemon inherits the user shell env at launch. Daemon and child resolve the same file. Do not add `CLAUDE_CONFIG_DIR` to the spawn `extraEnv` dict.
- **No user-facing CLI surface change** → no `README.md` / `help-*.txt` update.
- **Branch:** `feat/daemon-claude-config-dir` (already created; spec already committed).

---

### Task 1: `ClaudePaths.UserConfigJson` resolver + tests

**Files:**
- Modify: `src/Capacitor.Cli.Core/ClaudePaths.cs` (add `UserConfigJson` after `UserSettings`, line 19)
- Test: `test/Capacitor.Cli.Tests.Unit/ClaudePathsOverrideTests.cs` (add tests to the existing class)

**Interfaces:**
- Consumes: nothing.
- Produces: `ClaudePaths.UserConfigJson(string? home = null, string? configDir = null) : string` — returns `Path.Combine(configDir, ".claude.json")` when `configDir`/`$CLAUDE_CONFIG_DIR` is set, else `Path.Combine(home ?? PathHelpers.HomeDirectory, ".claude.json")`. (Task 2 calls `ClaudePaths.UserConfigJson()`.)

- [ ] **Step 1: Write the failing tests**

Append these tests to the existing `ClaudePathsOverrideTests` class in `test/Capacitor.Cli.Tests.Unit/ClaudePathsOverrideTests.cs` (the file already has `using Capacitor.Cli.Core;` and `using Capacitor.Cli.Commands;`):

```csharp
    [Test]
    public async Task UserConfigJson_config_dir_param_puts_json_inside_the_config_dir() {
        // Override param is non-null -> env var never read (parallel-safe).
        await Assert.That(ClaudePaths.UserConfigJson(home: "/fake/home", configDir: "/relocated"))
            .IsEqualTo(Path.Combine("/relocated", ".claude.json"));
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task UserConfigJson_default_is_sibling_of_claude_dir_and_env_relocates_inside() {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try {
            // Default: <home>/.claude.json (NOT <home>/.claude/.claude.json).
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            await Assert.That(ClaudePaths.UserConfigJson(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".claude.json"));

            // Override via env var: .claude.json lives INSIDE the config dir.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-claude-cfg");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", relocated);
            await Assert.That(ClaudePaths.UserConfigJson())
                .IsEqualTo(Path.Combine(relocated, ".claude.json"));
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePathsOverrideTests/UserConfigJson*"`
Expected: COMPILE ERROR — `ClaudePaths` does not contain a definition for `UserConfigJson`.

- [ ] **Step 3: Add the resolver**

In `src/Capacitor.Cli.Core/ClaudePaths.cs`, insert after line 19 (`public static string UserSettings => ...`):

```csharp

    /// <summary>
    /// Path to Claude's user-global config FILE (account/OAuth, MCP servers,
    /// per-project trust flags under <c>projects[path]</c>). Its base differs
    /// from <see cref="Home"/>: with CLAUDE_CONFIG_DIR set it lives INSIDE the
    /// config dir (<c>$CLAUDE_CONFIG_DIR/.claude.json</c>); by default it is a
    /// SIBLING of <c>~/.claude</c> (<c>$HOME/.claude.json</c>). Verified against
    /// Claude Code 2.1.196 — do NOT collapse this into Path.Combine(Home(), …).
    /// </summary>
    public static string UserConfigJson(string? home = null, string? configDir = null) {
        configDir ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir)) return Path.Combine(configDir, ".claude.json");

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".claude.json");
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePathsOverrideTests/UserConfigJson*"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/ClaudePaths.cs test/Capacitor.Cli.Tests.Unit/ClaudePathsOverrideTests.cs
git commit -m "feat: add ClaudePaths.UserConfigJson honoring CLAUDE_CONFIG_DIR

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `ClaudeLauncher` uses the resolver + verification

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs:215` (in `TrustWorktreeInClaudeConfig`) and `:337` (in `WriteMcpConfig`)

**Interfaces:**
- Consumes: `ClaudePaths.UserConfigJson()` from Task 1.
- Produces: nothing downstream.

- [ ] **Step 1: Swap the trust-write call site**

In `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs`, inside `TrustWorktreeInClaudeConfig` (around line 215), replace:

```csharp
            var claudeJsonPath = Path.Combine(PathHelpers.HomeDirectory, ".claude.json");
```

with:

```csharp
            // The spawned `claude` inherits CLAUDE_CONFIG_DIR from the daemon's
            // environment, so it reads/writes the same file we resolve here —
            // $CLAUDE_CONFIG_DIR/.claude.json when set, else ~/.claude.json.
            var claudeJsonPath = ClaudePaths.UserConfigJson();
```

- [ ] **Step 2: Swap the MCP-read call site**

In the same file, inside `WriteMcpConfig` (around line 337), replace:

```csharp
        var claudeJsonPath = Path.Combine(PathHelpers.HomeDirectory, ".claude.json");
```

with:

```csharp
        var claudeJsonPath = ClaudePaths.UserConfigJson();
```

- [ ] **Step 3: Confirm no hardcoded `.claude.json` home path remains**

Run: `grep -n 'PathHelpers.HomeDirectory, ".claude.json"' src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs || echo "none remaining"`
Expected: `none remaining`.

(Note: `PathHelpers` may still be referenced elsewhere in the file, and `using Capacitor.Cli.Core;` covers both `PathHelpers` and `ClaudePaths`, so no `using` cleanup is needed. If the build reports an unused-using or unreferenced symbol, leave the `using` — it is still required for `ClaudePaths`.)

- [ ] **Step 4: Build the daemon (the consumer of the change)**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run the full unit suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all tests PASS (the new `UserConfigJson` tests plus all existing tests).

- [ ] **Step 6: AOT publish warning check (CLI + daemon)**

Run:
```
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no IL warnings (CLI)"
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no IL warnings (daemon)"
```
Expected: `no IL warnings (CLI)` and `no IL warnings (daemon)`.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs
git commit -m "fix: daemon resolves ~/.claude.json via CLAUDE_CONFIG_DIR

ClaudeLauncher's worktree-trust write and MCP-server merge now target
\$CLAUDE_CONFIG_DIR/.claude.json (matching the spawned claude) instead of
the hardcoded ~/.claude.json.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- New resolver `ClaudePaths.UserConfigJson` with the verified base logic → Task 1. ✓
- `ClaudeLauncher` lines 215 & 337 use the resolver → Task 2 (steps 1-2). ✓
- Clarifying comment about child inheriting `CLAUDE_CONFIG_DIR` → Task 2 step 1. ✓
- No env-forwarding (explicitly not done) → Global Constraints + comment; no task adds it. ✓
- Testing: `UserConfigJson` unit tests covering default (sibling), override-param (inside), env override, and the regression guard (default is `<home>/.claude.json`, not `<home>/.claude/.claude.json`) → Task 1 (the env test asserts both default and override; the regression guard is the default assertion). ✓
- Launcher coverage rests on the resolver tests + unchanged default behavior + the grep guard (no cheap seam exists for the `static void` file-I/O methods) → Task 2 step 3, as the spec permits. ✓
- AOT verification on CLI + daemon → Task 2 step 6. ✓
- No README/help change → Global Constraints. ✓

**Placeholder scan:** No TBD/TODO. Every code step shows complete code; every test/command step shows the exact command and expected output.

**Type consistency:** `UserConfigJson(string? home = null, string? configDir = null)` is named and called identically in the interface block, the implementation (Task 1 step 3), the tests (Task 1 step 1), and the launcher call sites (Task 2, called as `ClaudePaths.UserConfigJson()` with no args). Matches the existing sibling `Home(string? home = null, string? configDir = null)`.
