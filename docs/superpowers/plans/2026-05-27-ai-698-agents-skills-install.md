# Wider skill installation via `~/.agents/skills/` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Install kapacitor skills once, to `~/.agents/skills/kapacitor-*`, so Codex, Cursor, and any future agent honoring the `.agents/skills` convention picks them up. Eliminate the duplicate `kapacitor/codex-skills/` source set.

**Architecture:**
- New `AgentsPaths` (paths) and `AgentsSkillsInstaller` (copy+prefix+frontmatter rewrite, legacy cleanup) in `Kapacitor.Cli.Core`.
- `kapacitor/skills/` becomes the single skill source; folder names drop the `session-` prefix. SKILL.md bodies become agent-agnostic and session-id-env-var-agnostic.
- `kapacitor plugin install --skills` is the new agent-agnostic install flag. `--codex` continues to bundle skills, switching its target from `~/.codex/skills/` to `~/.agents/skills/`. Both install paths run a legacy `~/.codex/skills/kapacitor-*` cleanup *after* a successful new install.
- Codex plugin manifest drops its `skills` field; it now owns only MCP server registration.

**Tech Stack:** .NET 10, TUnit, NativeAOT publish. No new NuGet dependencies.

**Reference spec:** [`docs/superpowers/specs/2026-05-27-agents-skills-install-design.md`](../specs/2026-05-27-agents-skills-install-design.md)

---

## Task 1: Add `AgentsPaths` to `Kapacitor.Cli.Core`

**Files:**
- Create: `src/Kapacitor.Cli.Core/AgentsPaths.cs`
- Test: `test/Kapacitor.Cli.Tests.Unit/AgentsPathsTests.cs`

- [ ] **Step 1: Write the failing test**

`test/Kapacitor.Cli.Tests.Unit/AgentsPathsTests.cs`:

```csharp
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class AgentsPathsTests {
    [Test]
    public async Task Home_resolves_under_HOME_dot_agents() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-agentspaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = AgentsPaths.Home; // force any static init
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(AgentsPaths.Home).IsEqualTo(Path.Combine(tmp.FullName, ".agents"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UserSkillsDir_resolves_under_HOME_dot_agents_skills() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-agentspaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = AgentsPaths.UserSkillsDir;
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(AgentsPaths.UserSkillsDir)
                .IsEqualTo(Path.Combine(tmp.FullName, ".agents", "skills"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentsPathsTests/*"`
Expected: build error — `AgentsPaths` does not exist.

- [ ] **Step 3: Create `AgentsPaths`**

`src/Kapacitor.Cli.Core/AgentsPaths.cs`:

```csharp
namespace Kapacitor.Cli.Core;

public static class AgentsPaths {
    public static string Home          => Path.Combine(PathHelpers.HomeDirectory, ".agents");
    public static string UserSkillsDir => Path.Combine(Home, "skills");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentsPathsTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/AgentsPaths.cs test/Kapacitor.Cli.Tests.Unit/AgentsPathsTests.cs
git commit -m "Add AgentsPaths for ~/.agents/skills/ resolution"
```

---

## Task 2: `AgentsSkillsInstaller.Install` with prefix + frontmatter rewrite

The installer copies each source folder (`recap`, `errors`, …) under `sourceDir` to `targetDir/kapacitor-<name>/`, rewriting the `name:` field inside `SKILL.md` to match. Non-`SKILL.md` files (e.g. `references/*`) are copied verbatim. User-authored folders in the target are left alone. Existing `kapacitor-*` folders are replaced atomically (delete-then-copy).

**Files:**
- Create: `src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs`
- Test: `test/Kapacitor.Cli.Tests.Unit/AgentsSkillsInstallerTests.cs`

- [ ] **Step 1: Write the failing tests**

`test/Kapacitor.Cli.Tests.Unit/AgentsSkillsInstallerTests.cs`:

```csharp
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class AgentsSkillsInstallerTests {
    static readonly string[] SourceNames = ["recap", "errors", "disable", "hide", "validate-plan"];

    [Test]
    public async Task Install_copies_each_source_to_kapacitor_prefixed_target() {
        using var src = new InstallerTempDir();
        using var dst = new InstallerTempDir();

        foreach (var name in SourceNames) {
            Directory.CreateDirectory(Path.Combine(src.Path, name));
            await File.WriteAllTextAsync(
                Path.Combine(src.Path, name, "SKILL.md"),
                $"---\nname: {name}\ndescription: x\n---\nbody\n");
        }

        var ok = AgentsSkillsInstaller.Install(src.Path, dst.Path);
        await Assert.That(ok).IsTrue();

        foreach (var name in SourceNames) {
            var path = Path.Combine(dst.Path, $"kapacitor-{name}", "SKILL.md");
            await Assert.That(File.Exists(path)).IsTrue();
        }
    }

    [Test]
    public async Task Install_rewrites_name_frontmatter_to_kapacitor_prefix() {
        using var src = new InstallerTempDir();
        using var dst = new InstallerTempDir();

        Directory.CreateDirectory(Path.Combine(src.Path, "recap"));
        await File.WriteAllTextAsync(
            Path.Combine(src.Path, "recap", "SKILL.md"),
            "---\nname: recap\ndescription: |\n  long desc\n  with newlines\n---\nbody content\n");

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var written = await File.ReadAllTextAsync(Path.Combine(dst.Path, "kapacitor-recap", "SKILL.md"));
        await Assert.That(written).Contains("name: kapacitor-recap");
        await Assert.That(written).DoesNotContain("name: recap\n");
        await Assert.That(written).Contains("description: |");
        await Assert.That(written).Contains("body content");
    }

    [Test]
    public async Task Install_copies_nested_files_verbatim() {
        using var src = new InstallerTempDir();
        using var dst = new InstallerTempDir();

        var refsDir = Path.Combine(src.Path, "recap", "references");
        Directory.CreateDirectory(refsDir);
        await File.WriteAllTextAsync(Path.Combine(refsDir, "examples.md"), "raw content $not-rewritten");
        await File.WriteAllTextAsync(
            Path.Combine(src.Path, "recap", "SKILL.md"),
            "---\nname: recap\n---\nbody");

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var copied = await File.ReadAllTextAsync(
            Path.Combine(dst.Path, "kapacitor-recap", "references", "examples.md"));
        await Assert.That(copied).IsEqualTo("raw content $not-rewritten");
    }

    [Test]
    public async Task Install_leaves_user_authored_folders_untouched() {
        using var src = new InstallerTempDir();
        using var dst = new InstallerTempDir();

        var foreign = Path.Combine(dst.Path, "user-skill");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "SKILL.md"), "user content");

        Directory.CreateDirectory(Path.Combine(src.Path, "recap"));
        await File.WriteAllTextAsync(
            Path.Combine(src.Path, "recap", "SKILL.md"),
            "---\nname: recap\n---\nbody");

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(File.Exists(Path.Combine(foreign, "SKILL.md"))).IsTrue();
        var content = await File.ReadAllTextAsync(Path.Combine(foreign, "SKILL.md"));
        await Assert.That(content).IsEqualTo("user content");
    }

    [Test]
    public async Task Install_replaces_existing_kapacitor_folder_atomically() {
        using var src = new InstallerTempDir();
        using var dst = new InstallerTempDir();

        // Pre-existing stale skill in target.
        var stale = Path.Combine(dst.Path, "kapacitor-recap");
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "SKILL.md"), "old version");
        await File.WriteAllTextAsync(Path.Combine(stale, "leftover.md"), "delete me");

        Directory.CreateDirectory(Path.Combine(src.Path, "recap"));
        await File.WriteAllTextAsync(
            Path.Combine(src.Path, "recap", "SKILL.md"),
            "---\nname: recap\n---\nnew body");

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var newSkill = await File.ReadAllTextAsync(Path.Combine(stale, "SKILL.md"));
        await Assert.That(newSkill).Contains("new body");
        await Assert.That(File.Exists(Path.Combine(stale, "leftover.md"))).IsFalse();
    }

    [Test]
    public async Task Install_returns_false_when_a_source_folder_is_missing() {
        using var src = new InstallerTempDir();
        using var dst = new InstallerTempDir();

        // Only create some of the expected folders.
        Directory.CreateDirectory(Path.Combine(src.Path, "recap"));
        await File.WriteAllTextAsync(
            Path.Combine(src.Path, "recap", "SKILL.md"),
            "---\nname: recap\n---\nbody");

        var ok = AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(ok).IsFalse();
        // Target should not contain a partial copy.
        await Assert.That(Directory.Exists(Path.Combine(dst.Path, "kapacitor-recap"))).IsFalse();
    }

    sealed class InstallerTempDir : IDisposable {
        public string Path { get; }
        public InstallerTempDir() {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"kapacitor-installer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose() {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentsSkillsInstallerTests/*"`
