# Vendor Accounts — Part A: Recording Every Account — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** kcap knows every Claude config directory and Codex home on the machine, wires recording into each one, and attributes hooks, titles, plans and imports to the account a session actually ran under — so sessions in a non-default account stop being silently lost.

**Architecture:** A per-user account registry (`accounts/` beside the daemons directory, fixed location, lock-guarded, atomically replaced) lists accounts. A Core wiring service installs/removes the Claude plugin and the Codex hooks/MCP/skills per account through atomic, malformed-refusing settings writers. `kcap plugin` (user scope), setup, uninstall and the npm refresh iterate the registry; recording paths derive an account's layout from the transcript path they are handed.

**Tech Stack:** .NET 10, NativeAOT, System.Text.Json source generation, TUnit, existing Core helpers (`ConfigFileLock`, `AtomicFile`, `CodexConfigToml`, `AgentsSkillsInstaller`).

**Spec:** `docs/superpowers/specs/2026-10-07-vendor-accounts-and-limits-design.md` (Sections 3–4).

## Scope boundary with Part B

These parts of spec Section 3 move to Part B, because they need a vendor process probe that belongs with the daemon-side refresh:

- **Identity:** email, org, plan and sign-in, from `claude auth status --json` and Codex app-server `account/read`. This includes the `generation` counter and same-login grouping.
- **The "Needs trust" recording state:** Codex hook trust lives in `config.toml` `[hooks.state]` as a `trusted_hash` that only Codex computes. kcap reads it through app-server `hooks/list`, which today exists only in the daemon (`CodexHookTrust`).
- **`kcap accounts refresh`.**

Part A therefore reports four Codex states: `Installed` (hooks present, trust not verified), `Broken`, `NotWired`, plus Claude's `Recording`. The entry record leaves room for Part B's fields.

Uninstall keeps today's order (daemons stop, then integrations are removed). The spec's "unwire before stopping daemons" only matters once Part B installs the status line wrapper, so Part B moves it.

## Global Constraints

