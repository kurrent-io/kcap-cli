using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SkillsTargetCatalogTests {
    [Test]
    public async Task Every_target_is_anchor_relative_and_leafed_skills() {
        foreach (var t in SkillsCommand.Targets()) {
            await Assert.That(Path.IsPathRooted(t.RelativePath)).IsFalse();
            await Assert.That(Path.GetFileName(t.RelativePath)).IsEqualTo("skills");
            await Assert.That(t.Root("/anchor")).IsEqualTo(Path.Combine("/anchor", t.RelativePath));
        }
    }

    [Test]
    public async Task The_catalogue_matches_the_measured_roots_and_vendors() {
        var byKey = SkillsCommand.Targets().ToDictionary(t => t.Key);

        await Assert.That(byKey["agents"].RelativePath).IsEqualTo(Path.Combine(".agents", "skills"));
        await Assert.That(byKey["claude"].RelativePath).IsEqualTo(Path.Combine(".claude", "skills"));
        await Assert.That(byKey["kiro"].RelativePath).IsEqualTo(Path.Combine(".kiro", "skills"));
        await Assert.That(byKey["gemini"].RelativePath).IsEqualTo(Path.Combine(".gemini", "skills"));

        await Assert.That(byKey["agents"].Vendor).IsNull();
        await Assert.That(byKey["claude"].Vendor).IsEqualTo("claude");
        await Assert.That(byKey["kiro"].Vendor).IsEqualTo("kiro");
        await Assert.That(byKey["gemini"].Vendor).IsNull();

        // Measured in the probe matrix; the exposure a manifest records.
        await Assert.That(byKey["claude"].Readers)
            .IsEquivalentTo([HarnessId.Claude, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode]);
        await Assert.That(byKey["gemini"].Readers).IsEmpty();
    }

    [Test]
    public async Task Adoption_follows_consumers_so_an_unmeasured_tree_is_still_served() {
        var byKey = SkillsCommand.Targets().ToDictionary(t => t.Key);
        var gemini = new HarnessRegistryStub(HarnessId.Gemini);
        var antigravity = new HarnessRegistryStub(HarnessId.Antigravity);

        await Assert.That(SkillsCommand.Adopted(gemini, byKey["gemini"], false, false)).IsTrue();
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["agents"], false, false)).IsTrue();
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["claude"], false, false)).IsFalse();
        // The tree Antigravity was measured reading is the shared one, so it adopts no vendored
        // tree with no reader.
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["gemini"], false, false)).IsFalse();
        // A target kcap already owns keeps reconciling so a revocation still reaches it.
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["claude"], true, false)).IsTrue();
        await Assert.That(SkillsCommand.Adopted(antigravity, byKey["claude"], false, true)).IsTrue();
    }
}