Expected: build error — `AgentsSkillsInstaller` does not exist.

- [ ] **Step 3: Implement `AgentsSkillsInstaller.Install` and `SourceNames`**

`src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs`:

```csharp
namespace Kapacitor.Cli.Core;

/// <summary>
/// Copies kapacitor skills from a source tree to the user's
/// <c>~/.agents/skills/</c> directory, prefixing each folder name with
/// <c>kapacitor-</c> and rewriting the <c>name:</c> field in SKILL.md to
/// match. Also handles cleanup of legacy <c>~/.codex/skills/kapacitor-*</c>
/// folders left by prior installer versions.
/// </summary>
public static class AgentsSkillsInstaller {
    /// <summary>
    /// Source folder names under <c>kapacitor/skills/</c>. On install each
    /// becomes <c>kapacitor-&lt;name&gt;</c> under the target directory.
    /// Add a new skill here when adding it to <c>kapacitor/skills/</c>.
    /// </summary>
    public static readonly string[] SourceNames = [
        "recap",
        "errors",
        "hide",
        "disable",
        "validate-plan"
    ];

    /// <summary>
    /// Folder names that prior installer versions wrote to
    /// <c>~/.codex/skills/</c>. Fixed list — only these names are removed
    /// during legacy cleanup; user-authored skills are never touched.
    /// </summary>
    public static readonly string[] LegacyCodexSkillNames = [
        "kapacitor-recap",
        "kapacitor-errors",
        "kapacitor-hide",
        "kapacitor-disable",
        "kapacitor-validate-plan"
    ];

    public static bool Install(string sourceDir, string targetDir) {
        if (!Directory.Exists(sourceDir)) return false;

        // Preflight: every known source folder must exist. Fail before
        // doing anything destructive so a packaging defect can't leave the
        // target half-overwritten.
        var missing = SourceNames
            .Where(n => !Directory.Exists(Path.Combine(sourceDir, n)))
            .ToList();
        if (missing.Count > 0) {
            Console.Error.WriteLine(
                $"Cannot install agent skills: missing source folder(s) under {sourceDir}: "
                + string.Join(", ", missing));
            return false;
        }

        try {
            Directory.CreateDirectory(targetDir);

            foreach (var name in SourceNames) {
                var src    = Path.Combine(sourceDir, name);
                var prefix = "kapacitor-" + name;
                var dst    = Path.Combine(targetDir, prefix);

                if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                CopyDirectoryWithFrontmatterRewrite(src, dst, prefix);
            }
            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"Could not install agent skills: {ex.Message}");
            return false;
        }
    }

    static void CopyDirectoryWithFrontmatterRewrite(string source, string destination, string targetName) {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source)) {
            var fileName = Path.GetFileName(file);
            var dstPath  = Path.Combine(destination, fileName);
            if (fileName == "SKILL.md") {
                File.WriteAllText(dstPath, RewriteNameFrontmatter(File.ReadAllText(file), targetName));
            } else {
                File.Copy(file, dstPath, overwrite: true);
            }
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            CopyDirectoryWithFrontmatterRewrite(dir, Path.Combine(destination, Path.GetFileName(dir)), targetName);
        }
    }

    /// <summary>
    /// Replaces the first <c>name:</c> line inside the YAML frontmatter
    /// block of a SKILL.md. The block is delimited by lines containing
    /// only <c>---</c>. If no frontmatter is found, the content is
    /// returned unchanged.
    /// </summary>
    internal static string RewriteNameFrontmatter(string content, string newName) {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---") return content;

        for (var i = 1; i < lines.Length; i++) {
            if (lines[i].Trim() == "---") break; // end of frontmatter
            if (lines[i].StartsWith("name:", StringComparison.Ordinal)) {
                lines[i] = $"name: {newName}";
                break;
            }
        }
        return string.Join('\n', lines);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentsSkillsInstallerTests/*"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs test/Kapacitor.Cli.Tests.Unit/AgentsSkillsInstallerTests.cs
git commit -m "Add AgentsSkillsInstaller.Install with prefix + frontmatter rewrite"
```

---

## Task 3: `AgentsSkillsInstaller.Remove` and `CleanLegacyCodexSkills`

**Files:**
- Modify: `src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs`
- Modify: `test/Kapacitor.Cli.Tests.Unit/AgentsSkillsInstallerTests.cs`

- [ ] **Step 1: Append failing tests**

Append to `test/Kapacitor.Cli.Tests.Unit/AgentsSkillsInstallerTests.cs` (inside the `AgentsSkillsInstallerTests` class, before the `InstallerTempDir` helper):

