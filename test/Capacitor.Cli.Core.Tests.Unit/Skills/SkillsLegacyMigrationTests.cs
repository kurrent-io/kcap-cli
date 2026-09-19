using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsLegacyMigrationTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static SkillsIdentity Id(string account) => new(account, "https://s");

    string ConfigRoot => Tmp.GetResolvedPath("config");

    SkillsTarget Target => new("agents", Path.Combine(".agents", "skills"), null, [], []) {
        LegacyRoot = Tmp.GetResolvedPath("global"),
    };

    /// <summary>A global copy on disk plus the row one repository's legacy ledger owns it
    /// through.</summary>
    string Copy(string slug, string body) {
        Tmp.CreateFile(["global", "kcap-" + slug, "SKILL.md"], body);

        return Tmp.GetResolvedPath("global", "kcap-" + slug);
    }

    void WriteLegacy(string repoHash, string account, params (string Path, string Body)[] copies) =>
        SkillsLedgerFile.Save(
            SkillsLegacyMigration.ManifestPathFor(ConfigRoot, repoHash, "agents"),
            new SkillsLedger {
                Identity = account.Length == 0 ? null : Id(account),
                Owned = [.. copies.Select(c => new OwnedSkillRow {
                    Path = c.Path, Root = Tmp.GetResolvedPath("global"), Origin = SkillOrigin.Legacy,
                    State = OwnedSkillState.Published,
                    Confirmed = new SkillReceipt {
                        FileHash = SkillsMaterializer.FileHash(c.Body),
                        Document = new SkillDocument {
                            DocId = Guid.NewGuid(), Slug = Path.GetFileName(c.Path)[5..], Version = 1,
                            ContentHash = "h",
                        },
                    },
                })],
            });

    SkillsLedger? Legacy(string repoHash) =>
        SkillsLedgerFile.ReadQuietly(SkillsLegacyMigration.ManifestPathFor(ConfigRoot, repoHash, "agents"),
                                     SkillOrigin.Legacy);

    /// <summary>Ownership another live ledger also holds is given up rather than retained: the last
    /// owner out is what deletes the files, and a claim nobody drops leaves no last owner.</summary>
    [Test]
    public async Task A_path_another_repository_still_owns_is_relinquished_not_kept() {
        var shared = Copy("shared", "twice");
        var mine   = Copy("mine", "once");

        WriteLegacy("aaaa", "acct-1", (shared, "twice"), (mine, "once"));
        WriteLegacy("bbbb", "acct-1", (shared, "twice"));

        var plan = SkillsLegacyMigration.Plan(ConfigRoot, "aaaa", "agents", Id("acct-1"));

        await Assert.That(plan.Delete.Select(r => r.Path)).IsEquivalentTo([mine]);
        await Assert.That(plan.Relinquish.Select(h => h.Row.Path)).IsEquivalentTo([shared]);
    }

    /// <summary>Two ledgers can spell one physical directory differently, and a retirement that
    /// does not recognise the co-owner deletes a copy the other still serves.</summary>
    [Test]
    public async Task A_co_owner_spelling_the_same_directory_another_way_is_recognised() {
        var shared  = Copy("shared", "twice");
        var aliased = Tmp.GetResolvedPath("global") + Path.DirectorySeparatorChar
                    + Path.Combine("sub", "..", "kcap-shared");

        Tmp.CreateDir("global", "sub");
        WriteLegacy("aaaa", "acct-1", (shared, "twice"));
        WriteLegacy("bbbb", "acct-1", (aliased, "twice"));

        var plan = SkillsLegacyMigration.Plan(ConfigRoot, "aaaa", "agents", Id("acct-1"));

        await Assert.That(plan.Delete).IsEmpty();
        // The spelling this ledger recorded is what goes back into it, not the resolved form.
        await Assert.That(plan.Relinquish.Select(h => h.Row.Path)).IsEquivalentTo([shared]);
    }

    [Test]
    public async Task A_path_whose_every_owner_is_retired_is_deleted() {
        var shared = Copy("shared", "twice");

        WriteLegacy("aaaa", "acct-1", (shared, "twice"));
        WriteLegacy("bbbb", "acct-1", (shared, "twice"));

        // Both owners are on the retired account, so nobody serves it under a live credential.
        var plan = SkillsLegacyMigration.Plan(ConfigRoot, "aaaa", "agents", Id("acct-2"));

        await Assert.That(plan.Delete.Select(r => r.Path)).IsEquivalentTo([shared]);
        await Assert.That(plan.Relinquish).IsEmpty();
    }

    [Test]
    public async Task A_path_a_live_other_account_owns_survives_a_retirement() {
        var shared = Copy("shared", "twice");

        WriteLegacy("aaaa", "acct-1", (shared, "twice"));
        WriteLegacy("bbbb", "acct-2", (shared, "twice"));

        var plan = SkillsLegacyMigration.Plan(ConfigRoot, "aaaa", "agents", Id("acct-2"));

        await Assert.That(plan.Delete).IsEmpty();
        await Assert.That(plan.Relinquish.Select(h => h.Row.Path)).IsEquivalentTo([shared]);
    }

    /// <summary>The ledger is the only record of the directories it owns, so a copy of it that will
    /// not parse must not read as one owning nothing — which is what a caller would delete.</summary>
    [Test]
    public async Task A_ledger_of_its_own_that_will_not_parse_is_not_an_empty_one() {
        Tmp.CreateFile(["config", "skills", "aaaa", "agents", "manifest.json"], "{ truncated");

        var plan = SkillsLegacyMigration.Plan(ConfigRoot, "aaaa", "agents", Id("acct-1"));

        await Assert.That(plan.Actionable).IsFalse();
        await Assert.That(plan.Delete).IsEmpty();

        // A ledger that was never written owns nothing, which is a different answer.
        await Assert.That(SkillsLegacyMigration.Plan(ConfigRoot, "zzzz", "agents", Id("acct-1")).Actionable)
            .IsTrue();
    }

    [Test]
    public async Task Nothing_is_actionable_while_a_sibling_ledger_fails_to_parse() {
        var shared = Copy("shared", "twice");

        WriteLegacy("aaaa", "acct-1", (shared, "twice"));
        Tmp.CreateFile(["config", "skills", "bbbb", "agents", "manifest.json"], "not json");

        var plan = SkillsLegacyMigration.Plan(ConfigRoot, "aaaa", "agents", Id("acct-1"));

        await Assert.That(plan.Actionable).IsFalse();
        await Assert.That(plan.Delete).IsEmpty();
        await Assert.That(plan.Relinquish).IsEmpty();
    }

    /// <summary>Two owners of one path can hold different receipts, because whichever wrote last is
    /// the one the bytes match. Replacing a survivor's receipt with the leaver's loses the evidence
    /// exactly as often as keeping it does, so relinquishment merges: the survivor then deletes
    /// against whichever receipt the file actually matches.</summary>
    [Test]
    [Arguments("aaaa", "bbbb")]
    [Arguments("bbbb", "aaaa")]
    public async Task A_co_owner_hands_its_evidence_over_whichever_leaves_first(string first, string second) {
        var shared = Copy("shared", "what aaaa wrote");

        // Only aaaa's receipt accounts for the bytes; bbbb's is from an earlier rendering.
        WriteLegacy("aaaa", "acct-1", (shared, "what aaaa wrote"));
        WriteLegacy("bbbb", "acct-1", (shared, "what bbbb wrote"));

        await Assert.That(SkillsLegacyMigration.Retire(
                ConfigRoot, first, Target, Id("acct-1"), SkillDeletionCause.Superseded, null).Incomplete)
            .IsFalse();

        // The first out leaves the files standing and its ledger gone; the survivor now holds both.
        await Assert.That(Directory.Exists(shared)).IsTrue();
        await Assert.That(Legacy(first)).IsNull();
        await Assert.That(Legacy(second)!.Rows.Single().Inherited!).IsNotEmpty();

        await Assert.That(SkillsLegacyMigration.Retire(
                ConfigRoot, second, Target, Id("acct-1"), SkillDeletionCause.Superseded, null).Incomplete)
            .IsFalse();

        await Assert.That(Directory.Exists(shared)).IsFalse();
        await Assert.That(Legacy(second)).IsNull();
    }

    /// <summary>A crash between the survivor's save and the leaver's removal leaves the survivor
    /// already holding the evidence. The next run relinquishes again and loses nothing.</summary>
    [Test]
    public async Task A_handoff_already_saved_is_completed_without_duplicating_it() {
        var shared = Copy("shared", "what aaaa wrote");

        WriteLegacy("aaaa", "acct-1", (shared, "what aaaa wrote"));
        WriteLegacy("bbbb", "acct-1", (shared, "what bbbb wrote"));

        SkillsLegacyMigration.Retire(ConfigRoot, "aaaa", Target, Id("acct-1"), SkillDeletionCause.Superseded, null);
        // Put the leaver's ledger back exactly as it was before its row was removed.
        WriteLegacy("aaaa", "acct-1", (shared, "what aaaa wrote"));

        SkillsLegacyMigration.Retire(ConfigRoot, "aaaa", Target, Id("acct-1"), SkillDeletionCause.Superseded, null);

        await Assert.That(Legacy("bbbb")!.Rows.Single().Inherited!.Length).IsEqualTo(1);
        await Assert.That(Legacy("aaaa")).IsNull();
    }

    /// <summary>A converted entry with no file hash is a claim nothing can vouch for: reported,
    /// never deleted, and the ledger goes on owning it.</summary>
    [Test]
    public async Task An_entry_without_a_file_hash_is_reported_rather_than_deleted() {
        var vouched = Copy("vouched", "written by kcap");
        var claimed = Copy("claimed", "who knows");

        Tmp.CreateFile(["config", "skills", "aaaa", "agents", "manifest.json"], $$"""
            {"etag":"etag-0",
             "skills":[{"doc_id":"7c9a1f02-0000-4000-8000-000000000001","slug":"vouched","version":1,
                        "content_hash":"h","path":"{{Json(vouched)}}",
                        "file_hash":"{{SkillsMaterializer.FileHash("written by kcap")}}"},
                       {"doc_id":"7c9a1f02-0000-4000-8000-000000000002","slug":"claimed","version":1,
                        "content_hash":"h","path":"{{Json(claimed)}}"}]}
            """);

        var retirement = SkillsLegacyMigration.Retire(
            ConfigRoot, "aaaa", Target, Id("acct-1"), SkillDeletionCause.Superseded, null);

        await Assert.That(retirement.Incomplete).IsFalse();
        await Assert.That(Directory.Exists(vouched)).IsFalse();
        await Assert.That(Directory.Exists(claimed)).IsTrue();
        await Assert.That(Legacy("aaaa")!.Rows.Single().State).IsEqualTo(OwnedSkillState.Unverified);
        // An unverified row is never deletable, so it is not work a later run owes either.
        await Assert.That(SkillsLegacyMigration.HoldsOutstandingWork(Legacy("aaaa"))).IsFalse();
    }

    /// <summary>A path inside a JSON string literal — Windows separators are escapes there.</summary>
    static string Json(string path) => path.Replace("\\", "\\\\");
}
