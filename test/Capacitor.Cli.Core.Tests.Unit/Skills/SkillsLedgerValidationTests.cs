using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

/// <summary>The five states admit exactly one set of field combinations. Anything else is a corrupt
/// row, so nothing can act on it.</summary>
public class SkillsLedgerValidationTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static SkillReceipt Receipt(string hash = "f") => new() {
        FileHash = hash,
        Document = new SkillDocument { DocId = Guid.Empty, Slug = "x", Version = 1, ContentHash = "h" },
    };

    static PreparedSkillWrite Operation() => new() {
        Operation = Guid.NewGuid(), Intended = Receipt("g"),
        Identity = new SkillsIdentity("acct", "https://s"),
    };

    static OwnedSkillRow Row(OwnedSkillState state) => new() {
        Path = "/repo/.claude/skills/kcap-x", Root = "/repo/.claude/skills", Anchor = "/repo",
        Origin = SkillOrigin.Repository, State = state,
    };

    [Test]
    public async Task The_legal_combinations_are_accepted() {
        OwnedSkillRow[] legal = [
            Row(OwnedSkillState.Reserved),
            Row(OwnedSkillState.Reserved) with { Prepared = Operation() },
            Row(OwnedSkillState.Published) with { Confirmed = Receipt() },
            Row(OwnedSkillState.Published) with { Confirmed = Receipt(), Prepared = Operation() },
            Row(OwnedSkillState.Unverified),
            Row(OwnedSkillState.Owed) with { Cause = SkillDeletionCause.Revoked, Confirmed = Receipt() },
            Row(OwnedSkillState.Owed) with { Cause = SkillDeletionCause.Superseded },
            Row(OwnedSkillState.Owed) with {
                Cause = SkillDeletionCause.Retired, Confirmed = Receipt(),
                IdentityRetired = new SkillsIdentity("old", "https://s"),
            },
            Row(OwnedSkillState.Settled),
        ];

        foreach (var row in legal)
            await Assert.That(SkillsLedgerValidation.Reject(row)).IsNull();
    }

    [Test]
    public async Task Every_illegal_combination_is_refused() {
        OwnedSkillRow[] illegal = [
            Row(OwnedSkillState.Reserved) with { Confirmed = Receipt() },
            Row(OwnedSkillState.Published),
            Row(OwnedSkillState.Unverified) with { Confirmed = Receipt() },
            Row(OwnedSkillState.Unverified) with { Prepared = Operation() },
            Row(OwnedSkillState.Settled) with { Confirmed = Receipt() },
            Row(OwnedSkillState.Owed) with { Confirmed = Receipt() },
            Row(OwnedSkillState.Published) with { Confirmed = Receipt(), Cause = SkillDeletionCause.Revoked },
            Row(OwnedSkillState.Owed) with { Cause = SkillDeletionCause.Retired, Confirmed = Receipt() },
            Row(OwnedSkillState.Owed) with {
                Cause = SkillDeletionCause.Revoked, Confirmed = Receipt(),
                IdentityRetired = new SkillsIdentity("old", "https://s"),
            },
            Row(OwnedSkillState.Unverified) with { Inherited = [Receipt()] },
            Row(OwnedSkillState.Published) with { Confirmed = Receipt(), Origin = SkillOrigin.Legacy },
        ];

        foreach (var row in illegal)
            await Assert.That(SkillsLedgerValidation.Reject(row)).IsNotNull();
    }

    /// <summary>The converter admits a number for any of these, so a value outside the table
    /// reaches the row. Nothing may fall through a shape rule's default branch and be acted on as a
    /// state that is inside it.</summary>
    [Test]
    public async Task A_value_outside_the_table_is_refused_rather_than_defaulted() {
        OwnedSkillRow[] undefined = [
            Row((OwnedSkillState)99),
            Row(OwnedSkillState.Published) with { Confirmed = Receipt(), Origin = (SkillOrigin)7 },
            Row(OwnedSkillState.Owed) with { Confirmed = Receipt(), Cause = (SkillDeletionCause)99 },
        ];

        foreach (var row in undefined)
            await Assert.That(SkillsLedgerValidation.Reject(row)).IsNotNull();
    }

    /// <summary>A corrupt row is carried into the next save untouched: dropping it would leave the
    /// directory it names with nothing able to remove it.</summary>
    [Test]
    public async Task A_corrupt_row_is_reported_held_aside_and_never_lost() {
        List<string> reasons = [];
        var ledger = new SkillsLedger {
            Owned = [Row(OwnedSkillState.Published), Row(OwnedSkillState.Settled) with { Path = Tmp.Path }],
        };

        var rows = OwnedSkillRows.Adopt(ledger, (_, reason) => reasons.Add(reason));

        await Assert.That(reasons.Count).IsEqualTo(1);
        await Assert.That(rows.Live.Count()).IsEqualTo(1);
        await Assert.That(rows.All.Length).IsEqualTo(2);
    }

    /// <summary>Two rows resolving to one location are one row, and the second is not a row this run
    /// acts on.</summary>
    [Test]
    public async Task A_second_row_for_one_location_is_held_aside() {
        var dir    = Tmp.CreateDir("repo", ".claude", "skills", "kcap-x");
        var alias  = Tmp.PathTo("repo", ".claude", "skills", "..", "skills", "kcap-x");
        var ledger = new SkillsLedger {
            Owned = [
                Row(OwnedSkillState.Settled) with { Path = dir },
                Row(OwnedSkillState.Settled) with { Path = alias },
            ],
        };

        var rows = OwnedSkillRows.Adopt(ledger, (_, _) => { });

        await Assert.That(rows.Live.Count()).IsEqualTo(1);
        await Assert.That(rows.All.Length).IsEqualTo(2);
    }
}