```csharp
    [Test]
    public async Task Remove_deletes_kapacitor_prefixed_folders_only() {
        using var dst = new InstallerTempDir();

        foreach (var src in SourceNames) {
            Directory.CreateDirectory(Path.Combine(dst.Path, $"kapacitor-{src}"));
        }
        Directory.CreateDirectory(Path.Combine(dst.Path, "user-skill"));

        var removed = AgentsSkillsInstaller.Remove(dst.Path);

        await Assert.That(removed).IsTrue();
        foreach (var src in SourceNames) {
            await Assert.That(Directory.Exists(Path.Combine(dst.Path, $"kapacitor-{src}"))).IsFalse();
        }
        await Assert.That(Directory.Exists(Path.Combine(dst.Path, "user-skill"))).IsTrue();
    }

    [Test]
    public async Task Remove_returns_false_when_no_kapacitor_folders_present() {
        using var dst = new InstallerTempDir();
        Directory.CreateDirectory(Path.Combine(dst.Path, "someone-elses-skill"));

        var removed = AgentsSkillsInstaller.Remove(dst.Path);

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_removes_only_known_kapacitor_folders() {
        using var fakeHome = new InstallerTempDir();
        var legacy = Path.Combine(fakeHome.Path, ".codex", "skills");
        Directory.CreateDirectory(legacy);

        foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
            Directory.CreateDirectory(Path.Combine(legacy, name));
        }
        Directory.CreateDirectory(Path.Combine(legacy, "user-codex-skill"));

        var removed = AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(removed).IsTrue();
        foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
            await Assert.That(Directory.Exists(Path.Combine(legacy, name))).IsFalse();
        }
        await Assert.That(Directory.Exists(Path.Combine(legacy, "user-codex-skill"))).IsTrue();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_removes_empty_parent_dir() {
        using var fakeHome = new InstallerTempDir();
        var legacy = Path.Combine(fakeHome.Path, ".codex", "skills");
        Directory.CreateDirectory(legacy);
        foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
            Directory.CreateDirectory(Path.Combine(legacy, name));
        }

        AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(Directory.Exists(legacy)).IsFalse();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_preserves_non_empty_parent_dir() {
        using var fakeHome = new InstallerTempDir();
        var legacy = Path.Combine(fakeHome.Path, ".codex", "skills");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(Path.Combine(legacy, "kapacitor-recap"));
        Directory.CreateDirectory(Path.Combine(legacy, "user-codex-skill"));

        AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(Directory.Exists(legacy)).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(legacy, "user-codex-skill"))).IsTrue();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_is_noop_when_parent_dir_missing() {
        using var fakeHome = new InstallerTempDir();
        var legacy = Path.Combine(fakeHome.Path, ".codex", "skills");
        // legacy dir not created

        var removed = AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task Install_failure_does_not_trigger_legacy_cleanup() {
        // Asserts the contract: install runs first, legacy cleanup runs only on success.
        // The contract lives in PluginCommand (caller); here we verify the unit
        // primitives are independent and the caller can sequence them safely.
        using var src      = new InstallerTempDir();
        using var fakeHome = new InstallerTempDir();
        var legacy = Path.Combine(fakeHome.Path, ".codex", "skills");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(Path.Combine(legacy, "kapacitor-recap"));

        // sourceDir empty -> Install returns false without throwing.
        var ok = AgentsSkillsInstaller.Install(src.Path, Path.Combine(fakeHome.Path, ".agents", "skills"));
        await Assert.That(ok).IsFalse();

        // Caller would skip cleanup. Verify directly that legacy dir is still present.
        await Assert.That(Directory.Exists(Path.Combine(legacy, "kapacitor-recap"))).IsTrue();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentsSkillsInstallerTests/*"`
Expected: build error — `Remove` and `CleanLegacyCodexSkills` not defined.

- [ ] **Step 3: Implement `Remove` and `CleanLegacyCodexSkills`**

Append to `src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs` (inside the class):

```csharp
    /// <summary>
    /// Deletes every <c>kapacitor-&lt;name&gt;</c> folder this installer owns
    /// from <paramref name="targetDir"/>. User-authored folders are left alone.
    /// Returns true if any folder was removed.
    /// </summary>
    public static bool Remove(string targetDir) {
        if (!Directory.Exists(targetDir)) return false;

        var changed = false;
        foreach (var name in SourceNames) {
            var dst = Path.Combine(targetDir, "kapacitor-" + name);
            if (!Directory.Exists(dst)) continue;
            try {
                Directory.Delete(dst, recursive: true);
                changed = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"Could not remove agent skill 'kapacitor-{name}': {ex.Message}");
            }
        }
        return changed;
    }

    /// <summary>
    /// Removes legacy <c>kapacitor-*</c> folders that prior installer versions
    /// wrote to <c>~/.codex/skills/</c>. List of folder names is fixed
    /// (<see cref="LegacyCodexSkillNames"/>); user-authored skills are never
    /// touched. If the parent directory becomes empty, it is removed too.
    /// </summary>
    public static bool CleanLegacyCodexSkills(string legacySkillsDir) {
        if (!Directory.Exists(legacySkillsDir)) return false;

        var changed = false;
        foreach (var name in LegacyCodexSkillNames) {
            var dst = Path.Combine(legacySkillsDir, name);
            if (!Directory.Exists(dst)) continue;
            try {
                Directory.Delete(dst, recursive: true);
                Console.Out.WriteLine($"Removed legacy Codex skill folder: {dst}");
                changed = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"Could not remove legacy Codex skill '{name}': {ex.Message}");
            }
        }

        // Clean up empty parent.
        try {
            if (Directory.Exists(legacySkillsDir) && !Directory.EnumerateFileSystemEntries(legacySkillsDir).Any()) {
                Directory.Delete(legacySkillsDir);
            }
        } catch {
            // Best effort — leaving an empty dir behind isn't harmful.
        }

        return changed;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentsSkillsInstallerTests/*"`
Expected: PASS (all 13 tests in the class).

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/AgentsSkillsInstaller.cs test/Kapacitor.Cli.Tests.Unit/AgentsSkillsInstallerTests.cs
git commit -m "Add AgentsSkillsInstaller.Remove and CleanLegacyCodexSkills"
```

---

## Task 4: Rewrite SKILL.md content to be agent- and session-id-env-var-agnostic

Each of the five SKILL.md files in `kapacitor/skills/*/SKILL.md` must satisfy:

- **Rule 1 (env vars):** No mention of `KAPACITOR_SESSION_ID` or `CODEX_THREAD_ID`. Other env vars (e.g. `KAPACITOR_URL`) remain.
- **Rule 2 (agent names):** No mention of Claude, Codex, or Cursor in the body.
- **Rule 3 (session id):** Wherever the old text described session-id resolution, substitute the agent-agnostic paragraph (the skill's own command substituted):
  > Run `kapacitor <command>`. It resolves the current session id from the environment when the host agent CLI exposes one. If no session id is available, pass it explicitly: `kapacitor <command> <sessionId>`.

The MCP hint at the top of `session-recap/SKILL.md` ("when the `kapacitor-sessions` MCP server is available…") **stays** — it is already agent-agnostic and equally true for Codex and Cursor as Claude.

**Files:**
- Modify: `kapacitor/skills/session-recap/SKILL.md`
- Modify: `kapacitor/skills/session-errors/SKILL.md`
- Modify: `kapacitor/skills/session-hide/SKILL.md`
- Modify: `kapacitor/skills/session-disable/SKILL.md`
- Modify: `kapacitor/skills/validate-plan/SKILL.md`

> **Note for the engineer:** This task edits content in-place; the *folder rename* (`session-recap` → `recap` etc.) happens in Task 5. Do the content rewrite first so the rename step is a pure `git mv`.

- [ ] **Step 1: Read each SKILL.md and identify lines that violate Rules 1–3**

For each of the five files, run:

```bash
for f in kapacitor/skills/*/SKILL.md; do
  echo "=== $f ==="
  grep -nE "KAPACITOR_SESSION_ID|CODEX_THREAD_ID|Claude|Codex|Cursor" "$f" || echo "  (clean)"
