# AI-734 Hooks Auto-Refresh on npm Upgrade — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the PR #102 skills postinstall pattern to refresh Codex hooks and Claude plugin registration on every `npm install -g @kurrent/kapacitor`, gated by per-vendor `--if-installed` markers, so users picking up CLI upgrades never see hook calls silently fail.

**Architecture:** Mirror `AgentsSkillsInstaller` (marker + `IsInstalled` + same-version short-circuit + pre-marker detection) per-vendor in two new core classes — `CodexHooksInstaller` and `ClaudePluginInstaller`. Add `--if-installed` semantics to the existing `kapacitor plugin install --codex` and the default Claude branch of `kapacitor plugin install`. Extend `npm/kapacitor/bin/postinstall.js` to fan-out three refresh calls (skills, codex, claude) after a global install, with the same fail-open / timeout safeguards.

**Tech Stack:** C# (.NET 10 NativeAOT), TUnit for tests, Node.js postinstall script, `System.Text.Json.Nodes` for hooks.json / settings.json mutation.

**Out of scope:** Cursor hooks (AI-730 will land its own `CursorHooksInstaller` writing `~/.cursor/.kapacitor-hooks-version` — this plan only sets the pattern). Project-scope installs are *not* auto-refreshed; the postinstall is global-only by design, and project scope is a manual opt-in workflow.

---

## File Structure

**New files:**

- `src/Kapacitor.Cli.Core/KapacitorVersion.cs`
  - Single source of truth for the version string stamped into every marker. Replaces the per-installer `CurrentVersion()` so a future rename can't drift between installers.
- `src/Kapacitor.Cli.Core/CodexHooksInstaller.cs`
  - Owns `MarkerFileName`, `IsInstalled`, `ReadMarker`, `WriteMarker`, `DeleteMarker` for `~/.codex/.kapacitor-hooks-version`. `IsInstalled` returns true when the marker exists OR `hooks.json` already has any entry referencing `kapacitor codex-hook` (pre-marker detection — same pattern as `AgentsSkillsInstaller`).
- `src/Kapacitor.Cli.Core/ClaudePluginInstaller.cs`
  - Same shape for `~/.claude/.kapacitor-plugin-version`. `IsInstalled` returns true when the marker exists OR `settings.json` already has `enabledPlugins["kapacitor@kapacitor"] == true` OR `extraKnownMarketplaces["kapacitor"]`.
- `test/Kapacitor.Cli.Tests.Unit/CodexHooksInstallerTests.cs`
- `test/Kapacitor.Cli.Tests.Unit/ClaudePluginInstallerTests.cs`
- `test/Kapacitor.Cli.Tests.Unit/PluginCommandClaudeTests.cs`

**Modified files:**

- `src/Kapacitor.Cli/Commands/PluginCommand.cs`
  - `InstallCodexHooks` stamps the marker on success. `RemoveCodexHooks` deletes the marker on success. `InstallCodex` honors `--if-installed` (skip when not installed, short-circuit when marker matches, hooks-only refresh path — does **not** touch skills, since `--skills --if-installed` is its own postinstall call).
  - Claude branch (`InstallClaude`) honors `--if-installed`. `RemoveClaude` deletes the marker on successful removal.
- `src/Kapacitor.Cli/Commands/SetupCommand.cs`
  - `InstallPlugin` (Claude) stamps the marker on success so first-run setup primes the marker for future upgrades.
- `src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs`
  - Re-route `CurrentVersion()` to call `KapacitorVersion.Current()` (single source of truth; existing public API preserved).
- `npm/kapacitor/bin/postinstall.js`
  - After the existing `--skills --if-installed` spawn, fire two more spawns: `--codex --if-installed` and `--if-installed` (Claude). Each in its own `try`/`catch`, same `timeout` / `killSignal` / `windowsHide`.
- `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs`
  - Add `--if-installed` regression tests for Codex.
- `README.md`
  - Update the upgrade-notes block (currently the section just above "Loading historical sessions") so it documents hooks + plugin auto-refresh, removing the "Codex hooks are not auto-refreshed" caveat.

---

## Self-Review Checks Before Submission

- Every `IsInstalled` returns false when the target directory does not exist (no accidental directory creation as a side effect).
- Every marker write tolerates missing parent dirs (use `Directory.CreateDirectory(Path.GetDirectoryName(...))`).
- All new flags appear in `kapacitor plugin install` help text (`src/Kapacitor.Cli.Core/Resources/help-plugin.txt` if it exists — check during Task 8).
- README's "Getting started" + "CLI commands" sections both reflect the new behavior (CLAUDE.md note: README sync has been missed twice — #60, #61).
- No `IL3050`/`IL2026` warnings after `dotnet publish -c Release` (CLAUDE.md note).

---

## Task 1: Extract `KapacitorVersion` helper

**Files:**
- Create: `src/Kapacitor.Cli.Core/KapacitorVersion.cs`
- Modify: `src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs` (replace inline `CurrentVersion()` body)

- [ ] **Step 1: Write the failing test**

Create `test/Kapacitor.Cli.Tests.Unit/KapacitorVersionTests.cs`:

```csharp
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class KapacitorVersionTests {
    [Test]
    public async Task Current_returns_assembly_informational_version() {
        var version = KapacitorVersion.Current();
        await Assert.That(version).IsNotNull();
        await Assert.That(version).IsNotEmpty();
    }

    [Test]
    public async Task Current_matches_AgentsSkillsInstaller_CurrentVersion() {
        // Regression: every installer's marker must contain the same version
        // string so a postinstall checking one marker doesn't see drift caused
        // by two installers stamping different version values for the same build.
        await Assert.That(KapacitorVersion.Current())
            .IsEqualTo(AgentsSkillsInstaller.CurrentVersion());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KapacitorVersionTests/*"`
