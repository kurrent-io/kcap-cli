using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

/// <summary>Writing a file and saving the ledger cannot be made atomic, so the rows alone have to
/// decide whether a write landed.</summary>
public class SkillsRecoveryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Wrote = "---\nname: x\ndescription: \"d\"\n---\n\nintended.\n";
    const string Was   = "---\nname: x\ndescription: \"d\"\n---\n\nprevious.\n";

    static SkillDocument Document => new() { DocId = Guid.Empty, Slug = "x", Version = 1, ContentHash = "h" };

    static SkillReceipt Receipt(string content) =>
        new() { FileHash = SkillsMaterializer.FileHash(content), Document = Document };

    OwnedSkillRow Row(string dir, SkillReceipt? confirmed) => new() {
        Path = dir, Root = Tmp.PathTo("repo", ".claude", "skills"), Anchor = Tmp.PathTo("repo"),
        Origin = SkillOrigin.Repository,
        State = confirmed is null ? OwnedSkillState.Reserved : OwnedSkillState.Published,
        Confirmed = confirmed,
        Prepared = new PreparedSkillWrite {
            Operation = Guid.NewGuid(), Intended = Receipt(Wrote),
            Identity = new SkillsIdentity("acct", "https://s"),
        },
    };

    string Destination(string anchor = "repo") =>
        Tmp.PathTo(anchor, ".claude", "skills", "kcap-x");

    [Test]
    public async Task A_file_matching_the_operation_completes_it() {
        var dir = Tmp.CreateDir("repo", ".claude", "skills", "kcap-x");

        dir.CreateFile("SKILL.md", Wrote);

        var recovery = SkillsRecovery.Resolve(Row(dir, null), travelled: null);

        await Assert.That(recovery.Outcome).IsEqualTo(SkillRecoveryOutcome.Landed);
        await Assert.That(recovery.Row.State).IsEqualTo(OwnedSkillState.Published);
        await Assert.That(recovery.Row.Prepared).IsNull();
        await Assert.That(recovery.Row.Confirmed!.FileHash).IsEqualTo(SkillsMaterializer.FileHash(Wrote));
    }

    [Test]
    public async Task An_absent_file_with_no_receipt_discards_the_operation_and_keeps_the_reservation() {
        var recovery = SkillsRecovery.Resolve(Row(Destination(), null), travelled: null);

        await Assert.That(recovery.Outcome).IsEqualTo(SkillRecoveryOutcome.Discarded);
        await Assert.That(recovery.Row.State).IsEqualTo(OwnedSkillState.Reserved);
        await Assert.That(recovery.Row.Prepared).IsNull();
        await Assert.That(recovery.Row.Confirmed).IsNull();
        await Assert.That(SkillsLedgerValidation.Reject(recovery.Row)).IsNull();
    }

    [Test]
    public async Task A_file_matching_the_previous_receipt_discards_the_operation() {
        var dir = Tmp.CreateDir("repo", ".claude", "skills", "kcap-x");

        dir.CreateFile("SKILL.md", Was);

        var recovery = SkillsRecovery.Resolve(Row(dir, Receipt(Was)), travelled: null);

        await Assert.That(recovery.Outcome).IsEqualTo(SkillRecoveryOutcome.Discarded);
        await Assert.That(recovery.Row.State).IsEqualTo(OwnedSkillState.Published);
        await Assert.That(recovery.Row.Confirmed!.FileHash).IsEqualTo(SkillsMaterializer.FileHash(Was));
    }

    /// <summary>Absence is not evidence that a third party wrote anything, so there is nothing to
    /// judge and the receipt stands.</summary>
    [Test]
    public async Task An_absent_file_with_a_receipt_leaves_the_receipt_alone() {
        var recovery = SkillsRecovery.Resolve(Row(Destination(), Receipt(Was)), travelled: null);

        await Assert.That(recovery.Outcome).IsEqualTo(SkillRecoveryOutcome.Discarded);
        await Assert.That(recovery.Row.Confirmed!.FileHash).IsEqualTo(SkillsMaterializer.FileHash(Was));
    }

    [Test]
    public async Task Bytes_matching_neither_are_refused_and_the_operation_stays_unresolved() {
        var dir = Tmp.CreateDir("repo", ".claude", "skills", "kcap-x");

        dir.CreateFile("SKILL.md", "somebody else's");

        var row      = Row(dir, Receipt(Was));
        var recovery = SkillsRecovery.Resolve(row, travelled: null);

        await Assert.That(recovery.Outcome).IsEqualTo(SkillRecoveryOutcome.Refused);
        await Assert.That(recovery.Row).IsEqualTo(row);
        await Assert.That(File.ReadAllText(SkillsMaterializer.SkillFileFor(dir))).IsEqualTo("somebody else's");
    }

    /// <summary>Prepare at one anchor, write, crash, and the checkout moves before the retry: the
    /// row still names a path whose file is genuinely absent, while the bytes sit at the path the
    /// operation named under the new anchor.</summary>
    [Test]
    public async Task A_write_that_landed_and_then_travelled_completes_where_the_bytes_are() {
        var moved = Tmp.CreateDir("moved", ".claude", "skills", "kcap-x");

        moved.CreateFile("SKILL.md", Wrote);

        var here     = new SkillDestination(moved, Tmp.PathTo("moved", ".claude", "skills"), Tmp.PathTo("moved"));
        var recovery = SkillsRecovery.Resolve(Row(Destination(), null), here);

        await Assert.That(recovery.Outcome).IsEqualTo(SkillRecoveryOutcome.Landed);
        await Assert.That(recovery.Row.Path).IsEqualTo(moved.Path);
        await Assert.That(recovery.Row.Anchor).IsEqualTo(Tmp.PathTo("moved"));
        await Assert.That(recovery.From).IsEqualTo(Destination());
    }

    /// <summary>Bounded to the one destination the operation itself named: a match anywhere else
    /// proves nothing and authorises nothing.</summary>
    [Test]
    public async Task A_match_at_some_other_path_completes_nothing() {
        var elsewhere = Tmp.CreateDir("elsewhere", ".claude", "skills", "kcap-x");

        elsewhere.CreateFile("SKILL.md", Wrote);

        var named    = new SkillDestination(Tmp.PathTo("moved", ".claude", "skills", "kcap-x"),
                                            Tmp.PathTo("moved", ".claude", "skills"), Tmp.PathTo("moved"));
        var recovery = SkillsRecovery.Resolve(Row(Destination(), null), named);

        await Assert.That(recovery.Outcome).IsEqualTo(SkillRecoveryOutcome.Discarded);
        await Assert.That(recovery.Row.Path).IsEqualTo(Destination());
    }

    /// <summary>Recording what was written never publishes, and never revives a row already
    /// retired.</summary>
    [Test]
    public async Task Completing_an_owed_row_keeps_the_cause_that_owes_it() {
        var dir = Tmp.CreateDir("repo", ".claude", "skills", "kcap-x");

        dir.CreateFile("SKILL.md", Wrote);

        var owed = Row(dir, null) with {
            State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Retired,
            IdentityRetired = new SkillsIdentity("old", "https://s"),
        };

        var recovery = SkillsRecovery.Resolve(owed, travelled: null);

        await Assert.That(recovery.Row.State).IsEqualTo(OwnedSkillState.Owed);
        await Assert.That(recovery.Row.Cause).IsEqualTo(SkillDeletionCause.Retired);
        await Assert.That(recovery.Row.Confirmed!.FileHash).IsEqualTo(SkillsMaterializer.FileHash(Wrote));
        await Assert.That(SkillsLedgerValidation.Reject(recovery.Row)).IsNull();
    }
}