done
```

Note: the description fields in frontmatter contain trigger phrases — those are fine if they don't mention agent names. Be conservative; the goal is the *body*, not the YAML frontmatter trigger list (which mostly contains user phrases like "what did we do last time").

- [ ] **Step 2: Apply the rewrite**

For each file, replace any sentence referencing `KAPACITOR_SESSION_ID` (or analogous wording) with the template paragraph above, substituting the skill's own subcommand (`recap`, `errors`, `validate-plan`, `hide`, `disable`).

In `kapacitor/skills/session-recap/SKILL.md` specifically, the line currently reading:

> The session ID is automatically set by the `KAPACITOR_SESSION_ID` environment variable (persisted at session start). You can pass an explicit ID to review a different session.

becomes:

> `kapacitor recap` resolves the current session id from the environment when the host agent CLI exposes one. If no session id is available, pass it explicitly: `kapacitor recap <sessionId>`.

The "### Environment" section's `KAPACITOR_URL` paragraph **stays** verbatim (agent-agnostic env var).

Any heading or sentence naming "Claude" should be reworded to be agent-neutral (e.g. "Claude's text responses" → "Assistant text responses"). The `## Full Output` table that lists `## Assistant` etc. uses these labels to describe transcript section types — those are output labels, not agent claims, but the explanatory text "Claude's text responses" should become "Agent text responses".

- [ ] **Step 3: Verify no banned strings remain**

```bash
for f in kapacitor/skills/*/SKILL.md; do
  bad=$(grep -nE "KAPACITOR_SESSION_ID|CODEX_THREAD_ID|\bClaude\b|\bCodex\b|\bCursor\b" "$f" || true)
  if [ -n "$bad" ]; then echo "=== VIOLATION in $f ==="; echo "$bad"; fi
done
```

Expected: no output. If anything prints, fix the file and re-run.

- [ ] **Step 4: Commit**

```bash
git add kapacitor/skills/
git commit -m "Make SKILL.md bodies agent- and session-id-env-var-agnostic"
```

---

## Task 5: Rename source skill folders (`session-*` → unprefixed)

The `kapacitor-` prefix is applied by the installer at copy time. The Claude plugin reads from `kapacitor/skills/` directly and namespaces skills as `kapacitor:<folder>`. To get short invocations like `kapacitor:recap`, rename the source folders.

**Files:**
- Rename: `kapacitor/skills/session-recap/` → `kapacitor/skills/recap/`
- Rename: `kapacitor/skills/session-errors/` → `kapacitor/skills/errors/`
- Rename: `kapacitor/skills/session-hide/` → `kapacitor/skills/hide/`
- Rename: `kapacitor/skills/session-disable/` → `kapacitor/skills/disable/`
- (`kapacitor/skills/validate-plan/` unchanged.)
- Modify: each renamed `SKILL.md` `name:` field.

- [ ] **Step 1: `git mv` each folder**

```bash
git mv kapacitor/skills/session-recap   kapacitor/skills/recap
git mv kapacitor/skills/session-errors  kapacitor/skills/errors
git mv kapacitor/skills/session-hide    kapacitor/skills/hide
git mv kapacitor/skills/session-disable kapacitor/skills/disable
```

- [ ] **Step 2: Update each `SKILL.md`'s `name:` field**

```bash
sed -i.bak '0,/^name: session-recap$/s//name: recap/'   kapacitor/skills/recap/SKILL.md
sed -i.bak '0,/^name: session-errors$/s//name: errors/' kapacitor/skills/errors/SKILL.md
sed -i.bak '0,/^name: session-hide$/s//name: hide/'     kapacitor/skills/hide/SKILL.md
sed -i.bak '0,/^name: session-disable$/s//name: disable/' kapacitor/skills/disable/SKILL.md
rm kapacitor/skills/*/SKILL.md.bak
```

Verify:

```bash
grep -H "^name:" kapacitor/skills/*/SKILL.md
```

Expected:

```
kapacitor/skills/disable/SKILL.md:name: disable
kapacitor/skills/errors/SKILL.md:name: errors
kapacitor/skills/hide/SKILL.md:name: hide
kapacitor/skills/recap/SKILL.md:name: recap
kapacitor/skills/validate-plan/SKILL.md:name: validate-plan
```

- [ ] **Step 3: Commit**

```bash
git add kapacitor/skills/
git commit -m "Rename Claude plugin skill folders to drop session- prefix"
```

---

## Task 6: Switch `PluginCommand` Codex install/remove to use `AgentsSkillsInstaller`, add `--skills` flag

This is the central wiring change. After this task:
- `kapacitor plugin install --codex` writes hooks + `~/.agents/skills/kapacitor-*`, then cleans legacy `~/.codex/skills/kapacitor-*`.
- `kapacitor plugin install --skills` writes only `~/.agents/skills/kapacitor-*`, then cleans legacy.
- `kapacitor plugin remove --codex` removes hooks + `~/.agents/skills/kapacitor-*` + legacy.
- `kapacitor plugin remove --skills` removes `~/.agents/skills/kapacitor-*` + legacy.
- `PluginCommand` no longer maintains `CodexSkillNames`, `ValidateCodexSkillsSource`, `InstallCodexSkills`, `RemoveCodexSkills` — all moved to `AgentsSkillsInstaller`.

**Files:**
- Modify: `src/Kapacitor.Cli/Commands/PluginCommand.cs`
- Modify: `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs`
- Create: `test/Kapacitor.Cli.Tests.Unit/PluginCommandSkillsTests.cs`

- [ ] **Step 1: Write tests for `--skills` install/remove (new file)**

`test/Kapacitor.Cli.Tests.Unit/PluginCommandSkillsTests.cs`:

