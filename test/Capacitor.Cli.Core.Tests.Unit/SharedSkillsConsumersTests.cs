namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The shared <c>~/.agents/skills</c> tree goes with its last consumer, never with an earlier one, and
/// a tree installed before consumers were recorded keeps the integrations still configured.
/// </summary>
public class SharedSkillsConsumersTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly Func<IEnumerable<string>> NothingConfigured = () => [];

    string Dir => Tmp.PathTo("skills");

    void InstallFor(params string[] consumers) {
        if (!AgentsSkillsInstaller.Install(RepoTree.SkillsSource(), Dir))
            throw new InvalidOperationException("the shipped skills did not install");

        SharedSkillsConsumers.Write(Dir, consumers);
    }

    bool SkillsPresent => AgentsSkillsInstaller.SourceNames.All(n => AgentsSkillsInstaller.HasSkill(Dir, n));

    [Test]
    public async Task Removing_one_consumer_keeps_the_skills_for_the_other() {
        InstallFor("codex", "cursor");

        var release = SharedSkillsConsumers.Release(Dir, "codex", NothingConfigured);

        await Assert.That(release.Remaining).IsEquivalentTo(new[] { "cursor" });
        await Assert.That(release.Removal.RemovedAny).IsFalse();
        await Assert.That(SkillsPresent).IsTrue();
        await Assert.That(SharedSkillsConsumers.ReadRecorded(Dir)!).IsEquivalentTo(new[] { "cursor" });
    }

    [Test]
    [Arguments("codex", "cursor")]
    [Arguments("cursor", "codex")]
    public async Task The_last_consumer_takes_the_skills_whichever_order(string first, string second) {
        InstallFor("codex", "cursor");

        SharedSkillsConsumers.Release(Dir, first, NothingConfigured);
        await Assert.That(SkillsPresent).IsTrue();

        var last = SharedSkillsConsumers.Release(Dir, second, NothingConfigured);

        await Assert.That(last.Remaining).IsEmpty();
        await Assert.That(last.Removal.RemovedAny).IsTrue();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(Dir)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(Dir, SharedSkillsConsumers.FileName))).IsFalse();
    }

    [Test]
    public async Task Releasing_again_changes_nothing() {
        InstallFor("cursor");
        SharedSkillsConsumers.Release(Dir, "cursor", NothingConfigured);

        var again = SharedSkillsConsumers.Release(Dir, "cursor", NothingConfigured);

        await Assert.That(again.Remaining).IsEmpty();
        await Assert.That(again.Removal.RemovedAny).IsFalse();
        await Assert.That(again.Removal.HadErrors).IsFalse();
    }

    [Test]
    public async Task Releasing_a_consumer_that_never_used_them_keeps_them_for_those_that_do() {
        InstallFor("codex");

        var release = SharedSkillsConsumers.Release(Dir, "cursor", NothingConfigured);

        await Assert.That(release.Remaining).IsEquivalentTo(new[] { "codex" });
        await Assert.That(SkillsPresent).IsTrue();
    }

    [Test]
    public async Task Release_on_a_machine_without_the_tree_writes_nothing() {
        var release = SharedSkillsConsumers.Release(Dir, "cursor", () => ["codex"]);

        await Assert.That(release.Remaining).IsEmpty();
        await Assert.That(Directory.Exists(Dir)).IsFalse();
    }

    [Test]
    public async Task The_last_consumer_keeps_a_skill_the_user_edited_and_a_folder_they_wrote() {
        InstallFor("cursor");
        var edited = Path.Combine(Dir, "kcap-plans", "SKILL.md");
        await File.AppendAllTextAsync(edited, "\nmine\n");
        var own = Path.Combine(Dir, "my-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        await File.WriteAllTextAsync(own, "mine");

        var release = SharedSkillsConsumers.Release(Dir, "cursor", NothingConfigured);

        await Assert.That(release.Removal.Preserved).IsEquivalentTo(new[] { "kcap-plans" });
        await Assert.That(File.Exists(edited)).IsTrue();
        await Assert.That(File.Exists(own)).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(Dir, "kcap-recap"))).IsFalse();
    }

    /// <summary>A tree from a build that kept no record: the integrations still configured stand in for
    /// it, so removing one does not delete the skills another relies on.</summary>
    [Test]
    public async Task An_unrecorded_tree_keeps_the_skills_for_an_integration_still_configured() {
        InstallFor();
        File.Delete(Path.Combine(Dir, SharedSkillsConsumers.FileName));

        var release = SharedSkillsConsumers.Release(Dir, "codex", () => ["cursor"]);

        await Assert.That(release.Remaining).IsEquivalentTo(new[] { "cursor" });
        await Assert.That(SkillsPresent).IsTrue();
        await Assert.That(SharedSkillsConsumers.ReadRecorded(Dir)!).IsEquivalentTo(new[] { "cursor" });
    }

    [Test]
    public async Task An_unrecorded_tree_with_nobody_configured_is_removed() {
        InstallFor();
        File.Delete(Path.Combine(Dir, SharedSkillsConsumers.FileName));

        var release = SharedSkillsConsumers.Release(Dir, "codex", NothingConfigured);

        await Assert.That(release.Removal.RemovedAny).IsTrue();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(Dir)).IsFalse();
    }

    [Test]
    public async Task An_unreadable_record_falls_back_to_what_is_configured() {
        InstallFor("cursor");
        await File.WriteAllTextAsync(Path.Combine(Dir, SharedSkillsConsumers.FileName), "[not an object");

        await Assert.That(SharedSkillsConsumers.ReadRecorded(Dir)).IsNull();
        await Assert.That(SharedSkillsConsumers.Read(Dir, () => ["muse"])).IsEquivalentTo(new[] { "muse" });
    }

    [Test]
    public async Task Clear_removes_the_skills_whoever_still_uses_them() {
        InstallFor("codex", "cursor", SharedSkillsConsumers.Explicit);

        var removal = SharedSkillsConsumers.Clear(Dir);

        await Assert.That(removal.RemovedAny).IsTrue();
        await Assert.That(SharedSkillsConsumers.ReadRecorded(Dir)).IsNull();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(Dir)).IsFalse();
    }
}
