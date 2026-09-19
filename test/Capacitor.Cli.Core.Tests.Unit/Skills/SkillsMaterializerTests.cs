using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsMaterializerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static SkillSnapshotItem Item(string slug) => new() {
        DocId = Guid.NewGuid(), Slug = slug, Title = "T", Description = "When.", Body = "Body.",
        Version = 1, ContentHash = "h1",
    };

    static bool Write(string root, string anchor, SkillSnapshotItem item) =>
        SkillsMaterializer.Write(SkillsMaterializer.SkillDirFor(root, item.Slug), anchor,
                                 SkillsRendering.RenderSkillFile(item));

    [Test]
    public async Task A_probe_tells_present_from_absent_from_unreadable() {
        var root = Tmp.Path;
        var item = Item("retry-rules");

        Write(root, root, item);

        var dir = SkillsMaterializer.SkillDirFor(root, item.Slug);

        await Assert.That(SkillsMaterializer.Inspect(dir))
            .IsEqualTo((SkillFileProbe.Present, SkillsMaterializer.FileHash(SkillsRendering.RenderSkillFile(item))));

        File.AppendAllText(SkillsMaterializer.SkillFileFor(dir), "tampered");

        var (edited, hash) = SkillsMaterializer.Inspect(dir);

        await Assert.That(edited).IsEqualTo(SkillFileProbe.Present);
        await Assert.That(hash).IsNotEqualTo(SkillsMaterializer.FileHash(SkillsRendering.RenderSkillFile(item)));

        File.Delete(SkillsMaterializer.SkillFileFor(dir));

        await Assert.That(SkillsMaterializer.Inspect(dir).Probe).IsEqualTo(SkillFileProbe.Absent);

        // A link is not readable evidence of anything kcap wrote, whatever it points at.
        File.CreateSymbolicLink(SkillsMaterializer.SkillFileFor(dir), Tmp.CreateFile("elsewhere.md", "theirs"));

        await Assert.That(SkillsMaterializer.Inspect(dir).Probe).IsEqualTo(SkillFileProbe.Unreadable);
    }

    [Test]
    public async Task A_destination_linked_outside_the_anchor_is_refused() {
        var anchor  = Tmp.CreateDir("repo");
        var outside = Tmp.CreateDir("global/skills");
        var root    = Path.Combine(anchor, ".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.CreateSymbolicLink(root, outside);

        var written = Write(root, anchor, Item("x"));

        await Assert.That(written).IsFalse();
        await Assert.That(Directory.GetDirectories(outside)).IsEmpty();
    }

    [Test]
    public async Task A_publication_is_atomic() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo/.agents/skills");

        Write(root, anchor, Item("x"));

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
        await Assert.That(Write(root, anchor, Item("x"))).IsFalse();
        await Assert.That(Directory.GetDirectories(outside)).IsEmpty();
    }

    [Test]
    public async Task A_symlink_planted_at_the_temp_name_is_not_published_through() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo/.agents/skills");
        var dir    = SkillsMaterializer.SkillDirFor(root, "x");
        Directory.CreateDirectory(dir);
        var outsideFile = Tmp.CreateFile("global/secret.txt", "outside content");
        File.CreateSymbolicLink(SkillsMaterializer.SkillFileFor(dir) + ".tmp", outsideFile);

        Write(root, anchor, Item("x"));

        await Assert.That(File.ReadAllText(outsideFile)).IsEqualTo("outside content");
        await Assert.That(new FileInfo(SkillsMaterializer.SkillFileFor(dir)).LinkTarget).IsNull();
    }
}