```csharp
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandSkillsTests {
    [Test]
    public async Task Install_skills_writes_to_agents_dir_and_cleans_legacy() {
        var fakeHome      = Directory.CreateTempSubdirectory("kapacitor-plugin-skills-test-");
        var originalHome  = Environment.GetEnvironmentVariable("HOME");
        var pluginPath    = Directory.CreateTempSubdirectory("kapacitor-plugin-src-");
        var originalPlug  = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");

        try {
            var skillsSrc = Path.Combine(pluginPath.FullName, "skills");
            Directory.CreateDirectory(skillsSrc);
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                Directory.CreateDirectory(Path.Combine(skillsSrc, name));
                await File.WriteAllTextAsync(
                    Path.Combine(skillsSrc, name, "SKILL.md"),
                    $"---\nname: {name}\n---\nbody");
            }

            // Pre-seed legacy folders so we can verify cleanup.
            var legacyDir = Path.Combine(fakeHome.FullName, ".codex", "skills");
            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(Path.Combine(legacyDir, "kapacitor-recap"));

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", pluginPath.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "install", "--skills"]);
            await Assert.That(exit).IsEqualTo(0);

            var target = Path.Combine(fakeHome.FullName, ".agents", "skills");
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                await Assert.That(Directory.Exists(Path.Combine(target, $"kapacitor-{name}"))).IsTrue();
            }
            // Legacy cleaned up.
            await Assert.That(Directory.Exists(Path.Combine(legacyDir, "kapacitor-recap"))).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("KAPACITOR_PLUGIN_DIR", originalPlug);
            fakeHome.Delete(recursive: true);
            pluginPath.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Remove_skills_clears_agents_and_legacy() {
        var fakeHome     = Directory.CreateTempSubdirectory("kapacitor-plugin-skills-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            var agentsDir = Path.Combine(fakeHome.FullName, ".agents", "skills");
            var legacyDir = Path.Combine(fakeHome.FullName, ".codex", "skills");
            Directory.CreateDirectory(agentsDir);
            Directory.CreateDirectory(legacyDir);
            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                Directory.CreateDirectory(Path.Combine(agentsDir, $"kapacitor-{name}"));
            }
            foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
                Directory.CreateDirectory(Path.Combine(legacyDir, name));
            }

            Environment.SetEnvironmentVariable("HOME", fakeHome.FullName);

            var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--skills"]);
            await Assert.That(exit).IsEqualTo(0);

            foreach (var name in AgentsSkillsInstaller.SourceNames) {
                await Assert.That(Directory.Exists(Path.Combine(agentsDir, $"kapacitor-{name}"))).IsFalse();
            }
            // Legacy gone too.
            await Assert.That(Directory.Exists(legacyDir)).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            fakeHome.Delete(recursive: true);
        }
    }
}
```

> **Note:** `KAPACITOR_PLUGIN_DIR` is the override SetupCommand.ResolvePluginPath() uses to locate the plugin folder during tests. If this env var override does not currently exist in `SetupCommand`, add it as part of this step (small change: `Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR")` checked at the top of `ResolvePluginPath()`).

- [ ] **Step 2: Verify `SetupCommand.ResolvePluginPath` supports `KAPACITOR_PLUGIN_DIR` override**

Run:

```bash
grep -n "KAPACITOR_PLUGIN_DIR\|ResolvePluginPath" src/Kapacitor.Cli/Commands/SetupCommand.cs
```

If `KAPACITOR_PLUGIN_DIR` is not honored, add this at the top of `ResolvePluginPath()`:

```csharp
var overrideDir = Environment.GetEnvironmentVariable("KAPACITOR_PLUGIN_DIR");
if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir)) {
    return overrideDir;
}
```

- [ ] **Step 3: Update `PluginCommand` — Install branching**

In `src/Kapacitor.Cli/Commands/PluginCommand.cs`, change the `Install` dispatcher (around line 44):

```csharp
static async Task<int> Install(string[] args) {
    if (args.Contains("--skills")) {
        return await InstallSkills(args);
    }
    if (args.Contains("--codex")) {
        return await InstallCodex(args);
    }
    return await InstallClaude(args);
}

static async Task<int> Remove(string[] args) {
    if (args.Contains("--skills")) {
        return await RemoveSkills(args);
    }
    if (args.Contains("--codex")) {
        return await RemoveCodex(args);
    }
    return await RemoveClaude(args);
}
```

Add the new `InstallSkills` and `RemoveSkills` methods:

```csharp
static async Task<int> InstallSkills(string[] _) {
    var pluginPath = SetupCommand.ResolvePluginPath();
    if (pluginPath is null) {
        await Console.Error.WriteLineAsync(
            "Cannot install agent skills: kapacitor plugin folder not found. " +
            "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
        return 1;
    }

    var skillsSource = Path.Combine(pluginPath, "skills");
    if (!Directory.Exists(skillsSource)) {
        await Console.Error.WriteLineAsync(
            $"Cannot install agent skills: 'skills' folder missing from {pluginPath}. " +
            "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
        return 1;
    }

    if (!AgentsSkillsInstaller.Install(skillsSource, AgentsPaths.UserSkillsDir)) {
        await Console.Error.WriteLineAsync("Could not install agent skills.");
        return 1;
    }
    await Console.Out.WriteLineAsync($"Agent skills installed (user: {AgentsPaths.UserSkillsDir})");

    AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));
    return 0;
}

static async Task<int> RemoveSkills(string[] _) {
    var agentsRemoved = AgentsSkillsInstaller.Remove(AgentsPaths.UserSkillsDir);
    if (agentsRemoved) {
        await Console.Out.WriteLineAsync($"Agent skills removed (user: {AgentsPaths.UserSkillsDir})");
    }
    var legacyRemoved = AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));
    if (!agentsRemoved && !legacyRemoved) {
        await Console.Out.WriteLineAsync("Nothing to remove — agent skills were not installed.");
    }
    return 0;
}
```

Replace the body of `InstallCodex` (line 138) with a version that points at the new source folder and uses `AgentsSkillsInstaller`:

