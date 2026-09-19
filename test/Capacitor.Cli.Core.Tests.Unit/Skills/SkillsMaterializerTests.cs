using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsMaterializerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static SkillSnapshotItem Item(string slug) => new() {
        DocId = Guid.NewGuid(), Slug = slug, Title = "T", Description = "When.", Body = "Body.",
        Version = 1, ContentHash = "h1",
    };

    [Test]
    public async Task Drift_detection_covers_missing_edited_and_untracked_files() {
        var root = Tmp.Path;
        var item = Item("retry-rules");
        SkillsMaterializer.Write(root, root, item);
        var dir      = SkillsMaterializer.SkillDirFor(root, item.Slug);
        var rendered = SkillsSyncPlanner.RenderSkillFile(item);
        var entry = new SkillsManifestEntry {
            DocId = item.DocId, Slug = item.Slug, Version = 1, ContentHash = "h1",
            Path = dir, FileHash = SkillsMaterializer.FileHash(rendered),
        };

        await Assert.That(SkillsMaterializer.HasDrifted(entry)).IsFalse();          // served as written
        File.AppendAllText(Path.Combine(dir, "SKILL.md"), "tampered");
        await Assert.That(SkillsMaterializer.HasDrifted(entry)).IsTrue();           // edited
        Directory.Delete(dir, recursive: true);
        await Assert.That(SkillsMaterializer.HasDrifted(entry)).IsTrue();           // deleted
        await Assert.That(SkillsMaterializer.HasDrifted(entry with { FileHash = null })).IsTrue();   // pre-hash manifest
    }

    [Test]
    public async Task Prune_deletes_only_direct_kcap_children_of_the_root() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo/skills");
        Tmp.CreateDir("repo/skills/kcap-mine");
        Tmp.CreateDir("repo/skills/user-owned/kcap-nested");
        var siblingChild = Tmp.CreateDir("repo/skills-backup/kcap-foo");
        var owned  = Path.Combine(root, "kcap-mine");
        var nested = Path.Combine(root, "user-owned", "kcap-nested");

        await Assert.That(SkillsMaterializer.Prune(root, anchor, owned)).IsTrue();
        await Assert.That(SkillsMaterializer.Prune(root, anchor, nested)).IsFalse();
        await Assert.That(SkillsMaterializer.Prune(root, anchor, siblingChild)).IsFalse();

        await Assert.That(Directory.Exists(owned)).IsFalse();
        await Assert.That(Directory.Exists(nested)).IsTrue();        // nested user dir untouched
        await Assert.That(Directory.Exists(siblingChild)).IsTrue();  // sibling root untouched
    }

    [Test]
    public async Task A_destination_linked_outside_the_anchor_is_refused() {
        var anchor  = Tmp.CreateDir("repo");
        var outside = Tmp.CreateDir("global/skills");
        var root    = Path.Combine(anchor, ".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.CreateSymbolicLink(root, outside);

        var written = SkillsMaterializer.Write(root, anchor, Item("x"));

        await Assert.That(written).IsFalse();
        await Assert.That(Directory.GetDirectories(outside)).IsEmpty();
    }

    [Test]
    public async Task A_publication_is_atomic() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo/.agents/skills");

        SkillsMaterializer.Write(root, anchor, Item("x"));

        var dir = SkillsMaterializer.SkillDirFor(root, "x");

        await Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>()).IsEquivalentTo(["SKILL.md"]);
    }

    /// <summary>The skills root is a link out of the anchor, and the anchor is deep enough to have
    /// exhausted a budget charged per component — which would leave the link unfollowed and the
    /// destination reading as inside.</summary>
    [Test]
    public async Task A_deep_anchor_does_not_buy_a_link_out_of_itself() {
        var anchor  = Tmp.CreateDir("repo");
        var outside = Tmp.CreateDir("global", "skills");
        var deep    = anchor.Nest(45);
        var root    = deep.CreateDir(".agents").PathTo("skills");

        Directory.CreateSymbolicLink(root, outside.Path);

        await Assert.That(Directory.Exists(deep)).IsTrue();
        await Assert.That(SkillsMaterializer.Write(root, anchor, Item("x"))).IsFalse();
        await Assert.That(Directory.GetDirectories(outside)).IsEmpty();
    }

    [Test]
    public async Task A_prune_outside_the_anchor_is_refused() {
        var anchor  = Tmp.CreateDir("repo");
        var outside = Tmp.CreateDir("global/skills");
        Tmp.CreateDir("global/skills/kcap-x");
        var root = Path.Combine(anchor, ".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.CreateSymbolicLink(root, outside);

        // The root itself is the link, so the lexical parent-equality and kcap- prefix checks both
        // pass on this path — only the resolved containment check can refuse it.
        var pruned = SkillsMaterializer.Prune(root, anchor, Path.Combine(root, "kcap-x"));

        await Assert.That(pruned).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(outside, "kcap-x"))).IsTrue();
    }

    [Test]
    public async Task A_symlink_planted_at_the_temp_name_is_not_published_through() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo/.agents/skills");
        var dir    = SkillsMaterializer.SkillDirFor(root, "x");
        Directory.CreateDirectory(dir);
        var outsideFile = Tmp.CreateFile("global/secret.txt", "outside content");
        File.CreateSymbolicLink(SkillsMaterializer.SkillFileFor(dir) + ".tmp", outsideFile);

        SkillsMaterializer.Write(root, anchor, Item("x"));

        await Assert.That(File.ReadAllText(outsideFile)).IsEqualTo("outside content");
        await Assert.That(new FileInfo(SkillsMaterializer.SkillFileFor(dir)).LinkTarget).IsNull();
    }
}