- Registry location: `accounts/` as a sibling of the daemons directory (`DaemonStore.Directory`'s parent), ignoring `KCAP_CONFIG_DIR`; directory 0700, files 0600 on Unix.
- Registry lock: `ConfigFileLock.Acquire(<accounts dir>/accounts.lock)` (a named mutex keyed by that path); never derive it from a `ConfigRoot`.
- Every vendor settings write edits only kcap-owned keys and replaces the file atomically; a file that does not parse is never overwritten or reset to `{}`.
- Codex `config.toml` and `mcp-ownership-v1.json` stay 0600 (`RegisterKcapMcpServers_writes_owner_only_files_on_unix` must keep passing).
- Project-scope operations (`--project`) act on that project only and never touch registered accounts.
- No credential file is opened. Discovery checks presence of vendor files only, never contents.
- "Account" everywhere in CLI, UI and code — never "profile" (that means a Capacitor server profile).
- AOT: no `JsonArray` collection expressions; run the IL-warning publish check at the end.
- Comments: scarce, per CLAUDE.md — no history, no spec coordinates, no ticket narration.
- Commit subjects: imperative, ≤ 80 characters, no issue reference (none is known; do not invent one). End each commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Tests: TUnit; `TempDir`/`TempHome` fixtures; `ConsoleOutput.StartCapture()` with bare `[NotInParallel]`; env vars through `EnvScope.Exclusive`.

## Review Focus

1. **A trailing slash or symlink in an account directory.** `~/.claude-work/` and `~/.claude-work` must be the same account, and adding the same directory twice must not create two entries. *Test:* Task 5.
2. **Registering `~/.claude` explicitly.** The default Claude directory passed as `CLAUDE_CONFIG_DIR` relocates `.claude.json`, so the default account must map to a null config dir or wiring would read the wrong user config. *Test:* Task 6.
3. **Malformed vendor settings in one account while others are fine.** Wiring must refuse that one account, report it Broken, and still wire the rest. *Test:* Task 7.
4. **A Claude transcript path outside any registered account.** Example: a hosted agent's symlinked project dir. It must fall back to the environment layout and never throw from a hook. *Test:* Task 6.
5. **`kcap plugin remove` (user scope) with a registered account whose directory has been deleted.** It must report and continue, not fail the whole removal. *Test:* Task 8.

---

### Task 1: Permission-aware atomic file replacement

**Files:**
- Modify: `src/Capacitor.Cli.Core/Skills/AtomicFile.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Skills/AtomicFileTests.cs` (create)

**Interfaces:**
- Produces: `AtomicFile.Replace(string path, string contents, UnixFileMode? mode = null)` and `AtomicFile.Replace(string path, byte[] contents, UnixFileMode? mode = null)`. When `mode` is null and the destination exists on Unix, the destination's current mode is kept; when `mode` is given it is applied. The temp file has that mode before any byte is written.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class AtomicFileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Replace_writes_new_contents() {
        var path = Tmp.CreateFile("a.json", "old");

        AtomicFile.Replace(path, "new");

        await Assert.That(File.ReadAllText(path)).IsEqualTo("new");
    }

    [Test]
    public async Task Replace_keeps_the_destination_mode_when_none_is_given() {
        if (OperatingSystem.IsWindows()) return;
        var path = Tmp.CreateFile("a.toml", "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        AtomicFile.Replace(path, "new");

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Replace_applies_the_requested_mode_to_a_new_file() {
        if (OperatingSystem.IsWindows()) return;
        var path = Tmp.PathTo("new.json");

        AtomicFile.Replace(path, "x", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Replace_leaves_no_temporary_file_behind() {
        var path = Tmp.CreateFile("a.json", "old");

        AtomicFile.Replace(path, "new");

        await Assert.That(Directory.GetFiles(Tmp.Path)).IsEquivalentTo(new[] { path });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/AtomicFileTests/*"`
Expected: build error — no overload takes a `UnixFileMode?`.

- [ ] **Step 3: Implement**

Replace the body of `AtomicFile` (keep the existing doc comment):

```csharp
public static class AtomicFile {
    public static void Replace(string path, string contents, UnixFileMode? mode = null) =>
        Replace(path, Encoding.UTF8.GetBytes(contents), mode);

    public static void Replace(string path, byte[] contents, UnixFileMode? mode = null) {
        var tmp     = $"{path}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp";
        var created = false;
        var target  = mode ?? ExistingMode(path);

        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (target is { } m && !OperatingSystem.IsWindows()) options.UnixCreateMode = m;

            using (var stream = new FileStream(tmp, options)) {
                // Set once the exclusive create has returned, so a name that was somehow already
                // taken is never a name this call cleans up.
                created = true;
                // UnixCreateMode is filtered by the umask; set the exact mode before writing.
                if (target is { } exact && !OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, exact);
                stream.Write(contents);
            }

            File.Move(tmp, path, overwrite: true);
        } catch {
            if (created) {
                try { File.Delete(tmp); } catch { /* preserve the original exception */ }
            }
            throw;
        }
    }

    static UnixFileMode? ExistingMode(string path) =>
        !OperatingSystem.IsWindows() && File.Exists(path) ? File.GetUnixFileMode(path) : null;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: same command as Step 2. Expected: 4 passed. Also run the existing skills tests: `--treenode-filter "/*/*/Skills*/*"` — expected all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Skills/AtomicFile.cs test/Capacitor.Cli.Core.Tests.Unit/Skills/AtomicFileTests.cs
git commit -m "Keep file mode across atomic replacement"
```

---

### Task 2: Atomic, malformed-refusing JSON settings editor

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/JsonSettingsFile.cs`
- Create: `src/Capacitor.Cli.Core/Harness/SettingsEdit.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/JsonSettingsFileTests.cs`

**Interfaces:**
- Consumes: `AtomicFile.Replace(path, contents, mode)` (Task 1).
- Produces:
  - `public enum SettingsEdit { Changed, Unchanged, Malformed, Failed }`
  - `public static SettingsEdit JsonSettingsFile.Edit(string path, Func<JsonObject, bool> edit, bool createIfMissing = true)` — `edit` returns whether it changed anything. Missing file → empty object (or `Unchanged` without creating when `createIfMissing` is false). Unparseable or non-object root → `Malformed`, file untouched. I/O exception → `Failed`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

public class JsonSettingsFileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Edit_creates_a_missing_file_and_its_directory() {
        var path = Tmp.PathTo("sub", "settings.json");

        var result = JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(JsonNode.Parse(File.ReadAllText(path))!["a"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Edit_preserves_unrelated_keys() {
        var path = Tmp.CreateFile("settings.json", """{ "theme": "dark", "a": 0 }""");

        JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        await Assert.That(root["theme"]!.GetValue<string>()).IsEqualTo("dark");
        await Assert.That(root["a"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Edit_leaves_a_malformed_file_untouched() {
        var path = Tmp.CreateFile("settings.json", "{ not json");

        var result = JsonSettingsFile.Edit(path, root => { root["a"] = 1; return true; });

        await Assert.That(result).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("{ not json");
    }

    [Test]
    public async Task Edit_treats_a_non_object_root_as_malformed() {
        var path = Tmp.CreateFile("settings.json", "[1,2]");

        await Assert.That(JsonSettingsFile.Edit(path, _ => true)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("[1,2]");
    }

    [Test]
    public async Task Edit_does_not_write_when_nothing_changed() {
        var path = Tmp.CreateFile("settings.json", "{ }");
        var before = File.GetLastWriteTimeUtc(path);

        var result = JsonSettingsFile.Edit(path, _ => false);

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(before);
    }

    [Test]
    public async Task Edit_without_create_skips_a_missing_file() {
        var path = Tmp.PathTo("absent.json");

        var result = JsonSettingsFile.Edit(path, _ => true, createIfMissing: false);

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.Exists(path)).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/JsonSettingsFileTests/*"`
Expected: build error — `JsonSettingsFile` not found.

- [ ] **Step 3: Implement**

`SettingsEdit.cs`:

```csharp
namespace Capacitor.Cli.Core.Harness;

public enum SettingsEdit { Changed, Unchanged, Malformed, Failed }
```

`JsonSettingsFile.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Harness;

/// <summary>Edits a vendor's JSON settings file in place of the user's own: an unparseable file is
/// refused rather than replaced, because resetting it would discard every setting the user has.</summary>
public static class JsonSettingsFile {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static SettingsEdit Edit(string path, Func<JsonObject, bool> edit, bool createIfMissing = true) {
        try {
            JsonObject root;

            if (File.Exists(path)) {
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(File.ReadAllText(path)); } catch (JsonException) { return SettingsEdit.Malformed; }
                if (parsed is not JsonObject obj) return SettingsEdit.Malformed;
                root = obj;
            } else {
                if (!createIfMissing) return SettingsEdit.Unchanged;
                root = [];
            }

            if (!edit(root)) return SettingsEdit.Unchanged;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, root.ToJsonString(WriteOpts));

            return SettingsEdit.Changed;
        } catch (IOException) {
            return SettingsEdit.Failed;
        } catch (UnauthorizedAccessException) {
            return SettingsEdit.Failed;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: same as Step 2. Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Harness/JsonSettingsFile.cs src/Capacitor.Cli.Core/Harness/SettingsEdit.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/JsonSettingsFileTests.cs
git commit -m "Add an atomic settings editor that refuses malformed files"
```

---

### Task 3: Claude plugin writer in Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Claude/ClaudePluginWriter.cs`
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs:2373-2425` (`InstallPlugin` delegates)
- Modify: `src/Capacitor.Cli/Commands/PluginCommand.cs:223-256` (`RemoveClaudePlugin` delegates; keep `ClaudeRemovalOutcome`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudePluginWriterTests.cs`
- Modify test: `test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs:552` (`InstallPlugin_MalformedJson_StartsFromScratch`)

**Interfaces:**
- Consumes: `JsonSettingsFile.Edit`, `SettingsEdit` (Task 2); `ClaudePluginInstaller.WriteMarker/DeleteMarker` (existing).
- Produces:
  - `public static SettingsEdit ClaudePluginWriter.Install(string settingsPath, string marketplacePath)` — writes `extraKnownMarketplaces.kcap` and `enabledPlugins["kcap@kcap"] = true`, removes legacy keys, writes the marker on `Changed` or `Unchanged`.
  - `public static SettingsEdit ClaudePluginWriter.Remove(string settingsPath)` — removes the same keys; deletes the marker on `Changed`; a missing file returns `Unchanged`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudePluginWriterTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Install_enables_the_plugin_and_registers_the_marketplace() {
        var settings = Tmp.PathTo("settings.json");

        var result = ClaudePluginWriter.Install(settings, "/opt/kcap/plugin");

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["enabledPlugins"]!["kcap@kcap"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>()).IsEqualTo("/opt/kcap/plugin");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settings)).IsTrue();
    }

    [Test]
    public async Task Install_removes_legacy_entries_and_keeps_user_settings() {
        var settings = Tmp.CreateFile("settings.json", """
            { "model": "opus", "enabledPlugins": { "kapacitor@kapacitor": true, "other@x": true },
              "extraKnownMarketplaces": { "kurrent": {} } }
            """);

        ClaudePluginWriter.Install(settings, "/p");

        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["model"]!.GetValue<string>()).IsEqualTo("opus");
        await Assert.That(root["enabledPlugins"]!["other@x"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["enabledPlugins"]!["kapacitor@kapacitor"]).IsNull();
        await Assert.That(root["extraKnownMarketplaces"]!["kurrent"]).IsNull();
    }

    [Test]
    public async Task Install_refuses_a_malformed_settings_file() {
        var settings = Tmp.CreateFile("settings.json", "{ broken");

        await Assert.That(ClaudePluginWriter.Install(settings, "/p")).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(settings)).IsEqualTo("{ broken");
    }

    [Test]
    public async Task Remove_drops_only_kcap_entries() {
        var settings = Tmp.PathTo("settings.json");
        ClaudePluginWriter.Install(settings, "/p");
        var root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        root["enabledPlugins"]!["other@x"] = true;
        File.WriteAllText(settings, root.ToJsonString());

        var result = ClaudePluginWriter.Remove(settings);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var after = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(after["enabledPlugins"]!["kcap@kcap"]).IsNull();
        await Assert.That(after["enabledPlugins"]!["other@x"]!.GetValue<bool>()).IsTrue();
        await Assert.That(ClaudePluginInstaller.ReadMarker(settings)).IsNull();
    }

    [Test]
    public async Task Remove_on_a_missing_file_is_unchanged() {
        await Assert.That(ClaudePluginWriter.Remove(Tmp.PathTo("nope.json"))).IsEqualTo(SettingsEdit.Unchanged);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePluginWriterTests/*"`
Expected: build error — `ClaudePluginWriter` not found.

- [ ] **Step 3: Implement the writer**

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Claude;

public static class ClaudePluginWriter {
    static readonly string[] LegacyMarketplaces = ["kurrent", "kapacitor"];
    static readonly string[] LegacyPlugins      = ["kcap@kurrent", "kapacitor@kapacitor", "kapacitor@kurrent"];

    public static SettingsEdit Install(string settingsPath, string marketplacePath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var marketplaces = root["extraKnownMarketplaces"] as JsonObject ?? [];
            root["extraKnownMarketplaces"] = marketplaces;
            var before = marketplaces["kcap"]?["source"]?["path"]?.GetValue<string>();
            marketplaces["kcap"] = new JsonObject {
                ["source"] = new JsonObject { ["source"] = "directory", ["path"] = marketplacePath }
            };
            var changed = before != marketplacePath;
            foreach (var name in LegacyMarketplaces) changed |= marketplaces.Remove(name);

            var enabled = root["enabledPlugins"] as JsonObject ?? [];
            root["enabledPlugins"] = enabled;
            changed |= enabled["kcap@kcap"]?.GetValue<bool>() != true;
            enabled["kcap@kcap"] = true;
            foreach (var name in LegacyPlugins) changed |= enabled.Remove(name);

            return changed;
        });

        if (result is SettingsEdit.Changed or SettingsEdit.Unchanged) ClaudePluginInstaller.WriteMarker(settingsPath);

        return result;
    }

    public static SettingsEdit Remove(string settingsPath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var changed = false;
            if (root["enabledPlugins"] is JsonObject enabled) {
                changed |= enabled.Remove("kcap@kcap");
                foreach (var name in LegacyPlugins) changed |= enabled.Remove(name);
            }
            if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
                changed |= marketplaces.Remove("kcap");
                foreach (var name in LegacyMarketplaces) changed |= marketplaces.Remove(name);
            }
            return changed;
        }, createIfMissing: false);

        if (result is SettingsEdit.Changed) ClaudePluginInstaller.DeleteMarker(settingsPath);

        return result;
    }
}
```

- [ ] **Step 4: Delegate the CLI call sites**

In `SetupCommand.cs`, replace the whole body of `InstallPlugin`:

```csharp
    internal static bool InstallPlugin(string settingsPath, string marketplacePath) =>
        ClaudePluginWriter.Install(settingsPath, marketplacePath) is SettingsEdit.Changed or SettingsEdit.Unchanged;
```

In `PluginCommand.cs`, replace the body of `RemoveClaudePlugin` (keep the signature and the enum):

```csharp
    public static ClaudeRemovalOutcome RemoveClaudePlugin(string settingsPath) =>
        ClaudePluginWriter.Remove(settingsPath) switch {
            SettingsEdit.Changed   => ClaudeRemovalOutcome.Removed,
            SettingsEdit.Malformed => ClaudeRemovalOutcome.Malformed,
            SettingsEdit.Failed    => throw new IOException($"Could not write {settingsPath}."),
            _                      => ClaudeRemovalOutcome.NotInstalled,
        };
```

Add `using Capacitor.Cli.Core.Harness;` and `using Capacitor.Cli.Core.Harness.Claude;` where missing (IDE0005 fails the build on unused usings, so add only what is used).

- [ ] **Step 5: Update the setup test whose behavior changed**

In `SetupCommandTests.cs`, rename `InstallPlugin_MalformedJson_StartsFromScratch` to `InstallPlugin_MalformedJson_LeavesFileUntouched` and change its assertions to:

```csharp
        await Assert.That(SetupCommand.InstallPlugin(settingsPath, pluginDir)).IsFalse();
        await Assert.That(File.ReadAllText(settingsPath)).IsEqualTo(originalMalformedText);
```

(keep its existing arrange step; capture the malformed text it writes into `originalMalformedText`).

- [ ] **Step 6: Run the tests**

Run: Core filter from Step 2 → 5 passed. Then
`dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/SetupCommandTests/*"` and `"/*/*/PluginCommandClaudeTests/*"` → all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/Harness/Claude/ClaudePluginWriter.cs src/Capacitor.Cli/Commands/SetupCommand.cs src/Capacitor.Cli/Commands/PluginCommand.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudePluginWriterTests.cs test/Capacitor.Cli.Tests.Unit/Commands/SetupCommandTests.cs
git commit -m "Write the Claude plugin settings atomically from Core"
```

---

### Task 4: Codex hooks writer in Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Codex/CodexHooksWriter.cs`
- Modify: `src/Capacitor.Cli/Commands/PluginCommand.cs:602-700` (`InstallCodexHooks`, `RemoveCodexHooks` delegate; remove the now-unused `CodexHookCommand`, `PermissionRequestTimeout`, `DefaultHookTimeout` constants only if nothing else in the file uses them)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexHooksWriterTests.cs`

**Interfaces:**
- Consumes: `JsonSettingsFile.Edit` (Task 2); `CodexHooksParser.CodexHookEvents`, `CodexHooksParser.EntryReferencesCapacitorCodexHook`, `CodexHooksInstaller.WriteMarker/DeleteMarker` (existing).
- Produces:
  - `public const string CodexHooksWriter.HookCommand = "kcap hook --codex";`
  - `public static SettingsEdit CodexHooksWriter.Install(string hooksPath)` — one kcap entry per event in `CodexHookEvents` (timeout 86400 for `PermissionRequest`, 30 otherwise), non-kcap entries preserved; marker written on `Changed`/`Unchanged`.
  - `public static SettingsEdit CodexHooksWriter.Remove(string hooksPath)` — removes kcap entries; marker deleted on `Changed`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexHooksWriterTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Install_adds_a_kcap_hook_for_every_event() {
        var hooks = Tmp.PathTo("hooks.json");

        await Assert.That(CodexHooksWriter.Install(hooks)).IsEqualTo(SettingsEdit.Changed);

        var root = JsonNode.Parse(File.ReadAllText(hooks))!["hooks"]!;
        foreach (var evt in CodexHooksParser.CodexHookEvents) {
            var entries = root[evt]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);
            await Assert.That(entries[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo(CodexHooksWriter.HookCommand);
        }
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(hooks)).IsTrue();
    }

    [Test]
    public async Task Install_keeps_foreign_hooks_and_replaces_its_own() {
        var hooks = Tmp.CreateFile("hooks.json", """
            { "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "my-tool" } ] },
                                    { "hooks": [ { "type": "command", "command": "kcap hook --codex" } ] } ] } }
            """);

        CodexHooksWriter.Install(hooks);

        var stop = JsonNode.Parse(File.ReadAllText(hooks))!["hooks"]!["Stop"]!.AsArray();
        await Assert.That(stop.Count).IsEqualTo(2);
        await Assert.That(stop[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("my-tool");
    }

    [Test]
    public async Task Install_refuses_malformed_hooks() {
        var hooks = Tmp.CreateFile("hooks.json", "nope");

        await Assert.That(CodexHooksWriter.Install(hooks)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(hooks)).IsEqualTo("nope");
    }

    [Test]
    public async Task Remove_drops_only_kcap_entries() {
        var hooks = Tmp.PathTo("hooks.json");
        CodexHooksWriter.Install(hooks);

        await Assert.That(CodexHooksWriter.Remove(hooks)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(hooks)).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHooksWriterTests/*"`
Expected: build error — `CodexHooksWriter` not found.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Codex;

public static class CodexHooksWriter {
    public const string HookCommand = "kcap hook --codex";

    // PermissionRequest waits for the dashboard's decision, so Codex must not time it out.
    const int PermissionRequestTimeout = 86400;
    const int DefaultHookTimeout       = 30;

    public static SettingsEdit Install(string hooksPath) {
        var result = JsonSettingsFile.Edit(hooksPath, root => {
            var hooks = root["hooks"] as JsonObject ?? [];
            root["hooks"] = hooks;
            var before = hooks.ToJsonString();

            foreach (var evt in CodexHooksParser.CodexHookEvents) {
                var entry = new JsonObject {
                    ["hooks"] = new JsonArray(new JsonObject {
                        ["type"]    = "command",
                        ["command"] = HookCommand,
                        ["timeout"] = evt == "PermissionRequest" ? PermissionRequestTimeout : DefaultHookTimeout,
                    })
                };

                var kept = new JsonArray();
                if (hooks[evt] is JsonArray existing)
                    foreach (var e in existing)
                        if (e is not null && !CodexHooksParser.EntryReferencesCapacitorCodexHook(e)) kept.Add(e.DeepClone());

                kept.Add((JsonNode)entry);
                hooks[evt] = kept;
            }

            return hooks.ToJsonString() != before;
        });

        if (result is SettingsEdit.Changed or SettingsEdit.Unchanged) CodexHooksInstaller.WriteMarker(hooksPath);

        return result;
    }

    public static SettingsEdit Remove(string hooksPath) {
        var result = JsonSettingsFile.Edit(hooksPath, root => {
            if (root["hooks"] is not JsonObject hooks) return false;
            var changed = false;

            foreach (var evt in CodexHooksParser.CodexHookEvents) {
                if (hooks[evt] is not JsonArray entries) continue;
                var kept = new JsonArray();
                foreach (var e in entries) {
                    if (e is null) continue;
                    if (CodexHooksParser.EntryReferencesCapacitorCodexHook(e)) changed = true;
                    else kept.Add(e.DeepClone());
                }
                hooks[evt] = kept;
            }

            return changed;
        }, createIfMissing: false);

        if (result is SettingsEdit.Changed) CodexHooksInstaller.DeleteMarker(hooksPath);

        return result;
    }
}
```

- [ ] **Step 4: Delegate the CLI call sites**

In `PluginCommand.cs`:

```csharp
    public static bool InstallCodexHooks(string hooksPath) =>
        CodexHooksWriter.Install(hooksPath) is SettingsEdit.Changed or SettingsEdit.Unchanged;

    public static bool RemoveCodexHooks(string hooksPath) =>
        CodexHooksWriter.Remove(hooksPath) switch {
            SettingsEdit.Changed => true,
            SettingsEdit.Failed  => throw new IOException($"Could not write {hooksPath}."),
            _                    => false,
        };
```

Keep the existing doc comment on `RemoveCodexHooks`. Delete `CodexHookCommand`, `PermissionRequestTimeout`, `DefaultHookTimeout` from `PluginCommand` only if the compiler reports them unused.

- [ ] **Step 5: Run the tests**

Run: Core filter from Step 2 → 4 passed. Then `"/*/*/PluginCommandCodexTests/*"` and `"/*/*/UninstallCommandTests/*"` in the Cli unit suite → all pass. If a Cli test asserted the old "malformed → start fresh" behavior for Codex hooks, change it to assert the file is left untouched and the install returns false.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Harness/Codex/CodexHooksWriter.cs src/Capacitor.Cli/Commands/PluginCommand.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexHooksWriterTests.cs test/Capacitor.Cli.Tests.Unit/Commands
git commit -m "Write Codex hooks atomically from Core"
```

---

### Task 5: Account registry store

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/VendorAccount.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/AccountRegistry.cs` (document record + its JSON context)
- Create: `src/Capacitor.Cli.Core/Accounts/AccountStore.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/AccountDirectory.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountStoreTests.cs`

**Interfaces:**
- Consumes: `ConfigFileLock.Acquire(string, TimeSpan?)`, `AtomicFile.Replace(path, contents, mode)`, `DaemonStore.Directory`.
- Produces:
  - `public sealed record VendorAccount(string Id, HarnessId Vendor, string Directory, string Label, DateTimeOffset AddedAt)`
  - `public sealed record AccountRegistry { int Version = 1; long Revision; IReadOnlyList<VendorAccount> Accounts = []; }`
  - `public static class AccountDirectory { public static string Normalize(string path); public static bool Same(string a, string b); }` — full path, trailing separators trimmed, the final component's symlink resolved when it is one; `Same` is ordinal on Linux, ordinal-ignore-case on macOS and Windows.
  - `public sealed class AccountStore(string directory)`:
    - `public string Directory { get; }`
    - `public static AccountStore Beside(DaemonStore daemons)` → `new(Path.Combine(Path.GetDirectoryName(daemons.Directory)!, "accounts"))`
    - `public AccountRegistry Load()` — missing file → empty registry with `Revision = 0`; unparseable → throws `InvalidDataException`.
    - `public T Mutate<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change)` — under the lock: load, apply; if `Next` is not reference-equal to the loaded registry, increment `Revision` and save.
    - `public IDisposable Lock()` — the registry lock, for wiring runs that must serialize with mutations.
    - `public string HostId()` — reads `host.json` or creates it (a new GUID, "N" format) under the lock.
    - `public VendorAccount? Find(HarnessId vendor, string directory)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    AccountStore Store() => new(Tmp.PathTo("accounts"));

    static VendorAccount Claude(string dir) =>
        new(Guid.NewGuid().ToString("N"), HarnessId.Claude, dir, Path.GetFileName(dir), DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Load_of_a_missing_store_is_empty() {
        var registry = Store().Load();

        await Assert.That(registry.Accounts.Count).IsEqualTo(0);
        await Assert.That(registry.Revision).IsEqualTo(0L);
    }

    [Test]
    public async Task Mutate_persists_and_increments_the_revision() {
        var store = Store();

        store.Mutate(r => (r with { Accounts = [.. r.Accounts, Claude("/h/.claude-work")] }, 0));

        var loaded = store.Load();
        await Assert.That(loaded.Accounts.Count).IsEqualTo(1);
        await Assert.That(loaded.Revision).IsEqualTo(1L);
    }

    [Test]
    public async Task Mutate_without_a_change_keeps_the_revision() {
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude("/h/.claude")] }, 0));

        store.Mutate(r => (r, 0));

        await Assert.That(store.Load().Revision).IsEqualTo(1L);
    }

    [Test]
    public async Task Concurrent_mutations_lose_no_account() {
        var store = Store();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            store.Mutate(r => (r with { Accounts = [.. r.Accounts, Claude($"/h/.claude-{i}")] }, 0)))));

        var loaded = store.Load();
        await Assert.That(loaded.Accounts.Count).IsEqualTo(20);
        await Assert.That(loaded.Revision).IsEqualTo(20L);
    }

    [Test]
    public async Task Store_files_are_owner_only() {
        if (OperatingSystem.IsWindows()) return;
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude("/h/.claude")] }, 0));

        await Assert.That(File.GetUnixFileMode(store.Directory) & (UnixFileMode)0b111_111)
            .IsEqualTo((UnixFileMode)0);
        await Assert.That(File.GetUnixFileMode(Path.Combine(store.Directory, "accounts.json")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task HostId_is_stable() {
        var store = Store();

        await Assert.That(store.HostId()).IsEqualTo(store.HostId());
        await Assert.That(new AccountStore(store.Directory).HostId()).IsEqualTo(store.HostId());
    }

    [Test]
    public async Task Beside_places_the_store_next_to_the_daemons_directory() {
        var daemons = new DaemonStore(Tmp.PathTo("cfg", "daemons"));

        await Assert.That(AccountStore.Beside(daemons).Directory).IsEqualTo(Tmp.PathTo("cfg", "accounts"));
    }

    [Test]
    public async Task Normalize_ignores_a_trailing_separator() {
        var dir = Tmp.CreateDir(".claude-work");

        await Assert.That(AccountDirectory.Normalize(dir + Path.DirectorySeparatorChar))
            .IsEqualTo(AccountDirectory.Normalize(dir));
    }

    [Test]
    public async Task Find_matches_a_differently_spelled_directory() {
        var dir   = Tmp.CreateDir(".claude-work");
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude(AccountDirectory.Normalize(dir))] }, 0));

        await Assert.That(store.Find(HarnessId.Claude, dir + Path.DirectorySeparatorChar)).IsNotNull();
        await Assert.That(store.Find(HarnessId.Codex, dir)).IsNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/AccountStoreTests/*"`
Expected: build error — namespace `Capacitor.Cli.Core.Accounts` not found.

- [ ] **Step 3: Implement**

`VendorAccount.cs`:

```csharp
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>One vendor config directory kcap records: a Claude <c>CLAUDE_CONFIG_DIR</c> or a Codex
/// <c>CODEX_HOME</c>. <see cref="Directory"/> is normalized and is the account's key.</summary>
public sealed record VendorAccount(
    [property: JsonPropertyName("id")]        string         Id,
    [property: JsonPropertyName("vendor")]    HarnessId      Vendor,
    [property: JsonPropertyName("directory")] string         Directory,
    [property: JsonPropertyName("label")]     string         Label,
    [property: JsonPropertyName("added_at")]  DateTimeOffset AddedAt);
```

`AccountRegistry.cs`:

```csharp
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

public sealed record AccountRegistry {
    [JsonPropertyName("version")]  public int                           Version  { get; init; } = 1;
    [JsonPropertyName("revision")] public long                          Revision { get; init; }
    [JsonPropertyName("accounts")] public IReadOnlyList<VendorAccount>  Accounts { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AccountRegistry))]
[JsonSerializable(typeof(HostIdentity))]
internal partial class AccountRegistryJsonContext : JsonSerializerContext;

internal sealed record HostIdentity([property: JsonPropertyName("host_id")] string HostId);
```

(`HostIdentity` rides in this file because it exists only to be serialized by this context.)

`AccountDirectory.cs`:

```csharp
namespace Capacitor.Cli.Core.Accounts;

public static class AccountDirectory {
    public static string Normalize(string path) {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try {
            if (new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return Path.TrimEndingDirectorySeparator(target.FullName);
        } catch (IOException) { }
        return full;
    }

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
```

`AccountStore.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The per-user list of vendor accounts. Beside the daemons directory and, like it, blind
/// to <c>KCAP_CONFIG_DIR</c>: two config roots on one machine must see one list, or each would wire
/// the same vendor settings file and undo the other.</summary>
public sealed class AccountStore(string directory) {
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDir  = OwnerOnlyFile | UnixFileMode.UserExecute;

    public string Directory { get; } = directory;

    string RegistryPath => Path.Combine(Directory, "accounts.json");
    string LockPath     => Path.Combine(Directory, "accounts.lock");
    string HostPath     => Path.Combine(Directory, "host.json");

    public static AccountStore Beside(DaemonStore daemons) =>
        new(Path.Combine(Path.GetDirectoryName(daemons.Directory)!, "accounts"));

    public IDisposable Lock() => ConfigFileLock.Acquire(LockPath);

    public AccountRegistry Load() {
        if (!File.Exists(RegistryPath)) return new AccountRegistry();
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(RegistryPath), AccountRegistryJsonContext.Default.AccountRegistry)
                ?? throw new InvalidDataException($"{RegistryPath} is empty.");
        } catch (JsonException ex) {
            throw new InvalidDataException($"{RegistryPath} is not a valid account registry.", ex);
        }
    }

    public T Mutate<T>(Func<AccountRegistry, (AccountRegistry Next, T Result)> change) {
        using var _ = Lock();
        var current = Load();
        var (next, result) = change(current);
        if (!ReferenceEquals(next, current)) Save(next with { Revision = current.Revision + 1 });
        return result;
    }

    public VendorAccount? Find(HarnessId vendor, string directory) =>
        Load().Accounts.FirstOrDefault(a => a.Vendor == vendor && AccountDirectory.Same(a.Directory, directory));

    public string HostId() {
        using var _ = Lock();
        if (File.Exists(HostPath)
         && JsonSerializer.Deserialize(File.ReadAllText(HostPath), AccountRegistryJsonContext.Default.HostIdentity) is { } id)
            return id.HostId;

        var created = new HostIdentity(Guid.NewGuid().ToString("N"));
        EnsureDirectory();
        AtomicFile.Replace(HostPath, JsonSerializer.Serialize(created, AccountRegistryJsonContext.Default.HostIdentity), OwnerOnlyFile);
        return created.HostId;
    }

    void Save(AccountRegistry registry) {
        EnsureDirectory();
        AtomicFile.Replace(RegistryPath, JsonSerializer.Serialize(registry, AccountRegistryJsonContext.Default.AccountRegistry), OwnerOnlyFile);
    }

    void EnsureDirectory() {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
        else System.IO.Directory.CreateDirectory(Directory, OwnerOnlyDir);
    }
}
```

Note: `HostId()` calls `Lock()` and `Mutate` holds `Lock()`; never call `HostId()` from inside a `Mutate` delegate. `Directory.CreateDirectory(path, mode)` sets the mode on the leaf only — parents keep the umask default, which is fine because the parent is the user's `~/.config/kcap`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: same as Step 2. Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Accounts test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountStoreTests.cs
git commit -m "Add the per-user vendor account registry"
```

---

### Task 6: Account layouts and path attribution

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/AccountLayouts.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/AccountPaths.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountLayoutsTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountPathsTests.cs`

**Interfaces:**
- Consumes: `VendorAccount`, `AccountRegistry`, `AccountDirectory` (Task 5); `ClaudePaths(UserHome, string?)`, `CodexPaths(UserHome, string?)` (existing).
- Produces:
  - `public static ClaudePaths AccountLayouts.Claude(UserHome home, string directory)` — maps the default `~/.claude` to a null config dir.
  - `public static CodexPaths AccountLayouts.Codex(UserHome home, string directory)` — maps the default `~/.codex` to null.
  - `public static string AccountLayouts.DefaultDirectory(HarnessId vendor, UserHome home)` — `~/.claude` / `~/.codex`.
  - `public static ClaudePaths? AccountPaths.ClaudeForTranscript(string transcriptPath, AccountRegistry registry, UserHome home)` — the registered Claude account whose `projects/` contains the path, else null.
  - `public static CodexPaths? AccountPaths.CodexForRollout(string rolloutPath, AccountRegistry registry, UserHome home)` — the registered Codex account whose `sessions/` contains the path, else null.

- [ ] **Step 1: Write the failing tests**

`AccountLayoutsTests.cs`:

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountLayoutsTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task Default_claude_directory_keeps_the_sibling_user_config() {
        var paths = AccountLayouts.Claude(Home, Home.PathTo(".claude"));

        await Assert.That(paths.UserConfigJson).IsEqualTo(new ClaudePaths(Home, null).UserConfigJson);
    }

    [Test]
    public async Task Other_claude_directory_relocates_everything() {
        var paths = AccountLayouts.Claude(Home, Home.PathTo(".claude-work"));

        await Assert.That(paths.UserSettings).IsEqualTo(Home.PathTo(".claude-work", "settings.json"));
        await Assert.That(paths.UserConfigJson).IsEqualTo(Home.PathTo(".claude-work", ".claude.json"));
    }

    [Test]
    public async Task Codex_layout_points_at_the_account_home() {
        var paths = AccountLayouts.Codex(Home, Home.PathTo(".codex-b"));

        await Assert.That(paths.Sessions).IsEqualTo(Home.PathTo(".codex-b", "sessions"));
    }
}
```

`AccountPathsTests.cs`:

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountPathsTests {
    [TempHome] public required TempHome Home { get; init; }

    AccountRegistry Registry(params (HarnessId Vendor, string Dir)[] accounts) => new() {
        Accounts = [.. accounts.Select(a => new VendorAccount(a.Dir, a.Vendor, AccountDirectory.Normalize(a.Dir), a.Dir, DateTimeOffset.UnixEpoch))]
    };

    [Test]
    public async Task Claude_transcript_resolves_to_its_account() {
        var work       = Home.CreateDir(".claude-work");
        var transcript = Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", "");

        var paths = AccountPaths.ClaudeForTranscript(transcript, Registry((HarnessId.Claude, work)), Home);

        await Assert.That(paths!.Plans).IsEqualTo(Home.PathTo(".claude-work", "plans"));
    }

    [Test]
    public async Task Claude_transcript_outside_every_account_is_null() {
        var work = Home.CreateDir(".claude-work");

        var paths = AccountPaths.ClaudeForTranscript("/tmp/elsewhere/x.jsonl", Registry((HarnessId.Claude, work)), Home);

        await Assert.That(paths).IsNull();
    }

    [Test]
    public async Task Codex_rollout_resolves_to_its_home() {
        var b       = Home.CreateDir(".codex-b");
        var rollout = Home.CreateFile(".codex-b/sessions/2026/10/08/rollout-x.jsonl", "");

        var paths = AccountPaths.CodexForRollout(rollout, Registry((HarnessId.Codex, b)), Home);

        await Assert.That(paths!.Home).IsEqualTo(AccountDirectory.Normalize(b));
    }

    [Test]
    public async Task A_vendor_mismatch_does_not_resolve() {
        var work       = Home.CreateDir(".claude-work");
        var transcript = Home.CreateFile(".claude-work/projects/-repo/abc.jsonl", "");

        await Assert.That(AccountPaths.CodexForRollout(transcript, Registry((HarnessId.Claude, work)), Home)).IsNull();
    }
}
```

If `TempHome` has no `CreateDir`, use `Directory.CreateDirectory(Home.PathTo(...)).FullName`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/AccountL*/*"` then `"/*/*/AccountPathsTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

`AccountLayouts.cs`:

```csharp
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Accounts;

public static class AccountLayouts {
    public static string DefaultDirectory(HarnessId vendor, UserHome home) => vendor switch {
        HarnessId.Claude => new ClaudePaths(home, null).Home,
        HarnessId.Codex  => new CodexPaths(home, null).Home,
        _                => throw new ArgumentOutOfRangeException(nameof(vendor), vendor, "Accounts cover Claude and Codex only."),
    };

    // The default directory must stay a null override: naming ~/.claude as CLAUDE_CONFIG_DIR moves
    // .claude.json inside it, away from the ~/.claude.json Claude actually reads by default.
    public static ClaudePaths Claude(UserHome home, string directory) =>
        new(home, AccountDirectory.Same(directory, DefaultDirectory(HarnessId.Claude, home)) ? null : directory);

    public static CodexPaths Codex(UserHome home, string directory) =>
        new(home, AccountDirectory.Same(directory, DefaultDirectory(HarnessId.Codex, home)) ? null : directory);
}
```

`AccountPaths.cs`:

```csharp
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>The registered account a vendor-supplied path belongs to. Hooks carry no account and
/// may run with <c>CLAUDE_CONFIG_DIR</c> scrubbed, so the transcript path is the evidence.</summary>
public static class AccountPaths {
    public static ClaudePaths? ClaudeForTranscript(string transcriptPath, AccountRegistry registry, UserHome home) =>
        registry.Accounts
            .Where(a => a.Vendor == HarnessId.Claude)
            .Select(a => AccountLayouts.Claude(home, a.Directory))
            .FirstOrDefault(p => Contains(p.Projects, transcriptPath));

    public static CodexPaths? CodexForRollout(string rolloutPath, AccountRegistry registry, UserHome home) =>
        registry.Accounts
            .Where(a => a.Vendor == HarnessId.Codex)
            .Select(a => AccountLayouts.Codex(home, a.Directory))
            .FirstOrDefault(p => Contains(p.Sessions, rolloutPath));

    static bool Contains(string root, string path) {
        var r = AccountDirectory.Normalize(root) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return p.StartsWith(r, cmp) || AccountDirectory.Normalize(Path.GetDirectoryName(p) ?? p).StartsWith(r, cmp);
    }
}
```

(The second comparison covers a transcript whose own parent is reached through a symlink.)

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Accounts/AccountLayouts.cs src/Capacitor.Cli.Core/Accounts/AccountPaths.cs test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountLayoutsTests.cs test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountPathsTests.cs
git commit -m "Resolve a vendor layout per registered account"
```

---

### Task 7: Account wiring service and recording state

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/RecordingState.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/WiringStep.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/WiringOptions.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/AccountWiring.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountWiringTests.cs`

**Interfaces:**
- Consumes: `ClaudePluginWriter` (Task 3), `CodexHooksWriter` (Task 4), `AccountLayouts` (Task 6), `CodexConfigToml.RegisterKcapMcpServers/UnregisterKcapMcpServers/EnableNetworkAccess`, `AgentsSkillsInstaller.Install`, `ClaudePluginInstaller.IsPluginEnabled/IsEffectivelyInstalled`, `CodexHooksInstaller.ReferencesKcapHook`.
- Produces:
  - `public enum RecordingState { NotWired, Broken, Installed, Recording }` (`Installed` = Codex hooks present, trust not verified.)
  - `public sealed record WiringStep(string Name, bool Succeeded, string Detail)`
  - `public sealed record WiringOptions(string? PluginDir, string AgentsSkillsDir, Func<string?>? ResolveMcpBinaryPath, IReadOnlyCollection<string>? NetworkAllowDomains)` — `NetworkAllowDomains` null skips enabling Codex network access.
  - `public static IReadOnlyList<WiringStep> AccountWiring.Wire(VendorAccount account, UserHome home, WiringOptions options)`
  - `public static IReadOnlyList<WiringStep> AccountWiring.Unwire(VendorAccount account, UserHome home)`
  - `public static RecordingState AccountWiring.State(VendorAccount account, UserHome home)`
  - `public static bool AccountWiring.Succeeded(IReadOnlyList<WiringStep> steps)`

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountWiringTests {
    [TempHome] public required TempHome Home { get; init; }

    VendorAccount Account(HarnessId vendor, string dirName) {
        var dir = Directory.CreateDirectory(Home.PathTo(dirName)).FullName;
        return new(dirName, vendor, AccountDirectory.Normalize(dir), dirName, DateTimeOffset.UnixEpoch);
    }

    WiringOptions Options(string? pluginDir = null) =>
        new(pluginDir ?? Directory.CreateDirectory(Home.PathTo("plugin")).FullName, Home.PathTo(".agents", "skills"),
            () => "/usr/local/bin/kcap", NetworkAllowDomains: null);

    [Test]
    public async Task Wiring_a_claude_account_enables_the_plugin_in_its_own_settings() {
        var work = Account(HarnessId.Claude, ".claude-work");

        var steps = AccountWiring.Wire(work, Home, Options());

        await Assert.That(AccountWiring.Succeeded(steps)).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".claude", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task Wiring_two_codex_accounts_tracks_mcp_ownership_per_home() {
        var a = Account(HarnessId.Codex, ".codex");
        var b = Account(HarnessId.Codex, ".codex-b");

        AccountWiring.Wire(a, Home, Options());
        AccountWiring.Wire(b, Home, Options());

        await Assert.That(File.Exists(Home.PathTo(".codex", "mcp-ownership-v1.json"))).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".codex-b", "mcp-ownership-v1.json"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsTrue();
    }

    [Test]
    public async Task Unwire_removes_only_what_kcap_added() {
        var b = Account(HarnessId.Codex, ".codex-b");
        File.WriteAllText(Home.PathTo(".codex-b", "config.toml"), "[mcp_servers.mine]\ncommand = \"x\"\n");
        AccountWiring.Wire(b, Home, Options());

        AccountWiring.Unwire(b, Home);

        await Assert.That(File.ReadAllText(Home.PathTo(".codex-b", "config.toml"))).Contains("[mcp_servers.mine]");
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsFalse();
    }

    [Test]
    public async Task Unwiring_one_of_two_codex_accounts_keeps_the_shared_agent_skills() {
        var a = Account(HarnessId.Codex, ".codex");
        var b = Account(HarnessId.Codex, ".codex-b");
        AccountWiring.Wire(a, Home, Options());
        AccountWiring.Wire(b, Home, Options());

        AccountWiring.Unwire(b, Home);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(Home.PathTo(".agents", "skills"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex", "hooks.json"))).IsTrue();
    }

    [Test]
    public async Task Malformed_settings_make_one_account_broken_and_leave_the_file() {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), "{ nope");

        var steps = AccountWiring.Wire(work, Home, Options());

        await Assert.That(AccountWiring.Succeeded(steps)).IsFalse();
        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Broken);
        await Assert.That(File.ReadAllText(Home.PathTo(".claude-work", "settings.json"))).IsEqualTo("{ nope");
    }

    [Test]
    public async Task Claude_plugin_enabled_without_its_payload_is_broken() {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), """{ "enabledPlugins": { "kcap@kcap": true } }""");

        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Broken);
    }

    [Test]
    public async Task Codex_hooks_present_report_installed() {
        var b = Account(HarnessId.Codex, ".codex-b");
        AccountWiring.Wire(b, Home, Options());

        await Assert.That(AccountWiring.State(b, Home)).IsEqualTo(RecordingState.Installed);
    }

    [Test]
    public async Task An_untouched_account_is_not_wired() {
        await Assert.That(AccountWiring.State(Account(HarnessId.Claude, ".claude-x"), Home)).IsEqualTo(RecordingState.NotWired);
    }

    [Test]
    public async Task Codex_config_stays_owner_only_after_wiring() {
        if (OperatingSystem.IsWindows()) return;
        var b = Account(HarnessId.Codex, ".codex-b");

        AccountWiring.Wire(b, Home, Options());

        await Assert.That(File.GetUnixFileMode(Home.PathTo(".codex-b", "config.toml")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
```

The Codex skills step needs a plugin dir with `skills/<name>` for each `AgentsSkillsInstaller.SourceNames`. In `Options()`, create them: `foreach (var n in AgentsSkillsInstaller.SourceNames) Directory.CreateDirectory(Path.Combine(plugin, "skills", n));`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/AccountWiringTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

`RecordingState.cs`:

```csharp
namespace Capacitor.Cli.Core.Accounts;

public enum RecordingState { NotWired, Broken, Installed, Recording }
```

`WiringStep.cs`:

```csharp
namespace Capacitor.Cli.Core.Accounts;

public sealed record WiringStep(string Name, bool Succeeded, string Detail);
```

`WiringOptions.cs`:

```csharp
namespace Capacitor.Cli.Core.Accounts;

public sealed record WiringOptions(
    string?                      PluginDir,
    string                       AgentsSkillsDir,
    Func<string?>?               ResolveMcpBinaryPath,
    IReadOnlyCollection<string>? NetworkAllowDomains);
```

`AccountWiring.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Accounts;

public static class AccountWiring {
    public static bool Succeeded(IReadOnlyList<WiringStep> steps) => steps.All(s => s.Succeeded);

    public static IReadOnlyList<WiringStep> Wire(VendorAccount account, UserHome home, WiringOptions options) =>
        account.Vendor switch {
            HarnessId.Claude => WireClaude(AccountLayouts.Claude(home, account.Directory), options),
            HarnessId.Codex  => WireCodex(AccountLayouts.Codex(home, account.Directory), options),
            _                => [new WiringStep("vendor", false, $"{account.Vendor} has no accounts")],
        };

    public static IReadOnlyList<WiringStep> Unwire(VendorAccount account, UserHome home) {
        if (!Directory.Exists(account.Directory))
            return [new WiringStep("directory", true, $"{account.Directory} no longer exists; nothing to unwire")];

        return account.Vendor switch {
            HarnessId.Claude => [Step("plugin", ClaudePluginWriter.Remove(AccountLayouts.Claude(home, account.Directory).UserSettings))],
            HarnessId.Codex  => UnwireCodex(AccountLayouts.Codex(home, account.Directory)),
            _                => [],
        };
    }

    public static RecordingState State(VendorAccount account, UserHome home) {
        switch (account.Vendor) {
            case HarnessId.Claude: {
                var settings = AccountLayouts.Claude(home, account.Directory).UserSettings;
                if (IsMalformed(settings)) return RecordingState.Broken;
                if (!ClaudePluginInstaller.IsPluginEnabled(settings)) return RecordingState.NotWired;
                return ClaudePluginInstaller.IsEffectivelyInstalled(settings) ? RecordingState.Recording : RecordingState.Broken;
            }
            case HarnessId.Codex: {
                var hooks = AccountLayouts.Codex(home, account.Directory).UserHooksJson;
                if (IsMalformed(hooks)) return RecordingState.Broken;
                return CodexHooksInstaller.ReferencesKcapHook(hooks) ? RecordingState.Installed : RecordingState.NotWired;
            }
            default:
                return RecordingState.NotWired;
        }
    }

    static IReadOnlyList<WiringStep> WireClaude(ClaudePaths paths, WiringOptions options) =>
        options.PluginDir is null
            ? [new WiringStep("plugin", false, "kcap plugin directory not found; reinstall kcap via npm")]
            : [Step("plugin", ClaudePluginWriter.Install(paths.UserSettings, options.PluginDir))];

    static IReadOnlyList<WiringStep> WireCodex(CodexPaths paths, WiringOptions options) {
        var steps = new List<WiringStep> { Step("hooks", CodexHooksWriter.Install(paths.UserHooksJson)) };

        if (options.PluginDir is { } plugin) {
            var ok = AgentsSkillsInstaller.Install(Path.Combine(plugin, "skills"), options.AgentsSkillsDir);
            steps.Add(new WiringStep("skills", ok, ok ? options.AgentsSkillsDir : "could not install agent skills"));
        }

        if (options.NetworkAllowDomains is { } domains)
            steps.Add(Step("network", CodexConfigToml.EnableNetworkAccess(domains, paths.ConfigToml)));

        steps.Add(Step("mcp", CodexConfigToml.RegisterKcapMcpServers(paths.ConfigToml, options.ResolveMcpBinaryPath)));

        return steps;
    }

    static IReadOnlyList<WiringStep> UnwireCodex(CodexPaths paths) => [
        Step("hooks", CodexHooksWriter.Remove(paths.UserHooksJson)),
        Step("mcp", CodexConfigToml.UnregisterKcapMcpServers(paths.ConfigToml)),
    ];

    static WiringStep Step(string name, SettingsEdit edit) => edit switch {
        SettingsEdit.Malformed => new(name, false, "settings file unreadable; left unchanged"),
        SettingsEdit.Failed    => new(name, false, "could not write the settings file"),
        _                      => new(name, true, edit.ToString()),
    };

    static WiringStep Step(string name, CodexConfigToml.Change change) =>
        new(name, change is not CodexConfigToml.Change.Failed, change.ToString());

    static bool IsMalformed(string path) {
        if (!File.Exists(path)) return false;
        try { return JsonNode.Parse(File.ReadAllText(path)) is not JsonObject; } catch (JsonException) { return true; }
    }
}
```

Note: `AgentsSkillsInstaller.Install` writes progress to `Console.Error`; that matches today's `kcap plugin install --codex`. If `EnableNetworkAccess` returns a `Change`, `Step(name, Change)` covers it; if it returns another type, adapt the overload to its actual return type.

- [ ] **Step 4: Run the tests to verify they pass**

Expected: 9 passed. Then run `"/*/*/CodexConfigTomlTests/*"` in the Core suite — the existing owner-only test must still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Accounts test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountWiringTests.cs
git commit -m "Wire recording into a single vendor account"
```

---

### Task 8: Adoption, and `kcap plugin` iterating registered accounts

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/AccountAdoption.cs`
- Modify: `src/Capacitor.Cli/Commands/PluginEnvironment.cs` (add `AccountStore? Accounts { get; init; }`; set it in `FromProcess`)
- Modify: `src/Capacitor.Cli/Commands/CommandServices.cs:52` and `src/Capacitor.Cli/Commands/UninstallCommand.cs:132` (pass the store)
- Modify: `src/Capacitor.Cli/Commands/PluginCommand.cs` (`InstallClaude`, `RemoveClaude`, `InstallCodex`, `RemoveCodex` user-scope paths)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountAdoptionTests.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/PluginCommandAccountsTests.cs`

**Interfaces:**
- Consumes: Tasks 5–7.
- Produces:
  - `public static VendorAccount AccountAdoption.EnsureDefault(AccountStore store, HarnessId vendor, string environmentDirectory, TimeProvider time)` — registers the directory if no account of that vendor has it; returns the (existing or new) account. `environmentDirectory` is the env-derived layout's home (`harnesses.Of<ClaudeHarness>().Paths.Home` / `...Codex...`).
  - `public static IReadOnlyList<VendorAccount> AccountAdoption.Of(AccountStore store, HarnessId vendor)`.
  - `PluginEnvironment.Accounts` (null keeps today's single-layout behavior — every existing test constructs the record without it).

- [ ] **Step 1: Write the failing Core tests**

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountAdoptionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task EnsureDefault_registers_the_environment_directory_once() {
        var store = new AccountStore(Tmp.PathTo("accounts"));
        var dir   = Tmp.CreateDir(".claude");

        var first  = AccountAdoption.EnsureDefault(store, HarnessId.Claude, dir, TimeProvider.System);
        var second = AccountAdoption.EnsureDefault(store, HarnessId.Claude, dir + Path.DirectorySeparatorChar, TimeProvider.System);

        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(AccountAdoption.Of(store, HarnessId.Claude).Count).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureDefault_labels_the_account_after_its_directory() {
        var store = new AccountStore(Tmp.PathTo("accounts"));

        var account = AccountAdoption.EnsureDefault(store, HarnessId.Codex, Tmp.CreateDir(".codex"), TimeProvider.System);

        await Assert.That(account.Label).IsEqualTo(".codex");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `--treenode-filter "/*/*/AccountAdoptionTests/*"` in the Core suite. Expected: build error.

- [ ] **Step 3: Implement adoption**

```csharp
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

public static class AccountAdoption {
    public static IReadOnlyList<VendorAccount> Of(AccountStore store, HarnessId vendor) =>
        [.. store.Load().Accounts.Where(a => a.Vendor == vendor)];

    public static VendorAccount EnsureDefault(AccountStore store, HarnessId vendor, string environmentDirectory, TimeProvider time) =>
        store.Mutate(registry => {
            var existing = registry.Accounts.FirstOrDefault(a => a.Vendor == vendor && AccountDirectory.Same(a.Directory, environmentDirectory));
            if (existing is not null) return (registry, existing);

            var directory = AccountDirectory.Normalize(environmentDirectory);
            var added     = new VendorAccount(Guid.NewGuid().ToString("N"), vendor, directory, Path.GetFileName(directory), time.GetUtcNow());
            return (registry with { Accounts = [.. registry.Accounts, added] }, added);
        });
}
```

- [ ] **Step 4: Run the Core tests** — expected 2 passed.

- [ ] **Step 5: Write the failing CLI tests**

`PluginCommandAccountsTests.cs` — build `PluginCommand` the way `PluginCommandClaudeTests` does (copy its environment helper: `PluginEnvironment` with `StringWriter` stdout/stderr, `Harnesses = TestHarnesses.Under(Home)`, `Binaries = TestBinaries.None`, `ResolvePluginPath = () => pluginDir`, `ResolveMcpBinaryPath = () => "/usr/local/bin/kcap"`), plus `Accounts = new AccountStore(Tmp.PathTo("accounts"))`.

```csharp
    [Test]
    public async Task User_install_wires_every_registered_claude_account() {
        var env = Env();
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);
        Directory.CreateDirectory(Home.PathTo(".claude-work"));

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsTrue();
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task Install_on_an_empty_registry_adopts_the_default_directory() {
        var env = Env();

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Claude).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_of_a_pre_registry_install_adopts_and_refreshes() {
        var env = Env();
        ClaudePluginWriter.Install(Home.PathTo(".claude", "settings.json"), PluginDir); // installed by an older kcap
        File.WriteAllText(Home.PathTo(".claude", ClaudePluginInstaller.MarkerFileName), "0.0.1");

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Claude).Count).IsEqualTo(1);
        await Assert.That(ClaudePluginInstaller.ReadMarker(Home.PathTo(".claude", "settings.json"))).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task Refresh_does_not_adopt_a_vendor_kcap_was_never_installed_into() {
        var env = Env();

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Claude).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Project_remove_leaves_registered_accounts_wired() {
        var env = Env();
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove", "--project"]);

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task User_remove_continues_past_a_deleted_account_directory() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".claude-gone"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-gone"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);
        Directory.Delete(Home.PathTo(".claude-gone"), recursive: true);

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task Codex_user_install_wires_every_registered_home() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".codex-b"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Codex, Home.PathTo(".codex-b"), TimeProvider.System);

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--codex", "--skip-codex-network-access"]);

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex", "hooks.json"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsTrue();
    }
```

- [ ] **Step 6: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/PluginCommandAccountsTests/*"`
Expected: build error (`Accounts` not on `PluginEnvironment`), then failures once it compiles.

- [ ] **Step 7: Implement in `PluginEnvironment` and DI**

In `PluginEnvironment`:

```csharp
    /// <summary>The account registry user-scope wiring iterates. Null wires the one environment-derived
    /// layout per vendor, as every test that predates accounts expects.</summary>
    public AccountStore? Accounts { get; init; }
```

Change `FromProcess` to take it:

```csharp
    public static PluginEnvironment FromProcess(
            ProfileConfig profiles, UserHome home, HarnessRegistry harnesses, BinaryProbe binaries, AccountStore accounts) => new(
        Home:              home,
        Profiles:          profiles,
        ResolvePluginPath: () => SetupCommand.ResolvePluginPath(),
        Stdout:            Console.Out,
        Stderr:            Console.Error
    ) { Harnesses = harnesses, Binaries = binaries, Accounts = accounts };
```

In `CommandServices.cs`, register `services.AddSingleton(sp => AccountStore.Beside(sp.GetRequiredService<DaemonStore>()));` next to the other singletons and pass `sp.GetRequiredService<AccountStore>()` to `FromProcess`. In `UninstallCommand.cs:132`, pass `AccountStore.Beside(store)` (`store` is the `DaemonStore` already in scope there; if not, add `AccountStore accounts` to the command's constructor and pass it).

- [ ] **Step 8: Implement the user-scope iteration in `PluginCommand`**

Add helpers:

```csharp
    IReadOnlyList<VendorAccount> UserAccounts(HarnessId vendor, bool adoptDefault) {
        if (env.Accounts is null) return [];
        var envDir = vendor is HarnessId.Claude
            ? env.Harnesses.Of<ClaudeHarness>().Paths.Home
            : env.Harnesses.Of<CodexHarness>().Paths.Home;
        if (adoptDefault) AccountAdoption.EnsureDefault(env.Accounts, vendor, envDir, TimeProvider.System);
        return AccountAdoption.Of(env.Accounts, vendor);
    }

    WiringOptions Wiring(bool enableNetwork) => new(
        env.ResolvePluginPath(), env.Agents.UserSkillsDir, env.ResolveMcpBinaryPath,
        enableNetwork ? CodexConfigToml.BuildAllowDomains(env.Profiles.AllServerUrls()) : null);

    async Task<bool> ReportAsync(VendorAccount account, IReadOnlyList<WiringStep> steps, string verb) {
        var ok = AccountWiring.Succeeded(steps);
        var detail = string.Join(", ", steps.Where(s => !s.Succeeded).Select(s => $"{s.Name}: {s.Detail}"));
        if (ok) await env.Stdout.WriteLineAsync($"{verb} {account.Vendor} account {account.Label} ({account.Directory})");
        else await env.Stderr.WriteLineAsync($"Could not {verb.ToLowerInvariant()} {account.Vendor} account {account.Label} ({account.Directory}): {detail}");
        return ok;
    }
```

`env.Profiles.AllServerUrls()` stands for however `EnableCodexNetworkAccessAsync` (PluginCommand.cs:474) collects every profile's server URL today — read that method and reuse its exact expression; do not invent a new accessor.

In `InstallClaude`, after the existing single-layout install succeeds for user scope (not `--project`):

```csharp
        var failed = false;
        if (scope == "user" && env.Accounts is not null) {
            var refreshOnlyAdopt = !refreshOnly || ClaudePluginInstaller.IsInstalled(settingsPath);
            foreach (var account in UserAccounts(HarnessId.Claude, adoptDefault: refreshOnlyAdopt)) {
                if (AccountDirectory.Same(account.Directory, env.Harnesses.Of<ClaudeHarness>().Paths.Home)) continue;
                var accountSettings = AccountLayouts.Claude(env.Home, account.Directory).UserSettings;
                if (refreshOnly && ClaudePluginInstaller.ReadMarker(accountSettings) == CapacitorVersion.Current()) continue;
                if (!await ReportAsync(account, AccountWiring.Wire(account, env.Home, Wiring(enableNetwork: false)), "Wired") && !refreshOnly)
                    failed = true;
            }
        }
```

and return `failed ? 1 : 0` at the end. Place the adoption before the early `return 0` paths of `--if-installed` so a pre-registry install whose marker is current still gets adopted: move the `switch (refreshOnly)` early return so it only skips the default layout's rewrite, then continues to the account loop. Never fail the npm refresh path (`refreshOnly` ignores failures).

In `RemoveClaude` (user scope), after the existing default removal, loop `UserAccounts(HarnessId.Claude, adoptDefault: false)` skipping the default directory, call `AccountWiring.Unwire`, report each, and do not change the exit code for an account whose directory no longer exists (`Unwire` already returns success there).

Apply the same pattern to `InstallCodex` and `RemoveCodex` with `HarnessId.Codex`, `AccountLayouts.Codex(...).UserHooksJson`, `CodexHooksInstaller.ReadMarker`, and `Wiring(enableNetwork: !args.Contains("--skip-codex-network-access") && !refreshOnly)`. In the `--if-installed` Codex path, adopt only when `CodexHooksInstaller.IsInstalled(hooksPath)`.

- [ ] **Step 9: Run the tests**

Run: the CLI filter from Step 6 → 7 passed. Then `"/*/*/PluginCommand*/*"`, `"/*/*/UninstallCommandTests/*"` and `"/*/*/CommandContainerTests/*"` → all pass.

- [ ] **Step 10: Commit**

```bash
git add src/Capacitor.Cli.Core/Accounts/AccountAdoption.cs src/Capacitor.Cli/Commands test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountAdoptionTests.cs test/Capacitor.Cli.Tests.Unit/Commands/PluginCommandAccountsTests.cs
git commit -m "Wire every registered account from kcap plugin"
```

---

### Task 9: Attribute plan capture and Codex titles to the session's account

**Files:**
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs:671`, `:792` and its constructor (add `AccountStore accounts`)
- Modify: `src/Capacitor.Cli/Harness/Titles/HarnessTitleStores.cs:15-19` (add an `AccountRegistry?` parameter and a `UserHome`)
- Modify: `src/Capacitor.Cli/Commands/WatchCommand.cs:379` (pass them)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookPlanAccountTests.cs`, extend `test/Capacitor.Cli.Tests.Unit/Harness/Titles/HarnessTitleStoresTests.cs`

**Interfaces:**
- Consumes: `AccountStore.Load()` (Task 5), `AccountPaths.ClaudeForTranscript/CodexForRollout` (Task 6).
- Produces:
  - `ClaudeHookCommand` constructor gains a trailing `AccountStore accounts` parameter (DI supplies it; tests pass `new AccountStore(Tmp.PathTo("accounts"))`).
  - `HarnessTitleStores.For(string vendor, string? agentId, string sessionId, string transcriptPath, HarnessRegistry harnesses, AccountRegistry? accounts = null, UserHome? home = null)`.

- [ ] **Step 1: Write the failing tests**

`HarnessTitleStoresTests.cs` — add:

```csharp
    [Test]
    public async Task Codex_reads_the_index_of_the_account_that_wrote_the_rollout() {
        Home.CreateFile(".codex-b/session_index.jsonl", """{"id":"s1","thread_name":"From B"}""" + "\n");
        var rollout  = Home.CreateFile(".codex-b/sessions/2026/10/08/rollout-s1.jsonl", "");
        var registry = new AccountRegistry {
            Accounts = [new VendorAccount("b", HarnessId.Codex, AccountDirectory.Normalize(Home.PathTo(".codex-b")), "b", DateTimeOffset.UnixEpoch)]
        };

        var store = HarnessTitleStores.For("codex", null, "s1", rollout, TestHarnesses.Under(Home), registry, Home);

        await Assert.That(await store!.ReadAsync(CancellationToken.None)).IsEqualTo("From B");
    }
```

Match the `session_index.jsonl` line shape and the read method name to the existing `Codex_reads_the_session_index` test in the same file.

`ClaudeHookPlanAccountTests.cs` — construct `ClaudeHookCommand` exactly as `ClaudeHookCommandTests` does (constructor shown in the survey: `new ClaudeHookCommand(Config.Root, …, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), HostedAgent.Terminal, new FixedCapacitorHttpClient(), TestWatchers.For(...), FakeProcessStarter.Refusing(), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: store)`) and feed a `session-start` payload whose `transcript_path` is `<home>/.claude-work/projects/-repo/s1.jsonl` and whose `slug` is `my-plan`, with `<home>/.claude-work/plans/my-plan.md` containing `"# Plan B"` and no `~/.claude/plans/my-plan.md`. Assert that the request body the fake HTTP client captured for the session-start post contains `"plan_content":"# Plan B"`. Copy the HTTP capture pattern from the nearest existing session-start test in `ClaudeHookCommandTests.cs` (e.g. `session_start_includes_workspace_root_when_cwd_is_inside_a_git_repo`).

- [ ] **Step 2: Run to verify failure**

Run: `--treenode-filter "/*/*/HarnessTitleStoresTests/*"` and `"/*/*/ClaudeHookPlanAccountTests/*"` in the Cli suite. Expected: build errors.

- [ ] **Step 3: Implement**

`HarnessTitleStores.For`:

```csharp
    public static IHarnessTitleStore? For(
            string vendor, string? agentId, string sessionId, string transcriptPath, HarnessRegistry harnesses,
            AccountRegistry? accounts = null, UserHome? home = null) {
        if (agentId is not null) return null;

        return vendor switch {
            "codex"       => new CodexSessionIndexTitle(CodexHome(transcriptPath, harnesses, accounts, home), sessionId),
            // other arms unchanged
        };
    }

    static string CodexHome(string rolloutPath, HarnessRegistry harnesses, AccountRegistry? accounts, UserHome? home) =>
        accounts is not null && home is not null && AccountPaths.CodexForRollout(rolloutPath, accounts, home) is { } paths
            ? paths.Home
            : harnesses.Of<CodexHarness>().Paths.Home;
```

`WatchCommand.cs:379`: add `AccountStore accounts` and `UserHome home` to the constructor if not present (check the constructor first; `UserHome` may already be there), then:

```csharp
var titleStore = HarnessTitleStores.For(vendor, agentId, sessionId, transcriptPath, harnesses, SafeLoad(accounts), home);
```

with, in `WatchCommand`:

```csharp
    static AccountRegistry? SafeLoad(AccountStore accounts) {
        try { return accounts.Load(); } catch (InvalidDataException) { return null; } catch (IOException) { return null; }
    }
```

`ClaudeHookCommand`: add the `AccountStore accounts` constructor parameter, the same `SafeLoad` helper, and replace both plan reads:

```csharp
var planContent = ReadPlanFile(slug, ClaudeLayoutFor(transcriptPath));
...
var planContent = ReadPlanFile(resolvedSlug, ClaudeLayoutFor(transcriptPath));
```

```csharp
    ClaudePaths ClaudeLayoutFor(string? transcriptPath) =>
        transcriptPath is not null && SafeLoad(accounts) is { } registry
     && AccountPaths.ClaudeForTranscript(transcriptPath, registry, home) is { } paths
            ? paths
            : harnesses.Of<ClaudeHarness>().Paths;
```

Register nothing new in DI beyond Task 8's `AccountStore` singleton; existing tests that build `ClaudeHookCommand` / `WatchCommand` directly must now pass an `AccountStore` — add `new AccountStore(Tmp.PathTo("accounts"))` (or a `TempDir`-backed equivalent) at each construction site the compiler reports.

- [ ] **Step 4: Run the tests**

Expected: the new tests pass; then `"/*/*/ClaudeHookCommand*/*"`, `"/*/*/WatchCommand*/*"`, `"/*/*/HarnessTitleStoresTests/*"` all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli test/Capacitor.Cli.Tests.Unit
git commit -m "Read plans and Codex titles from the session's own account"
```

---

### Task 10: Import from every registered account

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs:1560-1568` (`BuildImportSources` gains `AccountRegistry? accounts = null, UserHome? home = null`)
- Modify: `src/Capacitor.Cli/Commands/ImportCommand.cs:28`, `:790`, `:3505` (per-home Codex titles)
- Modify: callers of `BuildImportSources` — `SetupCommand.cs:261, :314`, `SetupImportRunner.cs:22, :45`, `Program.cs:721` (pass the loaded registry and home)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ImportAccountsTests.cs`

**Interfaces:**
- Consumes: `AccountStore`, `AccountLayouts`, `AccountPaths` (Tasks 5–6); `ClaudeImportSource(ConfigRoot, string projectsDir, GitProviderRouter, TimeProvider)`, `CodexImportSource(ConfigRoot, string sessionsDir, GitProviderRouter, TimeProvider)`, `CodexImportTitles(string codexHome, TimeProvider)`.
- Produces: `BuildImportSources(..., AccountRegistry? accounts = null, UserHome? home = null)` returns one Claude source per distinct Claude projects dir and one Codex source per distinct Codex sessions dir (environment layout plus every registered account, de-duplicated by `AccountDirectory.Same`). `ImportCommand` keeps a `Dictionary<string, CodexImportTitles>` keyed by Codex home.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Test]
    public async Task Import_sources_cover_every_registered_account_once() {
        var registry = new AccountRegistry {
            Accounts = [
                new("a", HarnessId.Claude, AccountDirectory.Normalize(Home.PathTo(".claude")), "a", DateTimeOffset.UnixEpoch),
                new("b", HarnessId.Claude, AccountDirectory.Normalize(Directory.CreateDirectory(Home.PathTo(".claude-work")).FullName), "b", DateTimeOffset.UnixEpoch),
                new("c", HarnessId.Codex,  AccountDirectory.Normalize(Directory.CreateDirectory(Home.PathTo(".codex-b")).FullName), "c", DateTimeOffset.UnixEpoch),
            ]
        };

        var sources = SetupCommand.BuildImportSources(Config.Root, TestHarnesses.Under(Home), new GitProviderRouter(), TimeProvider.System,
                                                      vendors: [HarnessId.Claude, HarnessId.Codex], accounts: registry, home: Home);

        await Assert.That(sources.OfType<ClaudeImportSource>().Count()).IsEqualTo(2);
        await Assert.That(sources.OfType<CodexImportSource>().Count()).IsEqualTo(2);
    }
```

Add a second test modelled on `ImportChainsTests.ImportChainsAsync_posts_codex_index_title_and_skips_title_generation` (:413) that writes the session index under `.codex-b`, the rollout under `.codex-b/sessions/...`, and asserts the posted title comes from `.codex-b`'s index. Reuse that test's server fake and assertion helpers verbatim.

- [ ] **Step 2: Run to verify failure** — `--treenode-filter "/*/*/ImportAccountsTests/*"`. Expected: build error (no `accounts` parameter).

- [ ] **Step 3: Implement `BuildImportSources`**

```csharp
    internal static IReadOnlyList<IImportSource> BuildImportSources(
            ConfigRoot config, HarnessRegistry harnesses, GitProviderRouter router, TimeProvider time,
            IReadOnlyCollection<HarnessId>? vendors = null, AccountRegistry? accounts = null, UserHome? home = null) {
        var cursor   = harnesses.Of<CursorHarness>().Paths;
        var opencode = harnesses.Of<OpenCodeHarness>().Paths;

        var claudeRoots = DistinctRoots(harnesses.Of<ClaudeHarness>().Paths.Projects, accounts, home, HarnessId.Claude,
                                        dir => AccountLayouts.Claude(home!, dir).Projects);
        var codexRoots  = DistinctRoots(harnesses.Of<CodexHarness>().Paths.Sessions, accounts, home, HarnessId.Codex,
                                        dir => AccountLayouts.Codex(home!, dir).Sessions);

        IReadOnlyList<IImportSource> all = [
            .. claudeRoots.Select(r => (IImportSource)new ClaudeImportSource(config, r, router, time)),
            .. codexRoots.Select(r => (IImportSource)new CodexImportSource(config, r, router, time)),
            // the remaining vendors' sources exactly as today
        ];
        // existing vendor filter unchanged
    }

    static IReadOnlyList<string> DistinctRoots(
            string environmentRoot, AccountRegistry? accounts, UserHome? home, HarnessId vendor, Func<string, string> rootOf) {
        var roots = new List<string> { environmentRoot };
        if (accounts is not null && home is not null)
            foreach (var a in accounts.Accounts.Where(a => a.Vendor == vendor))
                if (!roots.Any(r => AccountDirectory.Same(r, rootOf(a.Directory)))) roots.Add(rootOf(a.Directory));
        return roots;
    }
```

Keep the existing vendor filter: it must still filter by the source's vendor, so if it currently works by list position, change it to filter by each source's own `HarnessId` property (read `IImportSource` at `IImportSource.cs:112` for the member name).

- [ ] **Step 4: Per-home Codex titles in `ImportCommand`**

Replace the field at :28 with:

```csharp
    readonly Dictionary<string, CodexImportTitles> _codexTitles = new(StringComparer.Ordinal);

    CodexImportTitles CodexTitlesFor(string? transcriptPath) {
        var codexHome = transcriptPath is not null && SafeLoad() is { } registry
                     && AccountPaths.CodexForRollout(transcriptPath, registry, home) is { } paths
            ? paths.Home
            : harnesses.Of<CodexHarness>().Paths.Home;
        if (!_codexTitles.TryGetValue(codexHome, out var titles)) _codexTitles[codexHome] = titles = new CodexImportTitles(codexHome, time);
        return titles;
    }
```

Add `AccountStore accounts` to `ImportCommand`'s constructor, a `SafeLoad()` like Task 9's, and in `PostCodexHarnessTitleAsync` use `CodexTitlesFor(session.TranscriptPath).PostAsync(...)` — use the actual transcript-path member of `SessionClassification` (read its definition). At :790 pass `accounts: SafeLoad(), home: home` when building default sources, and update the other `BuildImportSources` callers listed above to pass the registry and home they have (each runs where an `AccountStore` can be resolved from DI or built with `AccountStore.Beside`).

- [ ] **Step 5: Run the tests**

Expected: new tests pass; `"/*/*/Import*/*"` and `"/*/*/Setup*/*"` all pass; `Codex_index_is_read_once_per_import` still passes (one read per home).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli test/Capacitor.Cli.Tests.Unit
git commit -m "Import sessions from every registered account"
```

---

### Task 11: Account discovery

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/AccountCandidate.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/AccountDiscovery.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountDiscoveryTests.cs`

**Interfaces:**
- Consumes: `AccountRegistry`, `AccountDirectory` (Task 5).
- Produces:
  - `public sealed record AccountCandidate(HarnessId Vendor, string Directory, string Reason)`
  - `public static IReadOnlyList<AccountCandidate> AccountDiscovery.Find(UserHome home, AccountRegistry registry, Func<string, string?> env)` — candidates not already registered, de-duplicated, ordered Claude first then by path. Sources:
    - `~/.claude*` dirs containing `settings.json` or `projects/`;
    - `~/.codex*` dirs containing `config.toml` or `sessions/`;
    - claude-swap profiles: `<base>/sessions/*` that look like Claude dirs, where `<base>` is `$XDG_DATA_HOME/claude-swap` (only if `XDG_DATA_HOME` is an absolute path) or `~/.local/share/claude-swap` on Linux, and `~/.claude-swap-backup` on macOS and Windows;
    - `CLAUDE_CONFIG_DIR` / `CODEX_HOME` from `env` when they exist.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountDiscoveryTests {
    [TempHome] public required TempHome Home { get; init; }

    static string? NoEnv(string _) => null;

    [Test]
    public async Task Finds_claude_and_codex_lookalikes_with_vendor_files() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        Home.CreateFile(".codex-b/config.toml", "");
        Directory.CreateDirectory(Home.PathTo(".claude-empty"));

        var found = AccountDiscovery.Find(Home, new AccountRegistry(), NoEnv);

        await Assert.That(found.Select(c => (c.Vendor, Path.GetFileName(c.Directory))))
            .IsEquivalentTo(new[] { (HarnessId.Claude, ".claude-work"), (HarnessId.Codex, ".codex-b") });
    }

    [Test]
    public async Task Skips_registered_directories() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        var registry = new AccountRegistry {
            Accounts = [new("x", HarnessId.Claude, AccountDirectory.Normalize(Home.PathTo(".claude-work")), "x", DateTimeOffset.UnixEpoch)]
        };

        await Assert.That(AccountDiscovery.Find(Home, registry, NoEnv).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Offers_the_environment_override() {
        var dir = Path.GetDirectoryName(Home.CreateFile("custom/claude/settings.json", "{}"))!;

        var found = AccountDiscovery.Find(Home, new AccountRegistry(), k => k == "CLAUDE_CONFIG_DIR" ? dir : null);

        await Assert.That(found.Single().Directory).IsEqualTo(AccountDirectory.Normalize(dir));
    }

    [Test]
    public async Task Finds_claude_swap_profiles() {
        var swapBase = OperatingSystem.IsLinux() ? ".local/share/claude-swap" : ".claude-swap-backup";
        Home.CreateFile($"{swapBase}/sessions/2-user_x.com/settings.json", "{}");

        var found = AccountDiscovery.Find(Home, new AccountRegistry(), NoEnv);

        await Assert.That(found.Any(c => c.Directory.EndsWith("2-user_x.com", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Never_reads_vendor_file_contents() {
        Home.CreateFile(".claude-work/settings.json", "{ not even json");

        await Assert.That(AccountDiscovery.Find(Home, new AccountRegistry(), NoEnv).Count).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `--treenode-filter "/*/*/AccountDiscoveryTests/*"`. Expected: build error.

- [ ] **Step 3: Implement**

`AccountCandidate.cs`:

```csharp
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

public sealed record AccountCandidate(HarnessId Vendor, string Directory, string Reason);
```

`AccountDiscovery.cs`:

```csharp
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>Directories that look like a vendor's config dir. Presence of the vendor's own files
/// only — contents are never opened, since some of them hold credentials.</summary>
public static class AccountDiscovery {
    public static IReadOnlyList<AccountCandidate> Find(UserHome home, AccountRegistry registry, Func<string, string?> env) {
        var found = new List<AccountCandidate>();

        foreach (var dir in Children(home.Path, ".claude")) if (LooksLikeClaude(dir)) found.Add(new(HarnessId.Claude, dir, "home directory"));
        foreach (var dir in Children(home.Path, ".codex"))  if (LooksLikeCodex(dir))  found.Add(new(HarnessId.Codex, dir, "home directory"));

        if (ClaudeSwapBase(home, env) is { } swap && Directory.Exists(Path.Combine(swap, "sessions")))
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(swap, "sessions")))
                if (LooksLikeClaude(dir)) found.Add(new(HarnessId.Claude, dir, "claude-swap profile"));

        if (env("CLAUDE_CONFIG_DIR") is { Length: > 0 } c && LooksLikeClaude(c)) found.Add(new(HarnessId.Claude, c, "CLAUDE_CONFIG_DIR"));
        if (env("CODEX_HOME") is { Length: > 0 } x && LooksLikeCodex(x))         found.Add(new(HarnessId.Codex, x, "CODEX_HOME"));

        var result = new List<AccountCandidate>();
        foreach (var candidate in found) {
            var normalized = candidate with { Directory = AccountDirectory.Normalize(candidate.Directory) };
            if (registry.Accounts.Any(a => a.Vendor == normalized.Vendor && AccountDirectory.Same(a.Directory, normalized.Directory))) continue;
            if (result.Any(r => r.Vendor == normalized.Vendor && AccountDirectory.Same(r.Directory, normalized.Directory))) continue;
            result.Add(normalized);
        }

        return [.. result.OrderBy(c => c.Vendor).ThenBy(c => c.Directory, StringComparer.Ordinal)];
    }

    static IEnumerable<string> Children(string root, string prefix) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, prefix + "*", SearchOption.TopDirectoryOnly)
            : [];

    static bool LooksLikeClaude(string dir) =>
        File.Exists(Path.Combine(dir, "settings.json")) || Directory.Exists(Path.Combine(dir, "projects"));

    static bool LooksLikeCodex(string dir) =>
        File.Exists(Path.Combine(dir, "config.toml")) || Directory.Exists(Path.Combine(dir, "sessions"));

    static string? ClaudeSwapBase(UserHome home, Func<string, string?> env) {
        if (!OperatingSystem.IsLinux()) return Path.Combine(home.Path, ".claude-swap-backup");
        return env("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
            ? Path.Combine(xdg, "claude-swap")
            : Path.Combine(home.Path, ".local", "share", "claude-swap");
    }
}
```

- [ ] **Step 4: Run the tests** — expected 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Accounts/AccountCandidate.cs src/Capacitor.Cli.Core/Accounts/AccountDiscovery.cs test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountDiscoveryTests.cs
git commit -m "Discover vendor account directories in the home folder"
```

---

### Task 12: `kcap accounts` command, help and README

**Files:**
- Create: `src/Capacitor.Cli/Commands/AccountsCommand.cs`
- Create: `src/Capacitor.Cli.Core/Resources/help-accounts.txt`
- Modify: `src/Capacitor.Cli.Core/Resources/help-usage.txt` (add the command near `profile`/`machine`, lines 13–20)
- Modify: `src/Capacitor.Cli/Program.cs` (`case "accounts":` in the dispatch switch; add `"accounts"` to `offlineCommands` at :234)
- Modify: `src/Capacitor.Cli/Commands/CommandServices.cs` (`services.AddTransient<AccountsCommand>();`)
- Modify: `README.md` (row in the "At a glance" table near :346 and a `### Accounts` section near `### Profiles` at :2122)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/AccountsCommandTests.cs`

**Interfaces:**
- Consumes: Tasks 5–8, 11. `PluginEnvironment` (for `WiringOptions` inputs: plugin dir, agents skills dir, MCP binary).
- Produces: `public sealed class AccountsCommand(AccountStore accounts, PluginEnvironment env, TimeProvider time)` with `public Task<int> HandleAsync(string[] args)`; subcommands:
  - `kcap accounts` / `kcap accounts list` — vendor, label, directory, recording state, one per line; then "Found but not added:" with discovery candidates and the add command.
  - `kcap accounts add <claude|codex> <dir>` — normalize, refuse a missing directory, register (idempotent), wire, print the steps; exit 1 if wiring failed (the account stays registered so `rewire` can finish it).
  - `kcap accounts remove <id|dir>` — unwire then unregister; exit 1 and keep the entry if unwiring failed.
  - `kcap accounts rename <id|dir> <label>`.
  - `kcap accounts rewire [<id|dir>]` — wire one or all.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class AccountsCommandTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    AccountStore Store => new(Tmp.PathTo("accounts"));

    AccountsCommand Sut() => new(Store, TestPluginEnvironment.For(Home, pluginDir: PluginDir()), TimeProvider.System);

    string PluginDir() {
        var dir = Directory.CreateDirectory(Home.PathTo("plugin")).FullName;
        foreach (var n in AgentsSkillsInstaller.SourceNames) Directory.CreateDirectory(Path.Combine(dir, "skills", n));
        return dir;
    }

    [Test]
    public async Task Add_registers_and_wires_a_claude_account() {
        var dir = Directory.CreateDirectory(Home.PathTo(".claude-work")).FullName;
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Store.Find(HarnessId.Claude, dir)).IsNotNull();
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Path.Combine(dir, "settings.json"))).IsTrue();
    }

    [Test]
    public async Task Add_twice_keeps_one_entry() {
        var dir = Directory.CreateDirectory(Home.PathTo(".claude-work")).FullName;
        using var capture = ConsoleOutput.StartCapture();

        await Sut().HandleAsync(["accounts", "add", "claude", dir]);
        await Sut().HandleAsync(["accounts", "add", "claude", dir + "/"]);

        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Add_refuses_a_missing_directory() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Sut().HandleAsync(["accounts", "add", "claude", Home.PathTo("nope")]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Remove_unwires_and_forgets() {
        var dir = Directory.CreateDirectory(Home.PathTo(".claude-work")).FullName;
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        var exit = await Sut().HandleAsync(["accounts", "remove", dir]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Path.Combine(dir, "settings.json"))).IsFalse();
        await Assert.That(Directory.Exists(dir)).IsTrue();
    }

    [Test]
    public async Task List_shows_state_and_undiscovered_candidates() {
        var dir = Directory.CreateDirectory(Home.PathTo(".claude-work")).FullName;
        Home.CreateFile(".codex-b/config.toml", "");
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        await Sut().HandleAsync(["accounts", "list"]);

        var output = capture.GetCapturedOutput();
        await Assert.That(output).Contains(".claude-work");
        await Assert.That(output).Contains("kcap accounts add codex");
    }

    [Test]
    public async Task Rename_changes_the_label() {
        var dir = Directory.CreateDirectory(Home.PathTo(".claude-work")).FullName;
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        await Sut().HandleAsync(["accounts", "rename", dir, "Work"]);

        await Assert.That(Store.Load().Accounts.Single().Label).IsEqualTo("Work");
    }
}
```

`TestPluginEnvironment.For(UserHome, string pluginDir)`: if the Cli test project already has a `PluginEnvironment` builder used by `PluginCommandClaudeTests`, use it and add the `pluginDir` argument; otherwise create it in that test project (not Helpers — it references a Cli type) with `ResolveMcpBinaryPath = () => "/usr/local/bin/kcap"`, `Harnesses = TestHarnesses.Under(home)`, `Binaries = TestBinaries.None`, stdout/stderr `Console.Out`/`Console.Error`.

- [ ] **Step 2: Run to verify failure** — `--treenode-filter "/*/*/AccountsCommandTests/*"` in the Cli suite. Expected: build error.

- [ ] **Step 3: Implement**

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

public sealed class AccountsCommand(AccountStore accounts, PluginEnvironment env, TimeProvider time) {
    public async Task<int> HandleAsync(string[] args) =>
        args.Length < 2 ? await List() : args[1] switch {
            "list"                              => await List(),
            "add" when args.Length == 4         => await Add(args[2], args[3]),
            "remove" when args.Length == 3      => await Remove(args[2]),
            "rename" when args.Length == 4      => await Rename(args[2], args[3]),
            "rewire"                            => await Rewire(args.Length >= 3 ? args[2] : null),
            _                                   => await Usage(),
        };

    WiringOptions Options => new(env.ResolvePluginPath(), env.Agents.UserSkillsDir, env.ResolveMcpBinaryPath, NetworkAllowDomains: null);

    async Task<int> List() {
        foreach (var a in accounts.Load().Accounts)
            await Console.Out.WriteLineAsync($"{a.Vendor,-7} {a.Label,-20} {StateLabel(AccountWiring.State(a, env.Home)),-22} {a.Directory}  [{a.Id[..8]}]");

        var candidates = AccountDiscovery.Find(env.Home, accounts.Load(), Environment.GetEnvironmentVariable);
        if (candidates.Count > 0) {
            await Console.Out.WriteLineAsync("");
            await Console.Out.WriteLineAsync("Found but not added:");
            foreach (var c in candidates)
                await Console.Out.WriteLineAsync($"  kcap accounts add {c.Vendor.ToString().ToLowerInvariant()} {c.Directory}   ({c.Reason})");
        }
        return 0;
    }

    async Task<int> Add(string vendorName, string directory) {
        if (!TryVendor(vendorName, out var vendor)) return await Usage();
        if (!Directory.Exists(directory)) {
            await Console.Error.WriteLineAsync($"{directory} does not exist.");
            return 1;
        }

        var account = AccountAdoption.EnsureDefault(accounts, vendor, directory, time);
        return await WireAndReport(account) ? 0 : 1;
    }

    async Task<int> Remove(string idOrDir) {
        if (Resolve(idOrDir) is not { } account) return await NotFound(idOrDir);

        var steps = AccountWiring.Unwire(account, env.Home);
        if (!AccountWiring.Succeeded(steps)) {
            await Console.Error.WriteLineAsync($"Could not unwire {account.Label}: {Failures(steps)}. It stays registered.");
            return 1;
        }

        accounts.Mutate(r => (r with { Accounts = [.. r.Accounts.Where(a => a.Id != account.Id)] }, 0));
        await Console.Out.WriteLineAsync($"Removed {account.Vendor} account {account.Label}. {account.Directory} was left in place.");
        return 0;
    }

    async Task<int> Rename(string idOrDir, string label) {
        if (Resolve(idOrDir) is not { } account) return await NotFound(idOrDir);
        accounts.Mutate(r => (r with { Accounts = [.. r.Accounts.Select(a => a.Id == account.Id ? a with { Label = label } : a)] }, 0));
        await Console.Out.WriteLineAsync($"Renamed to {label}.");
        return 0;
    }

    async Task<int> Rewire(string? idOrDir) {
        var targets = idOrDir is null ? accounts.Load().Accounts : Resolve(idOrDir) is { } one ? [one] : [];
        if (idOrDir is not null && targets.Count == 0) return await NotFound(idOrDir);

        var ok = true;
        foreach (var account in targets) ok &= await WireAndReport(account);
        return ok ? 0 : 1;
    }

    async Task<bool> WireAndReport(VendorAccount account) {
        IReadOnlyList<WiringStep> steps;
        using (accounts.Lock()) steps = AccountWiring.Wire(account, env.Home, Options);

        if (AccountWiring.Succeeded(steps)) {
            await Console.Out.WriteLineAsync($"Wired {account.Vendor} account {account.Label} ({account.Directory}).");
            if (account.Vendor is HarnessId.Codex)
                await Console.Out.WriteLineAsync($"  Trust the kcap hooks the next time you start Codex with CODEX_HOME={account.Directory}.");
            return true;
        }

        await Console.Error.WriteLineAsync($"Could not wire {account.Label}: {Failures(steps)}");
        return false;
    }

    VendorAccount? Resolve(string idOrDir) =>
        accounts.Load().Accounts.FirstOrDefault(a => a.Id.StartsWith(idOrDir, StringComparison.Ordinal))
     ?? accounts.Load().Accounts.FirstOrDefault(a => Directory.Exists(idOrDir) || Path.IsPathRooted(idOrDir) ? AccountDirectory.Same(a.Directory, idOrDir) : false);

    static bool TryVendor(string name, out HarnessId vendor) {
        vendor = name.ToLowerInvariant() switch { "claude" => HarnessId.Claude, "codex" => HarnessId.Codex, _ => default };
        return name.ToLowerInvariant() is "claude" or "codex";
    }

    static string StateLabel(RecordingState state) => state switch {
        RecordingState.Recording => "recording",
        RecordingState.Installed => "hooks installed",
        RecordingState.Broken    => "broken — run rewire",
        _                        => "not wired",
    };

    static string Failures(IReadOnlyList<WiringStep> steps) =>
        string.Join(", ", steps.Where(s => !s.Succeeded).Select(s => $"{s.Name}: {s.Detail}"));

    static async Task<int> NotFound(string idOrDir) {
        await Console.Error.WriteLineAsync($"No account matches {idOrDir}. Run `kcap accounts` to list them.");
        return 1;
    }

    static async Task<int> Usage() {
        await Console.Error.WriteLineAsync(EmbeddedResources.TryLoad("help-accounts.txt") ?? "Usage: kcap accounts [list|add|remove|rename|rewire]");
        return 1;
    }
}
```

Note: `AccountWiring.Wire` is called while holding `accounts.Lock()`, and `AccountAdoption.EnsureDefault` takes the same lock inside `Mutate` — call `EnsureDefault` before entering the `using (accounts.Lock())` block, as above. `Resolve` must not match a non-path argument against a directory; simplify the second lookup to `Path.IsPathRooted(idOrDir) || idOrDir.StartsWith('.') || idOrDir.StartsWith('~')` after expanding `~` with `env.Home.Path` if you prefer — keep the test `Remove_unwires_and_forgets` (absolute path) passing.

Wire the command:
- `Program.cs` dispatch switch: `case "accounts": return await Run<AccountsCommand>().HandleAsync(args);` beside `case "profile":`.
- `offlineCommands` (:234): add `"accounts"`.
- `CommandServices.cs`: `services.AddTransient<AccountsCommand>();`.

`help-accounts.txt` (same format as `help-profile.txt`):

```
Usage: kcap accounts [list|add|remove|rename|rewire]

Records every Claude Code config directory (CLAUDE_CONFIG_DIR) and Codex home (CODEX_HOME)
you use, not only the default one.

  kcap accounts                         List accounts, their recording state, and directories
                                        found on this machine that are not added yet
  kcap accounts add <claude|codex> <dir>
                                        Add a directory and install kcap's recording into it
  kcap accounts remove <id|dir>         Remove kcap's recording from an account and forget it
                                        (the directory and its login are left alone)
  kcap accounts rename <id|dir> <label> Change the label shown for an account
  kcap accounts rewire [<id|dir>]       Reinstall recording into one or every account
```

`help-usage.txt`: add a line `  accounts     Manage the Claude and Codex accounts kcap records` next to `profile`.

README: in the "At a glance" table add `| [\`kcap accounts\`](#accounts) | Record every Claude and Codex account, not only the default |`, and add a `### Accounts` section before `### Profiles` explaining multiple config directories, the four subcommands, that setup offers discovered directories, that Codex asks to trust the hooks per home, and that "account" is distinct from a Capacitor profile.

- [ ] **Step 4: Run the tests**

Expected: 6 passed; `"/*/*/CommandContainerTests/*"` passes.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/AccountsCommand.cs src/Capacitor.Cli/Program.cs src/Capacitor.Cli/Commands/CommandServices.cs src/Capacitor.Cli.Core/Resources/help-accounts.txt src/Capacitor.Cli.Core/Resources/help-usage.txt README.md test/Capacitor.Cli.Tests.Unit/Commands
git commit -m "Add kcap accounts to list, add and remove vendor accounts"
```

---

### Task 13: Setup adopts defaults and offers discovered accounts

**Files:**
- Create: `src/Capacitor.Cli/Commands/AccountSetupStep.cs`
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (run the step after `CodingAgentsStep` completes)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/AccountSetupStepTests.cs`

**Interfaces:**
- Consumes: Tasks 5–8, 11; `CodingAgentsStep.Options` (`SkipClaude`, `SkipCodex`, `NoPrompt`).
- Produces: `internal sealed class AccountSetupStep(AccountStore accounts, PluginEnvironment env, TimeProvider time)` with `internal async Task RunAsync(CodingAgentsStep.Options options, Func<string, bool> promptYesNo, Action<string> writeLine)`:
  1. For each of Claude/Codex not skipped and installed by setup this run (the env-derived layout reports wired), `AccountAdoption.EnsureDefault`.
  2. Wire every registered non-default account of a non-skipped vendor (`AccountWiring.Wire`), reporting each.
  3. `AccountDiscovery.Find` → for each candidate of a non-skipped vendor: with `NoPrompt`, print `Also found <dir> — add it with: kcap accounts add <vendor> <dir>`; otherwise ask `Record <vendor> sessions from <dir> too?` and on yes register and wire it.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Test]
    public async Task No_prompt_lists_candidates_without_adding_them() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        var lines = new List<string>();

        await Sut().RunAsync(Options(noPrompt: true), _ => throw new InvalidOperationException("must not prompt"), lines.Add);

        await Assert.That(lines.Any(l => l.Contains("kcap accounts add claude"))).IsTrue();
        await Assert.That(Store.Find(HarnessId.Claude, Home.PathTo(".claude-work"))).IsNull();
    }

    [Test]
    public async Task A_yes_adds_and_wires_the_candidate() {
        Home.CreateFile(".claude-work/settings.json", "{}");

        await Sut().RunAsync(Options(noPrompt: false), _ => true, _ => { });

        await Assert.That(Store.Find(HarnessId.Claude, Home.PathTo(".claude-work"))).IsNotNull();
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task A_skipped_vendor_is_neither_adopted_nor_offered() {
        Home.CreateFile(".codex-b/config.toml", "");
        var asked = new List<string>();

        await Sut().RunAsync(Options(noPrompt: false, skipCodex: true), q => { asked.Add(q); return true; }, _ => { });

        await Assert.That(asked.Any(q => q.Contains(".codex-b"))).IsFalse();
        await Assert.That(AccountAdoption.Of(Store, HarnessId.Codex).Count).IsEqualTo(0);
    }
```

`Options(...)` builds a `CodingAgentsStep.Options` with the given flags (read its constructor at `CodingAgentsStep.cs:19`; set the rest to defaults). `Sut()` uses the same `TestPluginEnvironment.For(Home, pluginDir)` as Task 12 and `new AccountStore(Tmp.PathTo("accounts"))`.

- [ ] **Step 2: Run to verify failure** — `--treenode-filter "/*/*/AccountSetupStepTests/*"`. Expected: build error.

- [ ] **Step 3: Implement `AccountSetupStep`**

```csharp
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Commands;

internal sealed class AccountSetupStep(AccountStore accounts, PluginEnvironment env, TimeProvider time) {
    internal async Task RunAsync(CodingAgentsStep.Options options, Func<string, bool> promptYesNo, Action<string> writeLine) {
        var vendors = new List<HarnessId>();
        if (!options.SkipClaude) vendors.Add(HarnessId.Claude);
        if (!options.SkipCodex)  vendors.Add(HarnessId.Codex);

        foreach (var vendor in vendors) {
            var envHome = vendor is HarnessId.Claude ? env.Harnesses.Of<ClaudeHarness>().Paths.Home : env.Harnesses.Of<CodexHarness>().Paths.Home;
            var wired   = vendor is HarnessId.Claude
                ? ClaudePluginInstaller.IsPluginEnabled(env.Harnesses.Of<ClaudeHarness>().Paths.UserSettings)
                : CodexHooksInstaller.ReferencesKcapHook(env.Harnesses.Of<CodexHarness>().Paths.UserHooksJson);
            if (wired) AccountAdoption.EnsureDefault(accounts, vendor, envHome, time);

            foreach (var account in AccountAdoption.Of(accounts, vendor).Where(a => !AccountDirectory.Same(a.Directory, envHome)))
                Report(account, Wire(account), writeLine);
        }

        foreach (var candidate in AccountDiscovery.Find(env.Home, accounts.Load(), Environment.GetEnvironmentVariable)
                                                   .Where(c => vendors.Contains(c.Vendor))) {
            var name = candidate.Vendor.ToString().ToLowerInvariant();
            if (options.NoPrompt) {
                writeLine($"  Also found {candidate.Directory} — add it with: kcap accounts add {name} {candidate.Directory}");
                continue;
            }
            if (!promptYesNo($"Record {candidate.Vendor} sessions from {candidate.Directory} too?")) continue;

            var account = AccountAdoption.EnsureDefault(accounts, candidate.Vendor, candidate.Directory, time);
            Report(account, Wire(account), writeLine);
        }

        await Task.CompletedTask;
    }

    IReadOnlyList<WiringStep> Wire(VendorAccount account) {
        using var _ = accounts.Lock();
        return AccountWiring.Wire(account, env.Home, new WiringOptions(env.ResolvePluginPath(), env.Agents.UserSkillsDir, env.ResolveMcpBinaryPath, null));
    }

    static void Report(VendorAccount account, IReadOnlyList<WiringStep> steps, Action<string> writeLine) =>
        writeLine(AccountWiring.Succeeded(steps)
            ? $"  [green]✓[/] {account.Vendor} account {account.Directory} recorded"
            : $"  [yellow]⚠[/] Could not wire {account.Directory}: {string.Join(", ", steps.Where(s => !s.Succeeded).Select(s => s.Detail))}");
}
```

(`writeLine` in setup takes Spectre markup, matching `CodingAgentsStep`'s output; escape directory text with `Markup.Escape` as `CodingAgentsStep` does.) If `RunAsync` has no awaits after review, make it synchronous and drop `Task`.

In `SetupCommand`, after `CodingAgentsStep` returns its results, construct `AccountSetupStep` with `AccountStore.Beside(daemonStore)` (or from DI if `SetupCommand` is DI-built — add `AccountStore accounts` to its constructor) and call `RunAsync(options, promptYesNo, writeLine)` with the same prompt and writer delegates `CodingAgentsStep` received.

- [ ] **Step 4: Run the tests** — 3 passed; `"/*/*/Setup*/*"` and `"/*/*/CodingAgentsStepTests/*"` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/AccountSetupStep.cs src/Capacitor.Cli/Commands/SetupCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/AccountSetupStepTests.cs
git commit -m "Offer discovered vendor accounts during setup"
```

---

### Task 14: Per-account status and uninstall cleanup

**Files:**
- Modify: `src/Capacitor.Cli/Commands/StatusCommand.cs:38-44`
- Modify: `src/Capacitor.Cli/Commands/UninstallCommand.cs` (delete the registry last)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/StatusCommandHooksTests.cs` (extend), `test/Capacitor.Cli.Tests.Unit/Commands/UninstallCommandTests.cs` (extend)

**Interfaces:**
- Consumes: `AccountStore`, `AccountWiring.State` (Tasks 5, 7).
- Produces: `internal static string StatusCommand.BuildAccountLines(IEnumerable<(VendorAccount Account, RecordingState State)> accounts)` — empty string when fewer than two accounts are registered for every vendor (the existing Hooks line already covers the single default); otherwise one line per account: `  Accounts: claude .claude-work recording` etc.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Test]
    public async Task Account_lines_are_omitted_for_a_single_default_per_vendor() {
        var line = StatusCommand.BuildAccountLines([(Account(HarnessId.Claude, "/h/.claude"), RecordingState.Recording)]);

        await Assert.That(line).IsEqualTo("");
    }

    [Test]
    public async Task Account_lines_list_each_account_with_its_state() {
        var line = StatusCommand.BuildAccountLines([
            (Account(HarnessId.Claude, "/h/.claude"), RecordingState.Recording),
            (Account(HarnessId.Claude, "/h/.claude-work"), RecordingState.Broken),
        ]);

        await Assert.That(line).Contains(".claude-work");
        await Assert.That(line).Contains("broken");
    }
```

with `static VendorAccount Account(HarnessId v, string dir) => new(dir, v, dir, Path.GetFileName(dir), DateTimeOffset.UnixEpoch);`.

For uninstall, extend `UninstallCommandTests` with a test that registers two Claude accounts (both wired), runs uninstall the way the existing tests do, and asserts both `settings.json` files no longer enable `kcap@kcap` and the `accounts/` directory is gone; and a second test where one account's `settings.json` is malformed: uninstall reports failure and `accounts/accounts.json` still exists.

- [ ] **Step 2: Run to verify failure.** Expected: build error (`BuildAccountLines` missing).

- [ ] **Step 3: Implement**

`StatusCommand`:

```csharp
    internal static string BuildAccountLines(IEnumerable<(VendorAccount Account, RecordingState State)> accounts) {
        var list = accounts.ToList();
        if (list.GroupBy(a => a.Account.Vendor).All(g => g.Count() < 2)) return "";

        return string.Join(Environment.NewLine, list.Select(a =>
            $"  Account: {a.Account.Vendor.ToString().ToLowerInvariant(),-6} {a.Account.Label,-20} {StateWord(a.State)}"));
    }

    static string StateWord(RecordingState state) => state switch {
        RecordingState.Recording => "recording",
        RecordingState.Installed => "hooks installed",
        RecordingState.Broken    => "broken (kcap accounts rewire)",
        _                        => "not wired (kcap accounts rewire)",
    };
```

After the Hooks line in `HandleAsync`, print it when non-empty:

```csharp
        var accountLines = BuildAccountLines(SafeLoad(accounts)?.Accounts.Select(a => (a, AccountWiring.State(a, home))) ?? []);
        if (accountLines.Length > 0) await Console.Out.WriteLineAsync(accountLines);
```

(add `AccountStore accounts` and `UserHome home` to the constructor if absent; `SafeLoad` as in Task 9).

`UninstallCommand`: user-scope `plugin remove` already unwires every account (Task 8). After the existing config-directory deletion decision, delete the registry only when no step failed:

```csharp
        var accountStore = AccountStore.Beside(store);
        if (!hadFailures && Directory.Exists(accountStore.Directory)) {
            try { Directory.Delete(accountStore.Directory, recursive: true); } catch (IOException) { hadFailures = true; }
        }
```

Place it so it runs even with `--keep-config` (the registry is not part of the config directory) but never when an earlier step failed.

- [ ] **Step 4: Run the tests** — new tests pass; `"/*/*/StatusCommand*/*"` and `"/*/*/UninstallCommandTests/*"` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/StatusCommand.cs src/Capacitor.Cli/Commands/UninstallCommand.cs test/Capacitor.Cli.Tests.Unit/Commands
git commit -m "Show each vendor account in status and clear them on uninstall"
```

---

### Task 15: Full verification

**Files:** none new.

- [ ] **Step 1: Solution build**

Run: `dotnet build Capacitor.slnx`
Expected: 0 errors, 0 warnings introduced (IDE0005 unused usings and CA1001 are errors here). A single-project build can pass while the solution fails on test code — always build the solution.

- [ ] **Step 2: Unit suites**

Run each and expect all green:

```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj
```

A daemon timing test failing alone is a known local flake: re-run that test by itself before treating it as real.

- [ ] **Step 3: npm refresh check**

Run: `node --test npm/kcap/bin/refresh.test.js`
Expected: pass. `refresh.js` needs no change: its `plugin install --if-installed` and `plugin install --codex --if-installed` entries now iterate registered accounts through Task 8.

- [ ] **Step 4: AOT publish check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 5: Manual end-to-end check**

With a published binary on PATH: `mkdir ~/.claude-kcaptest && kcap accounts add claude ~/.claude-kcaptest`, then `CLAUDE_CONFIG_DIR=~/.claude-kcaptest claude` (sign in there if prompted), send one message, exit, and confirm the session appears in Capacitor. Then `kcap accounts remove ~/.claude-kcaptest` and confirm `~/.claude-kcaptest/settings.json` no longer enables `kcap@kcap`. Report the outcome; if the sign-in or server step cannot be performed, say so explicitly rather than claiming the check passed.

- [ ] **Step 6: Commit any fixes** from Steps 1–5 with a subject naming what was fixed.