```csharp
static async Task<int> InstallCodex(string[] args) {
    var scope = args.Contains("--project") ? "project" : "user";
    var hooksPath = scope == "project"
        ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
        : CodexPaths.UserHooksJson;

    var pluginPath = SetupCommand.ResolvePluginPath();
    if (pluginPath is null) {
        await Console.Error.WriteLineAsync(
            "Cannot install Codex plugin: kapacitor plugin folder not found. " +
            "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
        return 1;
    }

    var skillsSource = Path.Combine(pluginPath, "skills");
    if (!Directory.Exists(skillsSource)) {
        await Console.Error.WriteLineAsync(
            $"Cannot install Codex plugin: 'skills' folder missing from {pluginPath}. " +
            "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
        return 1;
    }

    if (!InstallCodexHooks(hooksPath)) {
        await Console.Error.WriteLineAsync("Could not write Codex hooks file.");
        return 1;
    }
    await Console.Out.WriteLineAsync($"Codex hooks installed ({scope}: {hooksPath})");
    await Console.Out.WriteLineAsync(
        "Next: run /hooks inside Codex and trust each kapacitor entry — " +
        "Codex won't execute hooks until each is explicitly trusted.");

    if (!AgentsSkillsInstaller.Install(skillsSource, AgentsPaths.UserSkillsDir)) {
        await Console.Error.WriteLineAsync("Could not install agent skills.");
        return 1;
    }
    await Console.Out.WriteLineAsync($"Agent skills installed (user: {AgentsPaths.UserSkillsDir})");

    // Legacy cleanup runs only after both hooks and skills succeed, so a partial
    // install never leaves the user without working skills.
    AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

    if (scope == "project") {
        await Console.Out.WriteLineAsync(
            "Note: Codex requires the project's .codex directory to be trusted. " +
            "Run `codex` once in this directory and accept the trust prompt.");
    }
    return 0;
}
```

Replace the body of `RemoveCodex` (line 221):

```csharp
static async Task<int> RemoveCodex(string[] args) {
    var scope = args.Contains("--project") ? "project" : "user";
    var hooksPath = scope == "project"
        ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
        : CodexPaths.UserHooksJson;

    var hooksRemoved = File.Exists(hooksPath) && RemoveCodexHooks(hooksPath);
    if (hooksRemoved) {
        await Console.Out.WriteLineAsync($"Codex hooks removed ({scope}: {hooksPath})");
    }

    var agentsRemoved = AgentsSkillsInstaller.Remove(AgentsPaths.UserSkillsDir);
    if (agentsRemoved) {
        await Console.Out.WriteLineAsync($"Agent skills removed (user: {AgentsPaths.UserSkillsDir})");
    }

    var legacyRemoved = AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

    if (!hooksRemoved && !agentsRemoved && !legacyRemoved) {
        await Console.Out.WriteLineAsync("Nothing to remove — hooks and skills were not installed.");
    }
    return 0;
}
```

Delete the now-obsolete members from `PluginCommand` (lines 11–20 and 353–449 in the original):

- `CodexSkillNames` array
- `ValidateCodexSkillsSource`
- `InstallCodexSkills`
- `RemoveCodexSkills`
- `CopyDirectory` helper (was only used by `InstallCodexSkills`)

Update `PrintUsage` (line 452) to mention `--skills`:

```csharp
static int PrintUsage() {
    Console.Error.WriteLine("Usage: kapacitor plugin <install|remove> [--project] [--codex|--skills]");
    return 1;
}
```

- [ ] **Step 4: Update `PluginCommandCodexTests`**

Open `test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs`. Several tests reference `PluginCommand.InstallCodexSkills`, `PluginCommand.RemoveCodexSkills`, or `CodexSkillNames`. For each:

- Replace `PluginCommand.InstallCodexSkills(src, dst)` calls with `AgentsSkillsInstaller.Install(src, dst)`.
- Replace `PluginCommand.RemoveCodexSkills(dst)` calls with `AgentsSkillsInstaller.Remove(dst)`.
- Replace `PluginCommand.CodexSkillNames` references with `AgentsSkillsInstaller.SourceNames` (these are now unprefixed names).
- Where a test asserts on the prefixed folder name in the target (e.g. `kapacitor-recap`), wrap the expectation with the prefix logic — i.e. assert `Directory.Exists(Path.Combine(dst, "kapacitor-" + name))`.
- Remove or update any test that depends on the old `codex-skills/` source folder name; the new path is `<plugin>/skills/`.

Run the suite to find every reference:

```bash
grep -n "InstallCodexSkills\|RemoveCodexSkills\|CodexSkillNames\|ValidateCodexSkillsSource\|codex-skills" \
    test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs
```

Fix each line. The hook-related tests (`InstallCodexHooks_*`, `RemoveCodexHooks_*`) are unchanged.

- [ ] **Step 5: Run tests**

```bash
dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/PluginCommandCodexTests/*"
dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/PluginCommandSkillsTests/*"
```

Expected: PASS on both filters.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Cli/Commands/PluginCommand.cs \
        src/Kapacitor.Cli/Commands/SetupCommand.cs \
        test/Kapacitor.Cli.Tests.Unit/PluginCommandCodexTests.cs \
        test/Kapacitor.Cli.Tests.Unit/PluginCommandSkillsTests.cs
git commit -m "Switch Codex install to ~/.agents/skills/ and add --skills flag"
```

---

## Task 7: Update `CodingAgentsStep` to use the new paths and prompt wording

**Files:**
- Modify: `src/Kapacitor.Cli/Commands/CodingAgentsStep.cs`
- Modify: `test/Kapacitor.Cli.Tests.Unit/CodingAgentsStepTests.cs`
- Modify: any call site that constructs `CodingAgentsStep.Paths` (likely `SetupCommand`).

- [ ] **Step 1: Update tests for new wording and path**

Open `test/Kapacitor.Cli.Tests.Unit/CodingAgentsStepTests.cs` and update:

- Rename any test field/local from `CodexSkillsDir` to `AgentsSkillsDir`.
- Update any assertion that depends on the old prompt string "Install Codex CLI hooks and 5 skills?" to expect "Install Codex CLI hooks and kapacitor agent skills?".
- Update the installer-delegate test: where `InstallCodexSkills` was called with `(pluginDir/codex-skills, codexSkillsDir)`, it is now called with `(pluginDir/skills, agentsSkillsDir)`.

Run them first to see what breaks:

```bash
dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/CodingAgentsStepTests/*"
```

- [ ] **Step 2: Update `CodingAgentsStep`**

In `src/Kapacitor.Cli/Commands/CodingAgentsStep.cs`:

Rename the `Paths` field:

```csharp
internal record Paths(
    string ClaudeSettingsPath,
    string ClaudeScopeLabel,
    string? PluginDir,
    string CodexHooksPath,
    string AgentsSkillsDir);
```

Update `HandleCodexSkills` (line 50) — source folder is now `skills/`, log line text uses the new prefixed names but those are the same strings as today:

```csharp
static bool HandleCodexSkills(
    Paths paths,
    Installers installers,
    Action<string> writeLine) {
    if (paths.PluginDir is null) {
        writeLine("  [yellow]⚠[/] Codex hooks installed but agent skills could not be copied (plugin directory not found).");
        return false;
    }

    var src = Path.Combine(paths.PluginDir, "skills");
    var ok  = installers.InstallCodexSkills(src, paths.AgentsSkillsDir);

    if (!ok) {
        writeLine($"  [yellow]⚠[/] Codex hooks installed but agent skills could not be copied to {Markup.Escape(paths.AgentsSkillsDir)}");
        return false;
    }

    writeLine($"  [green]✓[/] Agent skills installed (user: {Markup.Escape(paths.AgentsSkillsDir)})");
    writeLine("    [dim]kapacitor-recap, kapacitor-errors, kapacitor-hide, kapacitor-disable, kapacitor-validate-plan[/]");
    return true;
}
```

> Note: the `Installers.InstallCodexSkills` delegate name stays as a delegate field name (it's just a delegate identifier), but the implementation behind it now points at `AgentsSkillsInstaller.Install`. The wiring change happens in the caller (Task 8 below). To make the intent clearer for future readers, also rename the delegate field to `InstallAgentSkills`:

```csharp
internal record Installers(
    Func<string /*settingsPath*/, string /*pluginDir*/, bool> InstallClaudePlugin,
    Func<string /*hooksPath*/, bool>                          InstallCodexHooks,
    Func<string /*srcDir*/, string /*dstDir*/, bool>          InstallAgentSkills);
