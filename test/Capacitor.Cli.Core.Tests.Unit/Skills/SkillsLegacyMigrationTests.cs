using System.Text.Json;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsLegacyMigrationTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static SkillsIdentity Id(string account) => new(account, "https://s");

    void WriteLegacy(string repoHash, string target, string account, params string[] paths) {
        var dir = Tmp.CreateDir($"config/skills/{repoHash}/{target}");
        var manifest = new SkillsManifest {
            Identity = Id(account),
            Skills = [.. paths.Select(p => new SkillsManifestEntry {
                DocId = Guid.NewGuid(), Slug = Path.GetFileName(p)[5..], Version = 1,
                ContentHash = "h", Path = p, FileHash = "f",
            })],
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest));
    }

    [Test]
    public async Task A_path_another_repository_still_owns_is_kept() {
        var shared = Tmp.PathTo("global/kcap-shared");
        var mine   = Tmp.PathTo("global/kcap-mine");
        WriteLegacy("aaaa", "agents", "acct-1", shared, mine);
        WriteLegacy("bbbb", "agents", "acct-1", shared);

        var plan = SkillsLegacyMigration.Plan(Tmp.GetResolvedPath("config"), "aaaa", "agents", Id("acct-1"));

        await Assert.That(plan.Delete).IsEquivalentTo([mine]);
        await Assert.That(plan.Keep).IsEquivalentTo([shared]);
    }

    [Test]
    public async Task A_path_whose_every_owner_is_retired_is_deleted() {
        var shared = Tmp.PathTo("global/kcap-shared");
        WriteLegacy("aaaa", "agents", "acct-1", shared);
        WriteLegacy("bbbb", "agents", "acct-1", shared);

        // Both owners are on the retired account, so nobody serves it under a live credential.
        var plan = SkillsLegacyMigration.Plan(Tmp.GetResolvedPath("config"), "aaaa", "agents", Id("acct-2"));

        await Assert.That(plan.Delete).IsEquivalentTo([shared]);
        await Assert.That(plan.Keep).IsEmpty();
    }

    [Test]
    public async Task A_path_a_live_other_account_owns_survives_a_retirement() {
        var shared = Tmp.PathTo("global/kcap-shared");
        WriteLegacy("aaaa", "agents", "acct-1", shared);
        WriteLegacy("bbbb", "agents", "acct-2", shared);

        var plan = SkillsLegacyMigration.Plan(Tmp.GetResolvedPath("config"), "aaaa", "agents", Id("acct-2"));

        await Assert.That(plan.Keep).IsEquivalentTo([shared]);
    }

    [Test]
    public async Task A_path_is_kept_when_a_sibling_manifest_fails_to_parse() {
        var shared = Tmp.PathTo("global/kcap-shared");
        WriteLegacy("aaaa", "agents", "acct-1", shared);
        var broken = Tmp.CreateDir("config/skills/bbbb/agents");
        File.WriteAllText(Path.Combine(broken, "manifest.json"), "not json");

        var plan = SkillsLegacyMigration.Plan(Tmp.GetResolvedPath("config"), "aaaa", "agents", Id("acct-1"));

        await Assert.That(plan.Keep).IsEquivalentTo([shared]);
        await Assert.That(plan.Delete).IsEmpty();
    }
}
