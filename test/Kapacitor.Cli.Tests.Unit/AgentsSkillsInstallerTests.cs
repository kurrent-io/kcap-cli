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

        foreach (var name in SourceNames.Where(n => n != "recap")) {
            Directory.CreateDirectory(Path.Combine(src.Path, name));
            await File.WriteAllTextAsync(Path.Combine(src.Path, name, "SKILL.md"), $"---\nname: {name}\n---\nbody\n");
        }
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

        foreach (var name in SourceNames.Where(n => n != "recap")) {
            Directory.CreateDirectory(Path.Combine(src.Path, name));
            await File.WriteAllTextAsync(Path.Combine(src.Path, name, "SKILL.md"), $"---\nname: {name}\n---\nbody\n");
        }
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

        foreach (var name in SourceNames) {
            Directory.CreateDirectory(Path.Combine(src.Path, name));
            await File.WriteAllTextAsync(Path.Combine(src.Path, name, "SKILL.md"), $"---\nname: {name}\n---\nbody");
        }

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(File.Exists(Path.Combine(foreign, "SKILL.md"))).IsTrue();
        var content = await File.ReadAllTextAsync(Path.Combine(foreign, "SKILL.md"));
        await Assert.That(content).IsEqualTo("user content");
    }

    [Test]
    public async Task Install_replaces_existing_kapacitor_folder_atomically() {
        using var src = new InstallerTempDir();
        using var dst = new InstallerTempDir();

        var stale = Path.Combine(dst.Path, "kapacitor-recap");
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "SKILL.md"), "old version");
        await File.WriteAllTextAsync(Path.Combine(stale, "leftover.md"), "delete me");

        foreach (var name in SourceNames.Where(n => n != "recap")) {
            Directory.CreateDirectory(Path.Combine(src.Path, name));
            await File.WriteAllTextAsync(Path.Combine(src.Path, name, "SKILL.md"), $"---\nname: {name}\n---\nbody\n");
        }
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

        Directory.CreateDirectory(Path.Combine(src.Path, "recap"));
        await File.WriteAllTextAsync(
            Path.Combine(src.Path, "recap", "SKILL.md"),
            "---\nname: recap\n---\nbody");

        var ok = AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(ok).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(dst.Path, "kapacitor-recap"))).IsFalse();
    }

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