```

Replace `installers.InstallCodexSkills` with `installers.InstallAgentSkills` in the call above.

Update `HandleCodexHooks` (line 91) prompt wording:

```csharp
var shouldInstall = options.NoPrompt || prompt("Install Codex CLI hooks and kapacitor agent skills?");
```

- [ ] **Step 3: Update `SetupCommand` (or wherever `Paths` is constructed)**

```bash
grep -rn "CodexSkillsDir\|CodexSkillsDir:" src/Kapacitor.Cli/
```

For each match, swap to:

```csharp
AgentsSkillsDir: AgentsPaths.UserSkillsDir,
```

The installer wiring (whichever site builds `Installers`) updates from `PluginCommand.InstallCodexSkills` to `AgentsSkillsInstaller.Install`:

```csharp
InstallAgentSkills: AgentsSkillsInstaller.Install,
```

- [ ] **Step 4: Run tests**

```bash
dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/CodingAgentsStepTests/*"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli/Commands/CodingAgentsStep.cs \
        src/Kapacitor.Cli/Commands/SetupCommand.cs \
        test/Kapacitor.Cli.Tests.Unit/CodingAgentsStepTests.cs
git commit -m "Wire setup bootstrap to ~/.agents/skills/ with combined prompt"
```

---

## Task 8: Delete `kapacitor/codex-skills/` and drop `skills` field from Codex plugin manifest

**Files:**
- Delete: `kapacitor/codex-skills/` (all 5 subfolders)
- Modify: `kapacitor/.codex-plugin/plugin.json`

- [ ] **Step 1: Remove the directory**

```bash
git rm -r kapacitor/codex-skills
```

- [ ] **Step 2: Update `kapacitor/.codex-plugin/plugin.json`**

Drop the `skills` line. New content:

```json
{
  "name": "kapacitor",
  "version": "1.6.0",
  "description": "Records and visualizes Codex CLI sessions via kapacitor CLI hooks",
  "mcpServers": "./.codex-mcp.json"
}
```

Verify:

```bash
cat kapacitor/.codex-plugin/plugin.json
```

- [ ] **Step 3: Build and run all tests to confirm nothing else referenced `codex-skills/`**

```bash
dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj
dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj
```

Expected: build succeeds; all tests pass.

If anything still references `codex-skills/`:

```bash
grep -rn "codex-skills" src/ test/ kapacitor/ docs/ README.md 2>/dev/null
```

Fix any remaining references (skill body docs, READMEs, etc.) before continuing.

- [ ] **Step 4: Commit**

```bash
git add kapacitor/codex-skills kapacitor/.codex-plugin/plugin.json
git commit -m "Delete kapacitor/codex-skills and drop skills field from Codex plugin manifest"
```

---

## Task 9: Remove `CodexPaths.UserSkillsDir`

The Codex install path no longer writes there. The legacy cleanup uses `Path.Combine(CodexPaths.Home, "skills")` inline so we don't need the named property.

**Files:**
- Modify: `src/Kapacitor.Cli.Core/CodexPaths.cs`
- Modify: any caller of `CodexPaths.UserSkillsDir`.

- [ ] **Step 1: Find callers**

```bash
grep -rn "CodexPaths.UserSkillsDir" src/ test/
```

Expected: zero results (Task 6 already replaced them). If any remain, fix them before deleting the property.

- [ ] **Step 2: Delete the property**

In `src/Kapacitor.Cli.Core/CodexPaths.cs`, delete line 7:

```csharp
public static string UserSkillsDir => Path.Combine(Home, "skills");
```

- [ ] **Step 3: Build and run all tests**

```bash
dotnet build src/Kapacitor.Cli/Kapacitor.Cli.csproj
dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj
```

Expected: build succeeds; all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Kapacitor.Cli.Core/CodexPaths.cs
git commit -m "Remove unused CodexPaths.UserSkillsDir"
```

---

## Task 10: Update help text + READMEs

**Files:**
- Modify: `src/Kapacitor.Cli.Core/Resources/help-plugin.txt`
- Modify: `kapacitor/README.md`
- Modify: `README.md` (top-level)

- [ ] **Step 1: Rewrite `help-plugin.txt`**

`src/Kapacitor.Cli.Core/Resources/help-plugin.txt`:

```
kapacitor plugin — Manage hooks and skills for Claude Code, Codex CLI, and other agents

Usage: kapacitor plugin <subcommand> [options]

Subcommands:
  install             Register the plugin / hooks / skills
  remove              Remove the plugin / hooks / skills

Options:
  --project           Apply hooks to current project only (default: user-wide).
                      Skills are always user-wide; --project only affects hooks.
  --codex             Target Codex CLI: install hooks AND agent skills.
  --skills            Install only the agent-agnostic skills to ~/.agents/skills/.
                      Use this if you only have Cursor (or another agent that
                      reads ~/.agents/skills/) and don't want Codex hooks.

Notes:
  Skills install to ~/.agents/skills/kapacitor-{recap,errors,hide,disable,validate-plan}/.
  Codex, Cursor, and any other agent that honors the .agents/skills convention
  will pick them up automatically.

  --codex also writes hooks to ~/.codex/hooks.json (or <repo>/.codex/hooks.json
  with --project). MCP server registration is handled separately by the Codex
  plugin manifest and is not affected by this command.

  install --codex and install --skills both clean up legacy ~/.codex/skills/kapacitor-*
  folders from prior installer versions.

Examples:
  kapacitor plugin install                       # Install Claude Code plugin (user)
  kapacitor plugin install --skills              # Install agent skills only (~/.agents/skills/)
  kapacitor plugin install --codex               # Install Codex hooks + agent skills (user)
  kapacitor plugin install --project --codex     # Codex hooks in <repo>/.codex/hooks.json, skills user-wide
  kapacitor plugin remove --codex                # Remove Codex hooks + agent skills + legacy ~/.codex/skills
  kapacitor plugin remove --skills               # Remove agent skills + legacy ~/.codex/skills
```

- [ ] **Step 2: Update `kapacitor/README.md`**

Open `kapacitor/README.md` and replace any wording that says:
- "Skills" referring specifically to Claude — generalize to "skills installed under `~/.agents/skills/` for Codex, Cursor, and any future agent that honors the convention".
- The list of skills under the Claude section: update folder names if listed (`session-recap` → `recap`, etc.; or use the install names `kapacitor-recap` if the doc shows install paths).
- Any reference to `~/.codex/skills/` for skills — point at `~/.agents/skills/` instead.
- The `kapacitor plugin install --codex` command description: note that skills now go to `~/.agents/skills/` and a new `--skills` flag is available.

Grep first to find every relevant location:

```bash
grep -nE "session-recap|kapacitor-recap|\.codex/skills|codex-skills|Skills|skill" kapacitor/README.md
```

- [ ] **Step 3: Update top-level `README.md`**

Per `CLAUDE.md`, any user-facing CLI surface change requires syncing the top-level `README.md`. Update:

- The `## Getting started` section: if it shows `kapacitor plugin install --codex`, mention the new `--skills` flag and the new install path.
- The `## CLI commands` section, `plugin` entry: mirror the new `help-plugin.txt`.

```bash
grep -nE "plugin install|plugin remove|\.codex/skills|\.agents/skills" README.md
```

- [ ] **Step 4: Verify**

```bash
diff <(sed -n '/^kapacitor plugin/,/^Examples:/p' src/Kapacitor.Cli.Core/Resources/help-plugin.txt) \
     <(awk '/^### plugin/,/^### /' README.md) || echo "manually verify CLI section matches help text"
```

(Manual diff — perfect match isn't required; sync the substance.)

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Cli.Core/Resources/help-plugin.txt kapacitor/README.md README.md
git commit -m "Document --skills flag and ~/.agents/skills/ install path"
```

---

## Task 11: Verify NativeAOT publish has no new warnings

Per `CLAUDE.md`, AOT warnings only appear on publish. The installer uses `System.IO` and string manipulation only — no reflection — so this should be clean, but always verify.

**Files:** none (verification only).

- [ ] **Step 1: Publish in Release mode**

```bash
dotnet publish src/Kapacitor.Cli/Kapacitor.Cli.csproj -c Release 2>&1 | tee /tmp/kapacitor-publish.log
```

- [ ] **Step 2: Scan for trimming/AOT warnings**

```bash
grep -E 'IL[23][01][0-9]{2}' /tmp/kapacitor-publish.log || echo "no AOT warnings"
```

Expected: `no AOT warnings`. If any appear, fix the root cause — common culprits per `CLAUDE.md` are JsonArray collection expressions; we use plain string and `System.IO` here, so any warning is unexpected and worth investigating.

- [ ] **Step 3: macOS only — re-sign the AOT binary if you ran `dotnet publish` after copying**

```bash
case "$(uname)" in Darwin) codesign --force --sign - "$(find src/Kapacitor.Cli/bin/Release -name kapacitor -type f -perm +111 | head -1)";; esac
```

- [ ] **Step 4: Smoke-test the CLI**

```bash
src/Kapacitor.Cli/bin/Release/net10.0/*/publish/kapacitor plugin --help
```

Expected: prints the new help text from Task 10.

- [ ] **Step 5: No commit** (this task is verification only). If any fix was required, commit it separately with a descriptive message.

---

## Task 12: Update `../kapacitor-web` docs

Per the user's auto-memory, public CLI docs live at `../kapacitor-web` (Astro Starlight). The CLI surface changed (new `--skills` flag, new install path), so the web docs must be synced in the same PR-equivalent batch.

**Files (external repo):**
- `../kapacitor-web/src/content/docs/getting-started/*.md` (whichever covers `kapacitor plugin install`)
- `../kapacitor-web/src/content/docs/commands.md` (or per-command page for `plugin`)

- [ ] **Step 1: Locate the relevant pages**

```bash
grep -rln "plugin install\|\.codex/skills\|codex-skills" ../kapacitor-web/src/ 2>/dev/null
```

- [ ] **Step 2: Mirror the changes from `help-plugin.txt`**

Update each found file:
- Add `--skills` flag with description.
- Replace `~/.codex/skills/` install path with `~/.agents/skills/`.
- Update example invocations.
- If any page lists the five skill names with the `session-` prefix, update to the unprefixed form (`recap`, `errors`, etc. in the Claude context; `kapacitor-recap`, etc. in the install-target context).

- [ ] **Step 3: Build the docs site to confirm no broken links**

```bash
(cd ../kapacitor-web && npm run build)
```

Expected: build succeeds.

- [ ] **Step 4: Commit in the docs repo**

```bash
cd ../kapacitor-web
git add src/
git commit -m "Document --skills flag and ~/.agents/skills/ install path"
```

(Return to CLI repo afterwards: `cd -`.)

---

## Final verification

- [ ] **Run the full unit test suite**

```bash
dotnet run --project test/Kapacitor.Cli.Tests.Unit/Kapacitor.Cli.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Run integration tests**

```bash
dotnet run --project test/Kapacitor.Cli.Tests.Integration/Kapacitor.Cli.Tests.Integration.csproj
```

Expected: all tests pass.

- [ ] **Manual smoke test against a fresh fake `$HOME`**

```bash
FAKE_HOME=$(mktemp -d)
HOME=$FAKE_HOME src/Kapacitor.Cli/bin/Release/net10.0/*/publish/kapacitor plugin install --skills
ls "$FAKE_HOME/.agents/skills/"
# Expected: kapacitor-recap kapacitor-errors kapacitor-hide kapacitor-disable kapacitor-validate-plan
cat "$FAKE_HOME/.agents/skills/kapacitor-recap/SKILL.md" | head -5
# Expected: frontmatter begins with `name: kapacitor-recap`
HOME=$FAKE_HOME src/Kapacitor.Cli/bin/Release/net10.0/*/publish/kapacitor plugin remove --skills
ls "$FAKE_HOME/.agents/skills/" 2>&1
# Expected: directory empty (or removed if cleanup removed empty parent)
rm -rf "$FAKE_HOME"
```

- [ ] **Confirm no banned strings remain in shipped SKILL.md files**

```bash
for f in kapacitor/skills/*/SKILL.md; do
  bad=$(grep -nE "KAPACITOR_SESSION_ID|CODEX_THREAD_ID|\bClaude\b|\bCodex\b|\bCursor\b" "$f" || true)
  if [ -n "$bad" ]; then echo "=== $f ==="; echo "$bad"; fi
done
```

Expected: empty output.

- [ ] **Open the PR**

Use `gh pr create` with title `[AI-698] Install kapacitor skills to ~/.agents/skills/` and a body summarising the wire change + linking to the design spec.
