using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

/// <summary>The floor: only <c>SKILL.md</c>, only what a receipt accounts for, then the directory
/// only if it is empty.</summary>
public class SkillsDeletionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Body = "---\nname: x\ndescription: \"d\"\n---\n\nBody.\n";

    static OwnedSkillRow Row(string path, string root, string anchor, string? fileHash) => new() {
        Path = path, Root = root, Anchor = anchor, Origin = SkillOrigin.Repository,
        State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Revoked,
        Confirmed = fileHash is null ? null : new SkillReceipt {
            FileHash = fileHash,
            Document = new SkillDocument { DocId = Guid.Empty, Slug = "x", Version = 1, ContentHash = "h" },
        },
    };

    [Test]
    public async Task Only_the_managed_file_goes_and_the_directory_follows_it_when_empty() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");

        dir.CreateFile("SKILL.md", Body);

        var result = SkillsDeletion.Delete(Row(dir, root, anchor, SkillsMaterializer.FileHash(Body)),
                                           new SkillAuthority(root, anchor));

        await Assert.That(result).IsEqualTo(SkillDeletionResult.Removed);
        await Assert.That(Directory.Exists(dir)).IsFalse();
    }

    /// <summary>A directory holding an authored file beside ours loses only ours, and the row then
    /// holds no claim.</summary>
    [Test]
    public async Task An_authored_file_beside_ours_keeps_the_directory_standing() {
        var anchor   = Tmp.CreateDir("repo");
        var root     = Tmp.CreateDir("repo", ".agents", "skills");
        var dir      = root.CreateDir("kcap-x");
        var authored = dir.CreateFile("notes.md", "mine");

        dir.CreateFile("SKILL.md", Body);

        var result = SkillsDeletion.Delete(Row(dir, root, anchor, SkillsMaterializer.FileHash(Body)),
                                           new SkillAuthority(root, anchor));

        await Assert.That(result).IsEqualTo(SkillDeletionResult.Settled);
        await Assert.That(File.Exists(SkillsMaterializer.SkillFileFor(dir))).IsFalse();
        await Assert.That(File.ReadAllText(authored)).IsEqualTo("mine");
    }

    [Test]
    public async Task A_file_no_receipt_accounts_for_is_refused_and_left_alone() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");
        var file   = dir.CreateFile("SKILL.md", "somebody else's");

        var result = SkillsDeletion.Delete(Row(dir, root, anchor, SkillsMaterializer.FileHash(Body)),
                                           new SkillAuthority(root, anchor));

        await Assert.That(result).IsEqualTo(SkillDeletionResult.Refused);
        await Assert.That(File.ReadAllText(file)).IsEqualTo("somebody else's");
    }

    /// <summary>A row that never landed a write has no receipt at all, so nothing there can be
    /// matched — the directory only goes if it is empty.</summary>
    [Test]
    public async Task A_row_with_no_receipt_deletes_no_bytes() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");
        var file   = dir.CreateFile("SKILL.md", Body);

        await Assert.That(SkillsDeletion.Delete(Row(dir, root, anchor, null), new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Refused);
        await Assert.That(File.Exists(file)).IsTrue();
    }

    /// <summary>Whichever receipt the file matches is the one the deletion uses: a co-owner's, when
    /// that owner is the one whose bytes are on disk.</summary>
    [Test]
    public async Task An_inherited_receipt_accounts_for_a_co_owners_bytes() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");

        dir.CreateFile("SKILL.md", Body);

        var mine    = Row(dir, root, anchor, "not-what-is-there");
        var theirs  = new SkillReceipt {
            FileHash = SkillsMaterializer.FileHash(Body), Document = mine.Confirmed!.Document,
        };

        await Assert.That(SkillsDeletion.Delete(mine with { Inherited = [theirs] },
                                                new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Removed);
        await Assert.That(Directory.Exists(dir)).IsFalse();
    }

    [Test]
    public async Task Nothing_but_a_direct_kcap_child_of_the_authorising_root_is_admitted() {
        var anchor  = Tmp.CreateDir("repo");
        var root    = Tmp.CreateDir("repo", "skills");
        var nested  = Tmp.CreateDir("repo", "skills", "user-owned", "kcap-nested");
        var sibling = Tmp.CreateDir("repo", "skills-backup", "kcap-foo");
        var plain   = Tmp.CreateDir("repo", "skills", "authored");

        foreach (var victim in (string[])[nested, sibling, plain])
            await Assert.That(SkillsDeletion.Delete(Row(victim, root, anchor, null),
                                                    new SkillAuthority(root, anchor)))
                .IsEqualTo(SkillDeletionResult.Refused);

        await Assert.That(Directory.Exists(nested)).IsTrue();
        await Assert.That(Directory.Exists(sibling)).IsTrue();
        await Assert.That(Directory.Exists(plain)).IsTrue();
    }

    /// <summary>The root itself is the link, so the lexical parent-equality and kcap- prefix checks
    /// both pass on this path — only the resolved containment check can refuse it.</summary>
    [Test]
    public async Task A_deletion_outside_the_anchor_is_refused() {
        var anchor  = Tmp.CreateDir("repo");
        var outside = Tmp.CreateDir("global", "skills");
        var root    = Path.Combine(anchor, ".agents", "skills");

        outside.CreateDir("kcap-x");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.CreateSymbolicLink(root, outside.Path);

        var victim = Path.Combine(root, "kcap-x");

        await Assert.That(SkillsDeletion.Delete(Row(victim, root, anchor, null), new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Refused);
        await Assert.That(Directory.Exists(outside.PathTo("kcap-x"))).IsTrue();
    }

    [Test]
    public async Task A_release_takes_an_empty_directory_and_leaves_a_used_one() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var empty  = root.CreateDir("kcap-empty");
        var used   = root.CreateDir("kcap-used");

        used.CreateFile("SKILL.md", "not ours");

        await Assert.That(SkillsDeletion.Release(Row(empty, root, anchor, null), new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Removed);
        await Assert.That(SkillsDeletion.Release(Row(used, root, anchor, null), new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Settled);

        await Assert.That(Directory.Exists(empty)).IsFalse();
        await Assert.That(File.ReadAllText(SkillsMaterializer.SkillFileFor(used))).IsEqualTo("not ours");
    }
}
