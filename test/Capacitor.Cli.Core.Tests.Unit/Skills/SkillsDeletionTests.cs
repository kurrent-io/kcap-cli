using System.Text;
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

    /// <summary>Read and matched against everything the row holds is an answer, and the only
    /// outcome that may later cost a row its evidence.</summary>
    [Test]
    public async Task A_file_no_receipt_accounts_for_is_unvouched_and_left_alone() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");
        var file   = dir.CreateFile("SKILL.md", "somebody else's");

        var result = SkillsDeletion.Delete(Row(dir, root, anchor, SkillsMaterializer.FileHash(Body)),
                                           new SkillAuthority(root, anchor));

        await Assert.That(result).IsEqualTo(SkillDeletionResult.Unvouched);
        await Assert.That(File.ReadAllText(file)).IsEqualTo("somebody else's");
    }

    /// <summary>A file that could not be read, or a link where the managed file should be,
    /// establishes nothing at all — which is a different answer from one that was read and matched
    /// nothing.</summary>
    [Test]
    public async Task A_file_that_cannot_be_read_is_refused_rather_than_unvouched() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");

        File.CreateSymbolicLink(SkillsMaterializer.SkillFileFor(dir), Tmp.CreateFile("theirs.md", Body));

        await Assert.That(SkillsDeletion.Delete(Row(dir, root, anchor, SkillsMaterializer.FileHash(Body)),
                                                new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Refused);
        await Assert.That(File.Exists(SkillsMaterializer.SkillFileFor(dir))).IsTrue();
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
            .IsEqualTo(SkillDeletionResult.Unvouched);
        await Assert.That(File.Exists(file)).IsTrue();
    }

    /// <summary>A receipt is over the bytes, not over the characters they decode to. A file
    /// re-encoded, or given a byte-order mark, reads back as the same text and must not be taken
    /// for the one kcap wrote.</summary>
    [Test]
    public async Task A_re_encoded_file_does_not_match_the_receipt_it_decodes_to() {
        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");
        var file   = SkillsMaterializer.SkillFileFor(dir);
        var row    = Row(dir, root, anchor, SkillsMaterializer.FileHash(Body));

        File.WriteAllText(file, Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await Assert.That(SkillsDeletion.Delete(row, new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Unvouched);

        File.WriteAllText(file, Body, Encoding.Unicode);

        await Assert.That(SkillsDeletion.Delete(row, new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Unvouched);

        File.WriteAllBytes(file, SkillsMaterializer.Encode(Body));

        await Assert.That(SkillsDeletion.Delete(row, new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Removed);
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

    /// <summary>A directory that cannot be searched answers "not found" for the managed file while
    /// still listing its name. Reading that as absence settles the row and discharges the receipts
    /// that were the only way to delete the file still sitting there.</summary>
    [Test]
    public async Task A_managed_file_that_cannot_be_answered_for_neither_settles_nor_deletes() {
        Skip.When(OperatingSystem.IsWindows(), "file modes are the mechanism this inspects");

        var anchor = Tmp.CreateDir("repo");
        var root   = Tmp.CreateDir("repo", ".agents", "skills");
        var dir    = root.CreateDir("kcap-x");
        var file   = dir.CreateFile("SKILL.md", Body);
        var row    = Row(dir, root, anchor, SkillsMaterializer.FileHash(Body));

        Mode(dir, UnixFileMode.UserRead);

        try {
            Skip.When(new FileInfo(file).Exists, "this user is not subject to the directory's mode");

            await Assert.That(SkillsDeletion.Delete(row, new SkillAuthority(root, anchor)))
                .IsEqualTo(SkillDeletionResult.Refused);
        } finally {
            Mode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(File.ReadAllText(file)).IsEqualTo(Body);
        await Assert.That(SkillsDeletion.Delete(row, new SkillAuthority(root, anchor)))
            .IsEqualTo(SkillDeletionResult.Removed);
    }

    static void Mode(string path, UnixFileMode mode) {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
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
