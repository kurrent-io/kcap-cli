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
                                 SkillsMaterializer.Encode(SkillsRendering.RenderSkillFile(item)));

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

    /// <summary>The temporary carries random bytes and is created exclusively, so a link planted at
    /// the name a write might have used is neither followed nor removed.</summary>
    [Test]
    public async Task A_symlink_planted_at_the_predictable_temp_name_is_left_alone() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo/.agents/skills");
        var dir    = SkillsMaterializer.SkillDirFor(root, "x");
        Directory.CreateDirectory(dir);
        var outsideFile = Tmp.CreateFile("global/secret.txt", "outside content");
        var planted     = SkillsMaterializer.SkillFileFor(dir) + ".tmp";
        File.CreateSymbolicLink(planted, outsideFile);

        Write(root, anchor, Item("x"));

        await Assert.That(File.ReadAllText(outsideFile)).IsEqualTo("outside content");
        await Assert.That(new FileInfo(planted).LinkTarget).IsNotNull();
        await Assert.That(new FileInfo(SkillsMaterializer.SkillFileFor(dir)).LinkTarget).IsNull();
    }

    /// <summary>A write owns the file it creates and nothing else. An authored file that happens to
    /// carry the name a temporary might have taken has no receipt, so deleting it would be the one
    /// thing the whole record forbids.</summary>
    [Test]
    public async Task An_authored_file_at_the_temp_name_survives_a_write() {
        var anchor   = Tmp.CreateDir("repo");
        var root     = Tmp.CreateDir("repo/.agents/skills");
        var item     = Item("x");
        var dir      = SkillsMaterializer.SkillDirFor(root, item.Slug);
        Directory.CreateDirectory(dir);
        var authored = new TempDirHandle(dir).CreateFile("SKILL.md.tmp", "notes the author keeps here");

        await Assert.That(Write(root, anchor, item)).IsTrue();

        await Assert.That(File.ReadAllText(authored)).IsEqualTo("notes the author keeps here");
        await Assert.That(File.ReadAllText(SkillsMaterializer.SkillFileFor(dir)))
            .IsEqualTo(SkillsRendering.RenderSkillFile(item));
        // Nothing of the write's own is left beside the file it published.
        await Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>())
            .IsEquivalentTo(["SKILL.md", "SKILL.md.tmp"]);
    }

    /// <summary>A directory that cannot be searched answers "not found" for everything inside it.
    /// Reading that as absence lets relocation accept matching bytes elsewhere as the same copy
    /// moved, and a later revocation then deletes an independent one.</summary>
    [Test]
    public async Task A_file_that_cannot_be_reached_is_not_absence() {
        Skip.When(OperatingSystem.IsWindows(), "file modes are the mechanism this inspects");

        var root = Tmp.CreateDir("repo", ".agents", "skills");
        var item = Item("x");

        Write(root, root, item);

        var dir = SkillsMaterializer.SkillDirFor(root, item.Slug);

        // Listable but not searchable: the entry is there and nothing about it can be answered.
        Mode(dir, UnixFileMode.UserRead);

        try {
            Skip.When(new FileInfo(SkillsMaterializer.SkillFileFor(dir)).Exists,
                      "this user is not subject to the directory's mode");

            await Assert.That(SkillsMaterializer.Inspect(dir).Probe).IsEqualTo(SkillFileProbe.Unreadable);
        } finally {
            Mode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(SkillsMaterializer.Inspect(dir).Probe).IsEqualTo(SkillFileProbe.Present);
    }

    /// <summary>A publication that could not be renamed into place takes its own temporary with it,
    /// and leaves nothing else behind.</summary>
    [Test]
    public async Task A_failed_publication_leaves_no_temporary_behind() {
        var holder = Tmp.CreateDir("holder");
        var target = holder.PathTo("target");

        // A directory where the file has to go: the rename cannot happen, and the create did.
        Directory.CreateDirectory(target);

        try {
            AtomicFile.Replace(target, "content");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The failure is the point; what it leaves behind is what this pins.
        }

        await Assert.That(Directory.GetFiles(holder)).IsEmpty();
    }

    static void Mode(string path, UnixFileMode mode) {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }
}