Expected: FAIL — `KapacitorVersion` type does not exist.

- [ ] **Step 3: Write the helper**

Create `src/Kapacitor.Cli.Core/KapacitorVersion.cs`:

```csharp
using System.Reflection;

namespace Kapacitor.Cli.Core;

/// <summary>
/// Single source of truth for the version string stamped into installer
/// marker files (skills, codex hooks, claude plugin, …). Every installer
/// MUST call this so a build's markers stay consistent — a same-version
/// short-circuit check elsewhere assumes all markers carry the same value.
/// </summary>
public static class KapacitorVersion {
    public static string Current() =>
        typeof(KapacitorVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}
```

- [ ] **Step 4: Route `AgentsSkillsInstaller.CurrentVersion` through the helper**

In `src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs`, replace:

```csharp
public static string CurrentVersion() =>
    typeof(AgentsSkillsInstaller).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
```

with:

```csharp
public static string CurrentVersion() => KapacitorVersion.Current();
```

Leave the unused `using System.Reflection;` at the top alone if no other types in the file need it removed — the analyzer will flag it cleanly. (Check whether any other code in that file still uses `Reflection`; if not, remove the `using`.)

- [ ] **Step 5: Run all unit tests to verify nothing else broke**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: PASS — including the new `KapacitorVersionTests` and existing `AgentsSkillsInstallerTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli.Core/KapacitorVersion.cs \
        src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs \
        test/Kapacitor.Cli.Tests.Unit/KapacitorVersionTests.cs
git commit -m "refactor: extract KapacitorVersion helper

Single source of truth for the version string stamped into installer
markers. Sets up the codex-hooks / claude-plugin installers added in
AI-734 to share the same marker format as AgentsSkillsInstaller without
each defining its own CurrentVersion()."
```

---

## Task 2: Add `CodexHooksInstaller` (marker + IsInstalled)

**Files:**
- Create: `src/Kapacitor.Cli.Core/CodexHooksInstaller.cs`
- Test: `test/Kapacitor.Cli.Tests.Unit/CodexHooksInstallerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/Kapacitor.Cli.Tests.Unit/CodexHooksInstallerTests.cs`:

```csharp
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class CodexHooksInstallerTests {
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "does-not-exist", "hooks.json");
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName),
            "1.2.3");
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_hooks_json_has_kapacitor_entry_but_no_marker() {
        // Pre-marker install: hooks.json was written by an older CLI build
        // that didn't stamp a marker. The very first upgrade must still
        // refresh, otherwise stale command strings linger forever.
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 5 }] }
                ]
              }
            }
            """);
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_has_only_third_party_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_is_malformed() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, "{not json");
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CodexHooksInstaller.WriteMarker(hooksPath);
        await Assert.That(CodexHooksInstaller.ReadMarker(hooksPath))
            .IsEqualTo(KapacitorVersion.Current());
    }

    [Test]
    public async Task ReadMarker_returns_null_when_marker_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await Assert.That(CodexHooksInstaller.ReadMarker(hooksPath)).IsNull();
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CodexHooksInstaller.WriteMarker(hooksPath);
        CodexHooksInstaller.DeleteMarker(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName))).IsFalse();

        // Idempotent — calling twice does not throw.
        CodexHooksInstaller.DeleteMarker(hooksPath);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHooksInstallerTests/*"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the installer**

Create `src/Kapacitor.Cli.Core/CodexHooksInstaller.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Kapacitor.Cli.Core;

/// <summary>
/// Marker + detection helpers for the Codex hooks file
/// (<c>~/.codex/hooks.json</c>). Mirror of <see cref="AgentsSkillsInstaller"/>:
/// the npm postinstall hook uses <see cref="IsInstalled"/> to gate the
/// upgrade-time refresh, and <see cref="WriteMarker"/> stamps the version
/// after a successful write so subsequent upgrades can short-circuit when
/// the marker already matches.
/// </summary>
/// <remarks>
/// The hooks.json itself is written by <c>PluginCommand.InstallCodexHooks</c>;
/// this type owns only the marker side-channel and pre-marker detection.
/// </remarks>
public static class CodexHooksInstaller {
    /// <summary>
    /// File name written next to <c>hooks.json</c> after a successful install.
    /// Holds the CLI version that produced the entries.
    /// </summary>
    public const string MarkerFileName = ".kapacitor-hooks-version";

