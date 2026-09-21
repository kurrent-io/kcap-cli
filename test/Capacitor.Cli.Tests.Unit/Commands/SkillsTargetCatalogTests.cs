using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SkillsTargetCatalogTests {
    // Rooted but never touched: every assertion here is on path arithmetic.
    static readonly string Root = Path.DirectorySeparatorChar.ToString();

    static readonly UserHome Home = new(Path.Combine(Root, "home", "u"));

    static readonly string KiroHome   = Path.Combine(Root, "srv", "kiro");
    static readonly string GeminiHome = Path.Combine(Root, "srv", "gemini");

    static readonly LegacySkillsRoots Legacy = new(Home, KiroHome, GeminiHome);

    static IReadOnlyList<SkillsTarget> Targets() => SkillsCommand.Targets(Legacy);

    [Test]
    public async Task Every_target_is_anchor_relative_and_leafed_skills() {
        var anchor = Path.Combine(Root, "anchor");

        foreach (var t in Targets()) {
            await Assert.That(Path.IsPathRooted(t.RelativePath)).IsFalse();
            await Assert.That(Path.GetFileName(t.RelativePath)).IsEqualTo("skills");
            await Assert.That(t.Root(anchor)).IsEqualTo(Path.Combine(anchor, t.RelativePath));
        }
    }

    [Test]
    public async Task The_catalogue_matches_the_measured_roots_and_vendors() {
        var byKey = Targets().ToDictionary(t => t.Key);

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

    /// <summary>The only root a legacy retirement may delete from, so it has to be the tree the
    /// copies were actually written into. Kiro's and Gemini's move with a supported environment
    /// override, which is why neither can be derived from the repository-relative tree.</summary>
    [Test]
    public async Task Every_targets_legacy_root_is_the_vendors_own_global_tree() {
        var byKey = Targets().ToDictionary(t => t.Key);

        await Assert.That(byKey["agents"].LegacyRoot).IsEqualTo(new AgentsPaths(Home).UserSkillsDir);
        await Assert.That(byKey["claude"].LegacyRoot)
            .IsEqualTo(new ClaudePaths(Home, null).UserSkillsDir);
        await Assert.That(byKey["kiro"].LegacyRoot).IsEqualTo(new KiroPaths(Home, KiroHome).SkillsDir);
        await Assert.That(byKey["gemini"].LegacyRoot)
            .IsEqualTo(new AntigravityPaths(Home, GeminiHome).SkillsDir);

        // The overridden pair is exactly what the home-relative form cannot express.
        await Assert.That(byKey["kiro"].LegacyRoot).IsNotEqualTo(byKey["kiro"].Root(Home.Path));
        await Assert.That(byKey["gemini"].LegacyRoot).IsNotEqualTo(byKey["gemini"].Root(Home.Path));
    }

    /// <summary>With no override set the two coincide with the home-relative tree, which is what
    /// makes the pair above the only place the difference shows.</summary>
    [Test]
    public async Task A_bare_environment_leaves_every_legacy_root_under_the_home() {
        var byKey = SkillsCommand.Targets(new LegacySkillsRoots(Home, null, null))
            .ToDictionary(t => t.Key);

        foreach (var (key, target) in byKey)
            await Assert.That(target.LegacyRoot).IsEqualTo(target.Root(Home.Path)).Because(key);
    }

    [Test]
    public async Task Adoption_follows_consumers_so_an_unmeasured_tree_is_still_served() {
        var byKey = Targets().ToDictionary(t => t.Key);
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
