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

    /// <summary>The survivor's merged evidence reaches disk before the leaver's removal does. The
    /// leaver's own save is stopped outright here, so the run ends between the two writes: an
    /// implementation that persisted the removal first would be caught by that same boundary with
    /// the survivor holding nothing. The next run relinquishes again and loses nothing.</summary>
    [Test]
    public async Task A_handoff_stopped_before_the_leaver_saves_loses_nothing() {
        var shared = Copy("shared", "what aaaa wrote");
        var mine   = Copy("mine", "aaaa alone");

        WriteLegacy("aaaa", "acct-1", (shared, "what aaaa wrote"), (mine, "aaaa alone"));
        WriteLegacy("bbbb", "acct-1", (shared, "what bbbb wrote"));

        var ledger = SkillsLegacyMigration.ManifestPathFor(ConfigRoot, "aaaa", "agents");
        var halted = false;

        using (Stop(ledger)) {
            try {
                SkillsLegacyMigration.Retire(ConfigRoot, "aaaa", Target, Id("acct-1"),
                                             SkillDeletionCause.Superseded, null);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                halted = true;
            }
        }

        Skip.When(!halted, "this user is not subject to the block on the leaver's save");

        // The survivor holds the evidence; the leaver still owns everything it did, and the
        // deletions it planned never ran.
        await Assert.That(Legacy("bbbb")!.Rows.Single().Inherited!.Length).IsEqualTo(1);
        await Assert.That(Legacy("aaaa")!.Rows.Select(r => r.Path)).IsEquivalentTo([shared, mine]);
        await Assert.That(Directory.Exists(mine)).IsTrue();

        SkillsLegacyMigration.Retire(ConfigRoot, "aaaa", Target, Id("acct-1"), SkillDeletionCause.Superseded, null);

        await Assert.That(Legacy("bbbb")!.Rows.Single().Inherited!.Length).IsEqualTo(1);
        await Assert.That(Legacy("aaaa")).IsNull();
        await Assert.That(Directory.Exists(mine)).IsFalse();
        await Assert.That(Directory.Exists(shared)).IsTrue();
    }

    /// <summary>A relinquishment whose handover did not durably succeed keeps its claim, and
    /// nothing else in the run may act on it — the file is the surviving co-owner's only copy, and
    /// a deletion here takes it from them.</summary>
    [Test]
    public async Task A_relinquishment_that_could_not_hand_over_is_not_deleted() {
        var shared = Copy("shared", "what aaaa wrote");
        var mine   = Copy("mine", "aaaa alone");
        var owned  = new SkillReceipt {
            FileHash = SkillsMaterializer.FileHash("what aaaa wrote"),
            Document = new SkillDocument {
                DocId = Guid.NewGuid(), Slug = "shared", Version = 1, ContentHash = "h",
            },
        };

        // The shared row is already owed from an earlier interrupted run.
        SkillsLedgerFile.Save(SkillsLegacyMigration.ManifestPathFor(ConfigRoot, "aaaa", "agents"),
                              new SkillsLedger {
            Identity = Id("acct-1"),
            Owned = [
                Global(shared, owned) with {
                    State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Superseded,
                },
                Global(mine, owned with { FileHash = SkillsMaterializer.FileHash("aaaa alone") }),
            ],
        });

        // A sibling that claims the shared path through a row nothing may act on: it counts as an
        // owner and it cannot take the evidence.
        SkillsLedgerFile.Save(SkillsLegacyMigration.ManifestPathFor(ConfigRoot, "bbbb", "agents"),
                              new SkillsLedger {
            Identity = Id("acct-1"),
            Owned    = [Global(shared, owned) with { Confirmed = null },
        ] });

        var retirement = SkillsLegacyMigration.Retire(ConfigRoot, "aaaa", Target, Id("acct-1"),
                                                      SkillDeletionCause.Superseded, null);

        await Assert.That(retirement.Refused).IsEquivalentTo([shared]);
        await Assert.That(Directory.Exists(shared)).IsTrue();
        await Assert.That(Directory.Exists(mine)).IsFalse();
        await Assert.That(Legacy("aaaa")!.Rows.Single().Path).IsEqualTo(shared);
    }

    static OwnedSkillRow Global(string path, SkillReceipt receipt) => new() {
        Path = path, Root = Path.GetDirectoryName(path)!, Origin = SkillOrigin.Legacy,
        State = OwnedSkillState.Published, Confirmed = receipt,
    };

    /// <summary>Stops the leaver's own save. A directory it cannot write into does that on Unix; on
    /// Windows the manifest is held open without sharing delete, which refuses the rename the save
    /// publishes through.</summary>
    static IDisposable Stop(string ledgerPath) =>
        OperatingSystem.IsWindows()
            ? new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            : new UnwritableDirectory(Path.GetDirectoryName(ledgerPath)!);

    sealed class UnwritableDirectory : IDisposable {
        readonly string _path;

        public UnwritableDirectory(string path) {
            _path = path;
            Mode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public void Dispose() =>
            Mode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A survivor that can vouch for nothing is still an owner, and relinquishing to it
    /// without handing the evidence over would leave the file with no receipt able to retire it.
    /// It takes the leaver's instead.</summary>
    [Test]
    public async Task A_survivor_holding_no_receipt_takes_the_leavers() {
        var shared = Copy("shared", "what aaaa wrote");

        WriteLegacy("aaaa", "acct-1", (shared, "what aaaa wrote"));
        // The shape the released version wrote, with no file hash at all.
        Tmp.CreateFile(["config", "skills", "bbbb", "agents", "manifest.json"], $$"""
            {"etag":"etag-0",
             "skills":[{"doc_id":"7c9a1f02-0000-4000-8000-000000000001","slug":"shared","version":1,
                        "content_hash":"h","path":"{{Json(shared)}}"}]}
            """);

        SkillsLegacyMigration.Retire(ConfigRoot, "aaaa", Target, Id("acct-1"), SkillDeletionCause.Superseded, null);

        var survivor = Legacy("bbbb")!.Rows.Single();

        await Assert.That(Legacy("aaaa")).IsNull();
        await Assert.That(Directory.Exists(shared)).IsTrue();
        await Assert.That(survivor.State).IsEqualTo(OwnedSkillState.Published);
        await Assert.That(survivor.Confirmed!.FileHash)
            .IsEqualTo(SkillsMaterializer.FileHash("what aaaa wrote"));

        // The last owner out can now finish what neither could before.
        SkillsLegacyMigration.Retire(ConfigRoot, "bbbb", Target, Id("acct-1"), SkillDeletionCause.Superseded, null);

        await Assert.That(Directory.Exists(shared)).IsFalse();
        await Assert.That(Legacy("bbbb")).IsNull();
    }

    /// <summary>A refusal establishes nothing — a link, a file that would not read, a root that
    /// moved — so it must not cost a merged row the receipts a later attempt needs.</summary>
    [Test]
    public async Task A_refusal_that_establishes_nothing_keeps_the_merged_receipts() {
        var shared = Copy("shared", "what bbbb wrote");

        WriteLegacy("aaaa", "acct-1", (shared, "what aaaa wrote"));
        WriteLegacy("bbbb", "acct-1", (shared, "what bbbb wrote"));

        SkillsLegacyMigration.Retire(ConfigRoot, "bbbb", Target, Id("acct-1"), SkillDeletionCause.Superseded, null);

        // A precondition: the sole remaining owner holds both receipts, and only the inherited one
        // accounts for the bytes.
        await Assert.That(Legacy("aaaa")!.Rows.Single().Inherited!.Length).IsEqualTo(1);

        var file = SkillsMaterializer.SkillFileFor(shared);

        File.Delete(file);
        File.CreateSymbolicLink(file, Tmp.CreateFile("elsewhere.md", "what bbbb wrote"));

        var refused = SkillsLegacyMigration.Retire(ConfigRoot, "aaaa", Target, Id("acct-1"),
                                                   SkillDeletionCause.Superseded, null);

        await Assert.That(refused.Refused).IsEquivalentTo([shared]);
        await Assert.That(refused.Unvouched).IsEmpty();

        var kept = Legacy("aaaa")!.Rows.Single();

        await Assert.That(kept.State).IsEqualTo(OwnedSkillState.Owed);
        await Assert.That(kept.Inherited!.Length).IsEqualTo(1);

        File.Delete(file);
        new TempDirHandle(shared).CreateFile("SKILL.md", "what bbbb wrote");

        await Assert.That(SkillsLegacyMigration
            .Retire(ConfigRoot, "aaaa", Target, Id("acct-1"), SkillDeletionCause.Superseded, null)
            .Incomplete).IsFalse();
        await Assert.That(Directory.Exists(shared)).IsFalse();
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
        // Reported, not merely preserved: nothing else tells the operator a global copy is being
        // left behind for good.
        await Assert.That(retirement.Unverifiable).IsEquivalentTo([claimed]);
        await Assert.That(Directory.Exists(vouched)).IsFalse();
        await Assert.That(Directory.Exists(claimed)).IsTrue();
        await Assert.That(Legacy("aaaa")!.Rows.Single().State).IsEqualTo(OwnedSkillState.Unverified);
        // An unverified row is never deletable, so it is not work a later run owes either.
        await Assert.That(SkillsLegacyMigration.HoldsOutstandingWork(Legacy("aaaa"))).IsFalse();

        // It goes on being reported once there is nothing else left to do.
        await Assert.That(SkillsLegacyMigration
            .Retire(ConfigRoot, "aaaa", Target, Id("acct-1"), SkillDeletionCause.Superseded, null)
            .Unverifiable).IsEquivalentTo([claimed]);
    }

    /// <summary>A path inside a JSON string literal — Windows separators are escapes there.</summary>
    static string Json(string path) => path.Replace("\\", "\\\\");

    static void Mode(string path, UnixFileMode mode) {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }
}