    /// <summary>
    /// True when the user has previously installed Codex hooks via setup or
    /// <c>kapacitor plugin install --codex</c>. The npm postinstall hook uses
    /// this to decide whether to refresh on upgrade vs. leave the system alone.
    /// </summary>
    /// <remarks>
    /// Detection is marker OR existing <c>kapacitor codex-hook</c> entry in
    /// <paramref name="hooksPath"/>. The hooks-json fallback covers users
    /// whose install predates the marker — without it, the first upgrade onto
    /// a marker-aware build would no-op and leave stale command strings in
    /// place forever.
    /// </remarks>
    public static bool IsInstalled(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(hooksPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, entries) in hooks) {
                if (entries is not JsonArray arr) continue;
                foreach (var entry in arr) {
                    if (CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)) return true;
                }
            }
        } catch {
            // Malformed JSON → treat as not installed; the next setup run
            // will rewrite the file cleanly.
        }
        return false;
    }

    /// <summary>
    /// Returns the version string from the marker, or null when absent or
    /// unreadable.
    /// </summary>
    public static string? ReadMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        } catch {
            return null;
        }
    }

    public static void WriteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), KapacitorVersion.Current());
        } catch {
            // Best effort. Worst case the next upgrade re-runs the install
            // unconditionally, which is idempotent.
        }
    }

    public static void DeleteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* non-fatal */ }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHooksInstallerTests/*"`
Expected: PASS — all eight tests.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/CodexHooksInstaller.cs \
        test/Kapacitor.Cli.Tests.Unit/CodexHooksInstallerTests.cs
git commit -m "feat: add CodexHooksInstaller marker + IsInstalled

Mirror of AgentsSkillsInstaller for ~/.codex/hooks.json. IsInstalled
returns true when the marker file exists OR hooks.json already has a
kapacitor codex-hook entry — the latter covers pre-marker installs so
the very first marker-aware upgrade still triggers a refresh.

Pure additive — PluginCommand still owns the hooks.json write itself.
Wired up in a follow-up commit."
```

---

## Task 3: Wire `CodexHooksInstaller` into `PluginCommand`

**Files:**
- Modify: `src/Kapacitor.Cli/Commands/PluginCommand.cs`
- Modify: `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs` (inside the existing `PluginCommandCodexTests` class):

```csharp
    [Test]
    public async Task InstallCodexHooks_stamps_marker_on_success() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCodexHooks(hooksPath);
        await Assert.That(ok).IsTrue();

        var marker = Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName);
        await Assert.That(File.Exists(marker)).IsTrue();
        await Assert.That((await File.ReadAllTextAsync(marker)).Trim())
            .IsEqualTo(KapacitorVersion.Current());
    }

    [Test]
    public async Task RemoveCodexHooks_deletes_marker_when_kapacitor_entries_were_removed() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        PluginCommand.InstallCodexHooks(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName))).IsTrue();

        var changed = PluginCommand.RemoveCodexHooks(hooksPath);
        await Assert.That(changed).IsTrue();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName))).IsFalse();
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/InstallCodexHooks_stamps_marker_on_success"`
Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/RemoveCodexHooks_deletes_marker_when_kapacitor_entries_were_removed"`
Expected: FAIL — marker is not written / deleted today.

- [ ] **Step 3: Stamp / delete the marker in `PluginCommand`**

In `src/Kapacitor.Cli/Commands/PluginCommand.cs`:

Find the body of `InstallCodexHooks`, immediately after `File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));`, add:

```csharp
            CodexHooksInstaller.WriteMarker(hooksPath);
```

Find the body of `RemoveCodexHooks`, immediately after `File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));` (inside the `if (changed)` block), add:

```csharp
                CodexHooksInstaller.DeleteMarker(hooksPath);
```

- [ ] **Step 4: Run all PluginCommandCodex tests**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/*"`
Expected: PASS — both new tests plus all pre-existing ones.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli/Commands/PluginCommand.cs \
        test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs
git commit -m "feat: stamp/delete CodexHooksInstaller marker on install/remove

InstallCodexHooks writes ~/.codex/.kapacitor-hooks-version on success,
RemoveCodexHooks deletes it when any kapacitor entry is removed. Sets
up the --if-installed refresh path added in the next commit."
```

---

## Task 4: Add `--if-installed` semantics to `InstallCodex`

**Files:**
- Modify: `src/Kapacitor.Cli/Commands/PluginCommand.cs`
- Modify: `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs`

The refresh path is **hooks-only**: it does not touch skills. The npm postinstall makes a separate `--skills --if-installed` call, so doing it again here would be redundant and would also make `--codex --if-installed` semantics asymmetric with `--codex` (which is an atomic hooks+skills contract — see AI-676 comment in `InstallCodex`).

- [ ] **Step 1: Write the failing tests**

Append to `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs` (note: marked `[NotInParallel("HomeEnvVarMutation")]` because they mutate `HOME`):

```csharp
    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Install_codex_with_if_installed_is_noop_when_no_marker_and_no_existing_entries() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-codex-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // hooks.json must NOT exist — user never opted in.
            var hooksPath = Path.Combine(fakeHome.FullName, ".codex", "hooks.json");
            await Assert.That(File.Exists(hooksPath)).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Install_codex_with_if_installed_refreshes_pre_marker_install() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-codex-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            // Seed hooks.json with a stale 5-second PermissionRequest timeout
            // and NO marker. This is the pre-marker scenario.
            var codexDir = Path.Combine(fakeHome.FullName, ".codex");
            Directory.CreateDirectory(codexDir);
            var hooksPath = Path.Combine(codexDir, "hooks.json");
            await File.WriteAllTextAsync(hooksPath, """
                {
                  "hooks": {
                    "PermissionRequest": [
                      { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 5 }] }
                    ]
                  }
                }
                """);
            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // PermissionRequest timeout must have been refreshed to 86400.
            var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
            var entries = root["hooks"]!["PermissionRequest"]!.AsArray();
            var kapacitor = entries.First(e =>
                (e!["hooks"] as JsonArray)!.Any(h =>
                    h?["command"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("kapacitor codex-hook")));
            await Assert.That(kapacitor!["hooks"]!.AsArray()[0]!["timeout"]!.GetValue<int>())
                .IsEqualTo(86400);

            // Marker now stamped → next upgrade takes the fast path.
            await Assert.That(File.Exists(Path.Combine(codexDir, CodexHooksInstaller.MarkerFileName))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Install_codex_with_if_installed_is_noop_when_marker_matches_current_version() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-codex-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            var codexDir = Path.Combine(fakeHome.FullName, ".codex");
            Directory.CreateDirectory(codexDir);

            // Pre-seed hooks.json with sentinel content + matching marker.
            var hooksPath = Path.Combine(codexDir, "hooks.json");
            await File.WriteAllTextAsync(hooksPath, """{"sentinel": "must-survive"}""");
            await File.WriteAllTextAsync(
                Path.Combine(codexDir, CodexHooksInstaller.MarkerFileName),
                KapacitorVersion.Current());
            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // Sentinel intact → installer short-circuited.
            var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
            await Assert.That(root["sentinel"]!.GetValue<string>()).IsEqualTo("must-survive");
            await Assert.That(root["hooks"]).IsNull();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run new tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/Install_codex_with_if_installed*"`
Expected: FAIL — `--if-installed` is currently ignored by the codex branch.

- [ ] **Step 3: Add `--if-installed` to `InstallCodex`**

In `src/Kapacitor.Cli/Commands/PluginCommand.cs`, replace the body of `InstallCodex` so the `--if-installed` gate fires at the top, before plugin resolution:

```csharp
    static async Task<int> InstallCodex(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : CodexPaths.UserHooksJson;

        // --if-installed: refresh-only mode used by the npm postinstall hook.
        // Skip when the user never opted in; short-circuit when the marker
        // already matches the current CLI version. Skills are NOT touched
        // here — `--skills --if-installed` is its own postinstall call.
        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly && !CodexHooksInstaller.IsInstalled(hooksPath)) {
            return 0;
        }

        if (refreshOnly &&
            CodexHooksInstaller.ReadMarker(hooksPath) == KapacitorVersion.Current()) {
            return 0;
        }

        if (refreshOnly) {
            // Hooks-only refresh: rewrite the kapacitor entries in hooks.json,
            // stamp the marker, exit. No skills, no plugin folder needed.
            if (!InstallCodexHooks(hooksPath)) {
                // Never fail the npm install path.
                return 0;
            }

            await Console.Out.WriteLineAsync($"Codex hooks refreshed ({scope}: {hooksPath})");
            return 0;
        }

        // `--codex` is an atomic hooks AND skills contract. Resolve the
        // skills source BEFORE writing hooks so a missing plugin folder
        // doesn't leave the user with hooks pointing at a binary whose
        // skills never installed.
        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            await Console.Error.WriteLineAsync(
                "Cannot install Codex plugin: kapacitor plugin folder not found. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor"
            );
            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            await Console.Error.WriteLineAsync(
                $"Cannot install Codex plugin: 'skills' folder missing from {pluginPath}. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor"
            );
            return 1;
        }

        // Per-skill preflight runs BEFORE writing hooks so a packaging defect
        // (top-level skills/ present but an individual skill folder missing)
        // can't leave the user with hooks installed and skills not. This is the
        // atomicity guarantee from AI-676 — either everything installs or nothing.
        var missingSkills = AgentsSkillsInstaller.SourceNames
            .Where(name => !Directory.Exists(Path.Combine(skillsSource, name)))
            .ToList();
        if (missingSkills.Count > 0) {
            await Console.Error.WriteLineAsync(
                $"Cannot install Codex plugin: missing skill folder(s) under {skillsSource}: "
                + string.Join(", ", missingSkills)
                + ". Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
            return 1;
        }

        if (!InstallCodexHooks(hooksPath)) {
            await Console.Error.WriteLineAsync("Could not write Codex hooks file.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Codex hooks installed ({scope}: {hooksPath})");
        await Console.Out.WriteLineAsync(
            "Next: run /hooks inside Codex and trust each kapacitor entry — " +
            "Codex won't execute hooks until each is explicitly trusted."
        );

        // Skills are user-scoped only. Written to ~/.agents/skills/ so they
        // work across Codex and other compatible agents.
        if (!AgentsSkillsInstaller.Install(skillsSource, AgentsPaths.UserSkillsDir)) {
            await Console.Error.WriteLineAsync("Could not install agent skills.");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Agent skills installed (user: {AgentsPaths.UserSkillsDir})");

        AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

        if (scope == "project") {
            await Console.Out.WriteLineAsync(
                "Note: Codex requires the project's .codex directory to be trusted. " +
                "Run `codex` once in this directory and accept the trust prompt."
            );
        }

        return 0;
    }
```

- [ ] **Step 4: Run all PluginCommandCodex tests**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandCodexTests/*"`
Expected: PASS — three new + all pre-existing.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli/Commands/PluginCommand.cs \
        test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs
git commit -m "feat: --if-installed for kapacitor plugin install --codex

Hooks-only refresh path gated on the CodexHooksInstaller marker (or
existing kapacitor entries in hooks.json for pre-marker installs).
Same-version short-circuit avoids rewriting hooks.json when the marker
already matches the current CLI build. Failures during refresh are
swallowed so npm install never breaks.

Skills are intentionally not touched on this path — postinstall makes
its own --skills --if-installed call."
```

---

## Task 5: Add `ClaudePluginInstaller` (marker + IsInstalled)

**Files:**
- Create: `src/Kapacitor.Cli.Core/ClaudePluginInstaller.cs`
- Test: `test/Kapacitor.Cli.Tests.Unit/ClaudePluginInstallerTests.cs`

The Claude side writes `extraKnownMarketplaces["kapacitor"]` and `enabledPlugins["kapacitor@kapacitor"] = true` into `~/.claude/settings.json` (or `<repo>/.claude/settings.local.json` for project scope). The marketplace `source.path` is an absolute path that can change between npm installs, so the user's settings file genuinely benefits from a refresh.

- [ ] **Step 1: Write the failing tests**

Create `test/Kapacitor.Cli.Tests.Unit/ClaudePluginInstallerTests.cs`:

```csharp
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class ClaudePluginInstallerTests {
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "does-not-exist", "settings.json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName),
            "1.2.3");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_enabledPlugins_has_kapacitor() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kapacitor@kapacitor": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_marketplace_has_kapacitor() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_has_unrelated_keys_only() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """{ "theme": "dark" }""");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_is_malformed() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{not json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        await Assert.That(ClaudePluginInstaller.ReadMarker(settingsPath))
            .IsEqualTo(KapacitorVersion.Current());
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        ClaudePluginInstaller.DeleteMarker(settingsPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        ClaudePluginInstaller.DeleteMarker(settingsPath);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePluginInstallerTests/*"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the installer**

Create `src/Kapacitor.Cli.Core/ClaudePluginInstaller.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Kapacitor.Cli.Core;

/// <summary>
/// Marker + detection helpers for the Claude Code settings file
/// (<c>~/.claude/settings.json</c> for user scope, or
/// <c>&lt;repo&gt;/.claude/settings.local.json</c> for project scope).
/// Mirrors <see cref="AgentsSkillsInstaller"/> and
/// <see cref="CodexHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful
/// install.
/// </summary>
/// <remarks>
/// The settings file itself is written by
/// <c>SetupCommand.InstallPlugin</c>; this type owns only the marker
/// side-channel and pre-marker detection. The marketplace source path
/// is absolute and changes between npm installs, so a refresh on
/// upgrade is meaningful — not just for command-string drift.
/// </remarks>
public static class ClaudePluginInstaller {
    public const string MarkerFileName = ".kapacitor-plugin-version";

    /// <summary>
    /// True when the user has previously installed the kapacitor Claude
    /// plugin via setup or <c>kapacitor plugin install</c>. Detection is
    /// marker OR an existing kapacitor entry in <paramref name="settingsPath"/>
    /// (either <c>enabledPlugins["kapacitor@kapacitor"]</c> or
    /// <c>extraKnownMarketplaces["kapacitor"]</c>) so pre-marker installs
    /// are picked up on the first marker-aware upgrade.
    /// </summary>
    public static bool IsInstalled(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(settingsPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;

            if (root["enabledPlugins"] is JsonObject enabled &&
                enabled["kapacitor@kapacitor"] is JsonValue v &&
                v.TryGetValue<bool>(out var on) && on) {
                return true;
            }

            if (root["extraKnownMarketplaces"] is JsonObject marketplaces &&
                marketplaces["kapacitor"] is not null) {
                return true;
            }
        } catch {
            // Malformed JSON → treat as not installed.
        }
        return false;
    }

    public static string? ReadMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        } catch {
            return null;
        }
    }

    public static void WriteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), KapacitorVersion.Current());
        } catch {
            // Best effort. Worst case the next upgrade re-runs the install
            // unconditionally, which is idempotent.
        }
    }

    public static void DeleteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* non-fatal */ }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePluginInstallerTests/*"`
Expected: PASS — all eight tests.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/ClaudePluginInstaller.cs \
        test/Kapacitor.Cli.Tests.Unit/ClaudePluginInstallerTests.cs
git commit -m "feat: add ClaudePluginInstaller marker + IsInstalled

Mirror of AgentsSkillsInstaller / CodexHooksInstaller for the Claude
Code settings.json. IsInstalled returns true when the marker exists OR
the file already has a kapacitor entry under enabledPlugins or
extraKnownMarketplaces — the latter covers pre-marker installs.

Wired into SetupCommand.InstallPlugin in a follow-up commit."
```

---

## Task 6: Wire `ClaudePluginInstaller` + `--if-installed` into Claude branch

**Files:**
- Modify: `src/Kapacitor.Cli/Commands/SetupCommand.cs` (write marker on successful `InstallPlugin`)
- Modify: `src/Kapacitor.Cli/Commands/PluginCommand.cs` (`InstallClaude` honors `--if-installed`; `RemoveClaude` deletes marker)
- Create: `test/Kapacitor.Cli.Tests.Unit/PluginCommandClaudeTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/Kapacitor.Cli.Tests.Unit/PluginCommandClaudeTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandClaudeTests {
    [Test]
    public async Task InstallPlugin_stamps_marker_on_success() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");

        var ok = SetupCommand.InstallPlugin(settingsPath, "/some/marketplace");
        await Assert.That(ok).IsTrue();

        var marker = Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName);
        await Assert.That(File.Exists(marker)).IsTrue();
        await Assert.That((await File.ReadAllTextAsync(marker)).Trim())
            .IsEqualTo(KapacitorVersion.Current());
    }

    [Test]
    public async Task Install_claude_with_if_installed_is_noop_when_no_marker_and_no_entries() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            var settingsPath = Path.Combine(fakeHome.FullName, ".claude", "settings.json");
            await Assert.That(File.Exists(settingsPath)).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_claude_with_if_installed_refreshes_pre_marker_install() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalPlug = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");
        var pluginDir    = Directory.CreateTempSubdirectory("kapacitor-plugin-src-");
        try {
            // Seed pre-marker install: enabledPlugins entry, no marker.
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            await File.WriteAllTextAsync(settingsPath, """
                {
                  "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/old/path" } } },
                  "enabledPlugins": { "kapacitor@kapacitor": true }
                }
                """);

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginDir.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            // Marketplace path must now point at the new plugin dir.
            var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
            var path = root["extraKnownMarketplaces"]!["kapacitor"]!["source"]!["path"]!.GetValue<string>();
            await Assert.That(path).IsEqualTo(pluginDir.FullName);

            // Marker stamped.
            await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginDir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_claude_with_if_installed_is_noop_when_marker_matches_current_version() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            var settingsPath = Path.Combine(claudeDir, "settings.json");

            // Sentinel content + matching marker.
            await File.WriteAllTextAsync(settingsPath, """{"sentinel": "must-survive"}""");
            await File.WriteAllTextAsync(
                Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
                KapacitorVersion.Current());
            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
            await Assert.That(root["sentinel"]!.GetValue<string>()).IsEqualTo("must-survive");
            await Assert.That(root["enabledPlugins"]).IsNull();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Install_claude_with_if_installed_swallows_plugin_resolution_failure() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalPlug = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");
        var originalErr  = Console.Error;
        var capturedErr  = new StringWriter();
        try {
            // Seed: marker present so the gate proceeds…
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            await File.WriteAllTextAsync(
                Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
                "some-old-version");

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            // …but plugin dir resolution fails.
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR",
                Path.Combine(Path.GetTempPath(), $"kapacitor-missing-{Guid.NewGuid():N}"));
            Console.SetError(capturedErr);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"]);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(capturedErr.ToString()).IsEmpty();
        } finally {
            Console.SetError(originalErr);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Remove_claude_deletes_marker() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-claude-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try {
            var claudeDir = Path.Combine(fakeHome.FullName, ".claude");
            Directory.CreateDirectory(claudeDir);
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            await File.WriteAllTextAsync(settingsPath, """
                {
                  "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/p" } } },
                  "enabledPlugins": { "kapacitor@kapacitor": true }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
                KapacitorVersion.Current());

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "remove"]);
            await Assert.That(exit).IsEqualTo(0);

            await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run new tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandClaudeTests/*"`
Expected: FAIL across the board.

- [ ] **Step 3: Stamp the marker on successful `InstallPlugin`**

In `src/Kapacitor.Cli/Commands/SetupCommand.cs`, find the body of `InstallPlugin`. Immediately after `File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));`, add:

```csharp
            ClaudePluginInstaller.WriteMarker(settingsPath);
```

- [ ] **Step 4: Add `--if-installed` to `InstallClaude` and delete marker in `RemoveClaude`**

In `src/Kapacitor.Cli/Commands/PluginCommand.cs`, replace the body of `InstallClaude`:

```csharp
    static async Task<int> InstallClaude(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        // --if-installed: refresh-only mode used by the npm postinstall hook.
        // Skip when the user never opted in; short-circuit when the marker
        // already matches the current CLI version.
        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly && !ClaudePluginInstaller.IsInstalled(settingsPath)) {
            return 0;
        }

        if (refreshOnly &&
            ClaudePluginInstaller.ReadMarker(settingsPath) == KapacitorVersion.Current()) {
            return 0;
        }

        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            if (refreshOnly) return 0;
            await Console.Error.WriteLineAsync("Plugin directory not found. Re-install kapacitor via npm:");
            await Console.Error.WriteLineAsync("  npm install -g @kurrent/kapacitor");

            return 1;
        }

        var installed = SetupCommand.InstallPlugin(settingsPath, pluginPath);

        if (installed) {
            await Console.Out.WriteLineAsync(refreshOnly
                ? $"Plugin refreshed ({scope}: {settingsPath})"
                : $"Plugin installed ({scope}: {settingsPath})");
        } else {
            if (refreshOnly) return 0;
            await Console.Error.WriteLineAsync("Could not update settings file.");

            return 1;
        }

        return 0;
    }
```

Then, in `RemoveClaude`, inside the `if (changed) { ... }` block, immediately after `await File.WriteAllTextAsync(settingsPath, root.ToJsonString(WriteOpts));`, add:

```csharp
                ClaudePluginInstaller.DeleteMarker(settingsPath);
```

- [ ] **Step 5: Run all PluginCommandClaude + SetupCommand + integration tests**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandClaudeTests/*"`
Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: PASS (every Claude test plus all existing tests).

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli/Commands/PluginCommand.cs \
        src/Kapacitor.Cli/Commands/SetupCommand.cs \
        test/Kapacitor.Cli.Tests.Unit/PluginCommandClaudeTests.cs
git commit -m "feat: --if-installed for kapacitor plugin install (Claude)

InstallPlugin stamps the ClaudePluginInstaller marker on success;
PluginCommand.InstallClaude honors --if-installed (skip when not
installed, short-circuit when marker matches current version, swallow
plugin resolution failures so npm install never breaks); RemoveClaude
deletes the marker.

The marketplace source path is absolute and can change between npm
installs, so a refresh on upgrade does meaningful work even when the
plugin manifest itself hasn't changed."
```

---

## Task 7: Extend `npm/postinstall.js` to call hooks/claude refresh

**Files:**
- Modify: `npm/kapacitor/bin/postinstall.js`

- [ ] **Step 1: Replace the script body**

Replace the single `spawnSync` call with a small loop so a stalled child of one refresh doesn't prevent the others from running, and so the three calls share one place to tune timeouts.

```javascript
#!/usr/bin/env node

// Runs after `npm install -g @kurrent/kapacitor` (including upgrades).
//
// Refreshes user-scope kapacitor agent installations so users pick up
// new or updated skills, Codex hook commands, and Claude plugin
// registration without manually re-running `kapacitor setup`.
//
// Contract:
// - Only runs on global installs. Skipping non-global installs avoids
//   touching ~/.agents/, ~/.codex/, or ~/.claude/ during unrelated
//   local/transitive installs on already-opted-in machines.
// - Each refresh uses `--if-installed`, which no-ops unless the user
//   has previously opted in (marker file present OR pre-marker install
//   detected via existing kapacitor entries in the target file).
// - Each refresh runs independently. A failure, timeout, or unexpected
//   exit code from one does not prevent the others. The script always
//   exits 0 — a failed refresh must never break `npm install`.

const { spawnSync } = require("child_process");
const path = require("path");

const isGlobal =
  process.env.npm_config_global === "true" ||
  process.env.npm_config_location === "global";

if (!isGlobal) {
  process.exit(0);
}

const launcher = path.join(__dirname, "kapacitor.js");

// One entry per agent. Order is independent — each refresh is gated by
// its own marker.
const refreshes = [
  ["plugin", "install", "--skills", "--if-installed"],
  ["plugin", "install", "--codex",  "--if-installed"],
  ["plugin", "install",             "--if-installed"], // Claude
];

for (const argv of refreshes) {
  try {
    spawnSync(process.execPath, [launcher, ...argv], {
      stdio: "ignore",
      env: process.env,
      // Hard ceiling so a stalled child can never hang `npm install`.
      // Each refresh is bounded independently.
      timeout: 60_000,
      killSignal: "SIGKILL",
      windowsHide: true,
    });
  } catch {
    // Never fail npm install.
  }
}

process.exit(0);
```

- [ ] **Step 2: Manually verify the script with a smoke test**

Run a global install simulation in a throwaway HOME:

```bash
# Build the binary
dotnet publish src/Kapacitor.Cli/Kapacitor.Cli.csproj -c Release

# Verify there are no AOT warnings (CLAUDE.md guidance)
dotnet publish src/Kapacitor.Cli/Kapacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no AOT warnings"
```

Manually invoke the three refresh commands in a temp HOME to confirm exit code 0 in the no-opt-in case:

```bash
TMPHOME=$(mktemp -d)
HOME="$TMPHOME" ./<published-binary>/kapacitor plugin install --skills --if-installed; echo "skills exit: $?"
HOME="$TMPHOME" ./<published-binary>/kapacitor plugin install --codex --if-installed; echo "codex exit: $?"
HOME="$TMPHOME" ./<published-binary>/kapacitor plugin install --if-installed; echo "claude exit: $?"
# Confirm: nothing under $TMPHOME — fresh user must not be touched.
test -z "$(ls -A "$TMPHOME")" && echo "TMPHOME is empty as expected"
rm -rf "$TMPHOME"
```

Expected: all three exit 0; `$TMPHOME` is empty.

- [ ] **Step 3: Commit**

```bash
git add npm/kapacitor/bin/postinstall.js
git commit -m "feat: postinstall also refreshes codex hooks and claude plugin

Adds two more --if-installed refresh calls to the npm postinstall hook
on top of the existing --skills refresh from PR #102. Each runs
independently so a stalled child of one does not block the others;
each is gated by its own marker file so a user who never opted in is
never silently activated.

Unblocks AI-730 / AI-732 / AI-733: command-shape migrations and new
Cursor hooks no longer need a legacy alias because the postinstall
will rewrite the user's hook config to the current CLI's command
strings on the next npm install."
```

---

## Task 8: Update `README.md`

**Files:**
- Modify: `README.md`

This is mandated by `CLAUDE.md` — README sync has been missed twice (#60, #61). Update **two** places:

1. The "Need hooks for an agent installed after setup" blockquote near the top of "Getting started" (line ~53) — mention the `--if-installed` postinstall behavior briefly so readers know they generally do not need to re-run after upgrades.
2. The "Upgrading from an earlier version of kapacitor?" block in the per-command section (line ~314) — remove the "Codex hooks are not auto-refreshed" caveat and document the three-way refresh.

- [ ] **Step 1: Update the upgrade-notes block**

Open `README.md`. Find the existing block (currently containing "Codex *hooks* (`~/.codex/hooks.json`) are not auto-refreshed"). Replace the whole block with:

```markdown
> **Upgrading from an earlier version of kapacitor?** The npm postinstall hook refreshes all user-scope kapacitor installations on every `npm install -g @kurrent/kapacitor`, so you always pick up the current CLI version's skills, Codex hook commands, and Claude plugin registration. Each refresh is gated on a marker file written by your previous setup — fresh systems that never opted in are left untouched. Project-scope installs (`--project`) are not auto-refreshed; re-run `kapacitor plugin install [--codex] --project` after upgrading if you want the latest config for a specific repo.
```

- [ ] **Step 2: Update the "Need hooks for an agent installed after setup" blockquote**

In the same `README.md`, find the existing blockquote starting `> **Need hooks for an agent installed after setup, or scoped to a single repo?**`. Add a sentence at the end:

```markdown
> Re-running after a kapacitor upgrade is rarely needed — the npm postinstall hook auto-refreshes user-scope installations on every `npm install -g @kurrent/kapacitor`.
```

- [ ] **Step 3: Document the new `--if-installed` shapes in the CLI examples block**

Find the existing `## CLI commands` block listing `kapacitor plugin install --skills --if-installed`. Add two siblings beneath it so the example reflects the new surface:

```bash
kapacitor plugin install --codex --if-installed           # refresh Codex hooks only if previously installed (used by npm postinstall, harmless to call by hand)
kapacitor plugin install --if-installed                   # refresh Claude plugin registration only if previously installed (used by npm postinstall)
```

- [ ] **Step 4: Quick visual review**

Run: `grep -n "if-installed" README.md`
Expected: three example lines (skills, codex, claude) plus the descriptive paragraph in the upgrade-notes block.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs: README — hooks + plugin auto-refresh on npm upgrade

Documents the three-way --if-installed postinstall refresh added in
AI-734. Removes the prior caveat that Codex hooks weren't auto-
refreshed."
```

---

## Task 9: Verify NativeAOT publish + full test suite

**Files:** (verification only — no edits expected)

- [ ] **Step 1: Confirm clean AOT publish**

Run: `dotnet publish src/Kapacitor.Cli/Kapacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no AOT warnings"`
Expected: `no AOT warnings`.

If any IL3050/IL2026 warnings appear, the most likely culprit is the new JSON parsing in `IsInstalled` — but `JsonNode.Parse` of a `string` is AOT-safe (no reflection metadata required) and the existing PluginCommand uses the same pattern, so this should be clean. Investigate before continuing if warnings surface.

- [ ] **Step 2: Run full unit test suite**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj`
Expected: PASS — total assertion count should include all new tests:
- `KapacitorVersionTests`: 2
- `CodexHooksInstallerTests`: 8
- `ClaudePluginInstallerTests`: 8
- `PluginCommandCodexTests`: 5 new + existing
- `PluginCommandClaudeTests`: 6 new

- [ ] **Step 3: Run integration test suite**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Integration/Kapacitor.Cli.Tests.Integration.csproj`
Expected: PASS — nothing in this plan should affect integration tests, but they may exercise plugin install paths.

- [ ] **Step 4: Code-review pass**

Use the `pr-review-toolkit:code-reviewer` subagent (or read through the diff manually) against the staged changes to catch:
- Any missing `--if-installed` swallowing on the Claude/Codex path (refresh must NEVER fail npm install).
- Any `IsInstalled` accidentally creating directories as a side effect.
- README drift between user docs and `commands.md` in `../kapacitor-web` (see memory: `ref_kapacitor_web.md` — `commands.md` and getting-started must mirror README for the public site).

- [ ] **Step 5: Final no-op commit if review uncovered nothing**

No code changes needed at this step unless review flagged something. Otherwise proceed to opening the PR.

---

## PR Summary

When opening the PR, suggested body:

```
## Summary

- Mirror the `AgentsSkillsInstaller` marker pattern per vendor:
  - `CodexHooksInstaller` stamps `~/.codex/.kapacitor-hooks-version` on every `kapacitor plugin install --codex`.
  - `ClaudePluginInstaller` stamps `~/.claude/.kapacitor-plugin-version` on every `kapacitor plugin install`.
- Add `--if-installed` to `kapacitor plugin install --codex` and `kapacitor plugin install` (Claude default). Refresh-only path: skip when the user never opted in, short-circuit when marker matches current version, swallow all errors so npm install never breaks.
- Extend `npm/kapacitor/bin/postinstall.js` to fire three independent refresh calls (skills, codex, claude) on every global install.
- README documents the new behavior; removes the "Codex hooks are not auto-refreshed" caveat.

## Why this gates AI-730 / AI-732 / AI-733

- AI-730 introduces Cursor hooks — it will write a `~/.cursor/.kapacitor-hooks-version` marker from day one and slot a fourth refresh call into the postinstall script.
- AI-732 / AI-733 migrate Codex / Claude from per-vendor commands (`kapacitor codex-hook`, `kapacitor session-start`, …) to the unified `kapacitor hook --<vendor>` surface. With this PR merged, those migrations can drop the "keep legacy command as alias for one release" requirement entirely — the postinstall rewrites the user's hook config to the new command strings before the next hook fires.

## Test plan

- [ ] `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj` passes (new tests: KapacitorVersionTests, CodexHooksInstallerTests, ClaudePluginInstallerTests, PluginCommandClaudeTests, plus added PluginCommandCodexTests cases).
- [ ] `dotnet run --project test/Kapacitor.Cli.Tests.Integration/Kapacitor.Cli.Tests.Integration.csproj` passes.
- [ ] `dotnet publish src/Kapacitor.Cli/Kapacitor.Cli.csproj -c Release` emits zero IL3050/IL2026 warnings.
- [ ] Manual smoke test: `npm install -g @kurrent/kapacitor` in a throwaway HOME with no prior setup → all three refreshes exit 0, no files written under `~/.agents`, `~/.codex`, `~/.claude`.
- [ ] Manual smoke test: same install in a HOME that has Codex hooks pre-seeded with a 5s `PermissionRequest` timeout → `~/.codex/hooks.json` rewritten with the current 86400s timeout, `~/.codex/.kapacitor-hooks-version` stamped.
```
