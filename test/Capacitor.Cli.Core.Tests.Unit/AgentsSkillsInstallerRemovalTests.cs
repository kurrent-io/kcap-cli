namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Removal deletes only the skills kcap installed as it installed them: a bundled folder the user
/// edited or added to stays, and a removal after that still knows it is theirs.
/// </summary>
public class AgentsSkillsInstallerRemovalTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Installed() {
        var target = Tmp.PathTo("skills");
        if (!AgentsSkillsInstaller.Install(RepoTree.SkillsSource(), target))
            throw new InvalidOperationException("the shipped skills did not install");

        return target;
    }

    [Test]
    public async Task Removes_every_skill_it_installed_untouched() {
        var target = Installed();

        var result = AgentsSkillsInstaller.Remove(target);

        await Assert.That(result.RemovedAny).IsTrue();
        await Assert.That(result.Preserved).IsEmpty();
        foreach (var name in AgentsSkillsInstaller.SourceNames)
            await Assert.That(Directory.Exists(Path.Combine(target, "kcap-" + name))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(target, SkillsFileManifest.FileName))).IsFalse();
    }

    [Test]
    public async Task Keeps_a_skill_the_user_edited() {
        var target = Installed();
        var edited = Path.Combine(target, "kcap-recap", "SKILL.md");
        await File.AppendAllTextAsync(edited, "\nMy own note.\n");

        var result = AgentsSkillsInstaller.Remove(target);

        await Assert.That(result.Preserved).IsEquivalentTo(new[] { "kcap-recap" });
        await Assert.That(await File.ReadAllTextAsync(edited)).Contains("My own note.");
        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-errors"))).IsFalse();
    }

    [Test]
    public async Task Keeps_a_skill_the_user_added_a_file_to() {
        var target = Installed();
        var added  = Path.Combine(target, "kcap-errors", "notes.md");
        await File.WriteAllTextAsync(added, "mine");

        var result = AgentsSkillsInstaller.Remove(target);

        await Assert.That(result.Preserved).IsEquivalentTo(new[] { "kcap-errors" });
        await Assert.That(File.Exists(added)).IsTrue();
    }

    [Test]
    public async Task Never_touches_a_folder_the_user_wrote() {
        var target = Installed();
        var mine   = Path.Combine(target, "my-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        await File.WriteAllTextAsync(mine, "mine");

        AgentsSkillsInstaller.Remove(target);

        await Assert.That(File.Exists(mine)).IsTrue();
    }

    /// <summary>The kept folder must not read as an install, or the next upgrade's refresh would copy
    /// every skill back over the user's edit.</summary>
    [Test]
    public async Task A_kept_edit_is_not_an_install_and_stays_through_a_second_removal() {
        var target = Installed();
        var edited = Path.Combine(target, "kcap-hide", "SKILL.md");
        await File.WriteAllTextAsync(edited, "rewritten by hand");

        AgentsSkillsInstaller.Remove(target);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(target)).IsFalse();
        await Assert.That(AgentsSkillsInstaller.IsUserEdited(target, "kcap-hide")).IsTrue();

        var again = AgentsSkillsInstaller.Remove(target);

        await Assert.That(again.RemovedAny).IsFalse();
        await Assert.That(again.HadErrors).IsFalse();
        await Assert.That(again.Preserved).IsEquivalentTo(new[] { "kcap-hide" });
        await Assert.That(await File.ReadAllTextAsync(edited)).IsEqualTo("rewritten by hand");
    }

    [Test]
    public async Task An_unreadable_manifest_proves_nothing_so_nothing_is_deleted() {
        var target = Installed();
        await File.WriteAllTextAsync(Path.Combine(target, SkillsFileManifest.FileName), "{ not json");

        var result = AgentsSkillsInstaller.Remove(target);

        await Assert.That(result.RemovedAny).IsFalse();
        foreach (var name in AgentsSkillsInstaller.SourceNames)
            await Assert.That(Directory.Exists(Path.Combine(target, "kcap-" + name))).IsTrue();
    }

    [Test]
    public async Task A_pristine_skill_is_not_user_edited() {
        var target = Installed();

        await Assert.That(AgentsSkillsInstaller.IsUserEdited(target, "kcap-recap")).IsFalse();
        await Assert.That(AgentsSkillsInstaller.IsUserEdited(target, "kcap-not-a-bundled-skill")).IsFalse();
    }
}
