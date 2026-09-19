using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsReconcilerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly SkillsTarget Target =
        new("claude", Path.Combine(".claude", "skills"), "claude", [], []) { LegacyRoot = "/nowhere" };

    string Anchor => Tmp.PathTo("repo");

    static SkillSnapshotItem Item(string slug, int version = 1) => new() {
        DocId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(slug)).AsSpan(0, 16)),
        Slug = slug, Title = slug, Description = "When.", Body = $"# {slug}\n", Version = version,
        ContentHash = $"h-{slug}-{version}",
    };

    /// <summary>What a completed publication leaves: the file on disk and the row that owns it.
    /// </summary>
    OwnedSkillRow Publish(SkillSnapshotItem item) {
        var rendered = SkillsRendering.RenderSkillFile(item);
        var at       = SkillDestination.For(Target, Anchor, item.Slug);

        Directory.CreateDirectory(at.Path);
        File.WriteAllText(SkillsMaterializer.SkillFileFor(at.Path), rendered);

        return new OwnedSkillRow {
            Path = at.Path, Root = at.Root, Anchor = at.Anchor, Origin = SkillOrigin.Repository,
            State = OwnedSkillState.Published,
            Confirmed = new SkillReceipt {
                FileHash = SkillsMaterializer.FileHash(rendered), Document = SkillDocument.Of(item, "repo:o/n"),
            },
        };
    }

    static OwnedSkillRows Rows(params OwnedSkillRow[] rows) =>
        OwnedSkillRows.Adopt(new SkillsLedger { Owned = rows },
                             (_, reason) => throw new InvalidOperationException(reason));

    SkillsSyncPlan Plan(OwnedSkillRows rows, params SkillSnapshotItem[] snapshot) =>
        SkillsReconciler.Plan(rows, snapshot, Target, Anchor, new HashSet<Guid>());

    [Test]
    public async Task A_new_document_is_written_and_an_unchanged_one_left_alone() {
        var keep  = Item("keep");
        var fresh = Item("fresh");
        var plan  = Plan(Rows(Publish(keep)), keep, fresh);

        await Assert.That(plan.Writes.Select(w => w.Item.Slug)).IsEquivalentTo(["fresh"]);
        await Assert.That(plan.Unchanged.Select(u => u.Slug)).IsEquivalentTo(["keep"]);
        await Assert.That(plan.Refusals).IsEmpty();
    }

    [Test]
    public async Task A_reapproved_version_and_a_hand_edited_file_both_rewrite() {
        var one  = Item("one");
        var two  = Item("two");
        var rows = Rows(Publish(one), Publish(two));

        File.AppendAllText(SkillsMaterializer.SkillFileFor(SkillDestination.For(Target, Anchor, "two").Path),
                           "tampered");

        var plan = Plan(rows, Item("one", version: 2), two);

        await Assert.That(plan.Writes.Select(w => w.Item.Slug)).IsEquivalentTo(["one", "two"]);
        await Assert.That(plan.Unchanged).IsEmpty();
    }

    /// <summary>A served slug can name a directory the repository already has. Writing it would
    /// record ownership of a directory the repository may track.</summary>
    [Test]
    public async Task A_destination_no_row_owns_is_refused() {
        Tmp.CreateFile(["repo", ".claude", "skills", "kcap-theirs", "notes.md"], "committed");

        var plan = Plan(Rows(), Item("theirs"));

        await Assert.That(plan.Writes).IsEmpty();
        await Assert.That(plan.Refusals.Single().Reason).Contains("does not own");
    }

    /// <summary>A settled row records a directory that is not kcap's to remove, so it grants no
    /// authority over any future file at that path either.</summary>
    [Test]
    public async Task A_settled_row_refuses_the_destination_it_left_standing() {
        var settled = Publish(Item("left")) with { State = OwnedSkillState.Settled, Confirmed = null };
        var plan    = Plan(Rows(settled), Item("left"));

        await Assert.That(plan.Writes).IsEmpty();
        await Assert.That(plan.Refusals.Single().Reason).Contains("no claim");
    }

    /// <summary>Requesting a replacement never clears a retirement cause.</summary>
    [Test]
    public async Task A_retired_deletion_still_owed_refuses_the_replacement() {
        var owed = Publish(Item("gone")) with {
            State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Retired,
            IdentityRetired = new SkillsIdentity("old", "https://s"),
        };
        var plan = Plan(Rows(owed), Item("gone"));

        await Assert.That(plan.Writes).IsEmpty();
        await Assert.That(plan.Refusals.Single().Reason).Contains("retired account");
    }

    /// <summary>A reservation is not publication: the destination is ours, so the write goes ahead
    /// rather than being refused as somebody else's directory.</summary>
    [Test]
    public async Task A_reservation_is_retried_rather_than_refused() {
        var at = SkillDestination.For(Target, Anchor, "held");
        Directory.CreateDirectory(at.Path);

        var reserved = new OwnedSkillRow {
            Path = at.Path, Root = at.Root, Anchor = at.Anchor,
            Origin = SkillOrigin.Repository, State = OwnedSkillState.Reserved,
        };

        var plan = Plan(Rows(reserved), Item("held"));

        await Assert.That(plan.Writes.Single().At.Path).IsEqualTo(at.Path);
        await Assert.That(plan.Refusals).IsEmpty();
    }

    /// <summary>A row at another anchor never satisfies publication here, whatever it holds.
    /// </summary>
    [Test]
    public async Task A_row_recorded_at_another_anchor_does_not_satisfy_publication() {
        var item     = Item("moved");
        var elsewhere = Publish(item) with { Anchor = Tmp.PathTo("previous") };

        await Assert.That(SkillsReconciler.Serves(elsewhere, item, Anchor)).IsFalse();
        await Assert.That(SkillsReconciler.Serves(Publish(item), item, Anchor)).IsTrue();
    }

    [Test]
    public async Task A_document_the_snapshot_withholds_is_neither_written_nor_reported_again() {
        var held = Item("held");
        var plan = SkillsReconciler.Plan(Rows(), [held], Target, Anchor, new HashSet<Guid> { held.DocId });

        await Assert.That(plan.Writes).IsEmpty();
        await Assert.That(plan.Unchanged).IsEmpty();
        await Assert.That(plan.Refusals).IsEmpty();
    }
}
