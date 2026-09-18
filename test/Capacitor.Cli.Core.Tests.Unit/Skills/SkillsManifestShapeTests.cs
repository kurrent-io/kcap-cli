using System.Text.Json;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsManifestShapeTests {
    [Test]
    public async Task A_manifest_round_trips_its_identity_exposure_and_journal() {
        var manifest = new SkillsManifest {
            Etag = "e1", SyncedAt = DateTimeOffset.UnixEpoch,
            Anchor = "/repo", Identity = new SkillsIdentity("acct-1", "https://server"),
            Exposure = ["claude", "copilot"], Pending = true,
            PendingPrunes = [new PendingPrune("/repo/.claude/skills/kcap-x", "/repo/.claude/skills")],
            Skills = [new SkillsManifestEntry {
                DocId = Guid.Empty, Slug = "x", Version = 1, ContentHash = "h", Path = "/repo/.claude/skills/kcap-x",
                FileHash = "f", Home = "repo:owner/name", Applicability = "vendor:claude",
            }],
        };

        var json  = JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest);
        var again = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.SkillsManifest)!;

        await Assert.That(again.Identity!.Account).IsEqualTo("acct-1");
        await Assert.That(again.Anchor).IsEqualTo("/repo");
        await Assert.That(again.Exposure).IsEquivalentTo(["claude", "copilot"]);
        await Assert.That(again.Pending).IsTrue();
        await Assert.That(again.PendingPrunes![0].Root).IsEqualTo("/repo/.claude/skills");
        await Assert.That(again.Skills![0].Home).IsEqualTo("repo:owner/name");
    }

    [Test]
    public async Task An_older_manifest_still_deserializes() {
        const string old = """
            {"etag":"e","skills":[{"doc_id":"00000000-0000-0000-0000-000000000000",
            "slug":"x","version":1,"content_hash":"h","path":"/p"}]}
            """;

        var manifest = JsonSerializer.Deserialize(old, CapacitorJsonContext.Default.SkillsManifest)!;

        // Missing identity reads as "no ledger" at the call site, not as a crash here.
        await Assert.That(manifest.Identity).IsNull();
        await Assert.That(manifest.PendingPrunes).IsNull();
        await Assert.That(manifest.Skills![0].Home).IsNull();
    }
}
