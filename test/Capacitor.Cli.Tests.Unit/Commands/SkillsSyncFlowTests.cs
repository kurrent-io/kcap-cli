using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The whole of one target's sync, driven through <c>kcap skills sync</c> against a real checkout
/// and a stubbed snapshot endpoint: ledger read, fetch, plan, write, delete, settle, migrate and
/// exclude.
///
/// <para>Every assertion is on the filesystem or on the ledger. The auto path sends both console
/// streams to a null writer, so a message proves nothing about the mode that actually delivers
/// skills, and the exit code is the only other channel.</para>
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class SkillsSyncFlowTests {
    [TempDir("skillsync")] public required TempDir Tmp { get; init; }

    [Test]
    [NotInParallel]
    public async Task A_first_sync_writes_into_the_checkout_and_nothing_into_the_home() {
        // HOME is repointed so that a destination resolved from the home rather than the anchor
        // lands somewhere this test can see, instead of in the developer's own trees.
        var       home  = Tmp.CreateDir("home");
        using var pin   = EnvScope.Exclusive("HOME", home);
        using var repo  = Checkout("repo");
        var       reach = new SkillApplicability { Vendors = ["claude"], SessionKinds = ["review"] };
        var       sent  = SkillsSyncFixture.Skill("alpha", home: "project:acme", applicability: reach);
        var       plain = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", sent, plain));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(sent));
        await Assert.That(File.ReadAllText(fx.SkillFile("beta"))).IsEqualTo(Rendered(plain));

        var ledger = fx.ReadLedger();
        var rows   = ledger.Rows.ToDictionary(r => r.Confirmed!.Document.Slug);

        await Assert.That(ledger.Identity).IsEqualTo(fx.Identity);
        await Assert.That(ledger.Etag).IsEqualTo("etag-1");
        await Assert.That(ledger.SyncedAt).IsEqualTo(SkillsSyncFixture.Now);
        await Assert.That(ledger.LegacyRetirement).IsNull();
        await Assert.That(ledger.Exposure!).IsEquivalentTo(["claude", "copilot", "cursor", "opencode"]);
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.Values.All(r => r.State == OwnedSkillState.Published)).IsTrue();
        await Assert.That(rows.Values.All(r => r.Prepared is null)).IsTrue();
        await Assert.That(rows["alpha"].Path).IsEqualTo(fx.SkillDir("alpha"));
        await Assert.That(rows["alpha"].Anchor).IsEqualTo(fx.Anchor);
        await Assert.That(rows["alpha"].Confirmed!.Document.Home).IsEqualTo("project:acme");
        await Assert.That(rows["alpha"].Confirmed.Document.Applicability!.Vendors).IsEquivalentTo(["claude"]);
        await Assert.That(rows["beta"].Confirmed!.Document.Home).IsEqualTo(fx.RepoHome);

        await Assert.That(Directory.Exists(new ClaudePaths(new UserHome(home), null).UserSkillsDir)).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(home)).IsEmpty();
    }

    [Test]
    public async Task Two_worktrees_of_one_repository_materialize_independently() {
        using var main   = Checkout("main");
        using var linked = main.AddWorktree(Tmp.PathTo("linked"), "feature");
        var       alpha  = SkillsSyncFixture.Skill("alpha");
        var       mainFx = new SkillsSyncFixture(Tmp, main.Path, StubSkillsApi.Serving("etag-1", alpha));
        var       wtFx   = new SkillsSyncFixture(Tmp, linked.Path, StubSkillsApi.Serving("etag-1", alpha));

        await Assert.That(await mainFx.Command.HandleSync(dryRun: false)).IsEqualTo(0);
        await Assert.That(await wtFx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        // A precondition, not a verdict: both sides are the fixture's own resolution.
        await Assert.That(mainFx.GitDir).IsNotEqualTo(wtFx.GitDir);
        await Assert.That(File.ReadAllText(mainFx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(File.ReadAllText(wtFx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(mainFx.Rows().Single().Anchor).IsEqualTo(main.Path);
        await Assert.That(wtFx.Rows().Single().Path).IsEqualTo(wtFx.SkillDir("alpha"));

        // A revocation reaching one worktree leaves the other's copy served: two ledgers, not one.
        var revoked = new SkillsSyncFixture(Tmp, linked.Path, StubSkillsApi.Serving("etag-2"));

        await Assert.That(await revoked.Command.HandleSync(dryRun: false)).IsEqualTo(0);
        await Assert.That(wtFx.HasSkill("alpha")).IsFalse();
        await Assert.That(mainFx.HasSkill("alpha")).IsTrue();
    }

    [Test]
    public async Task A_second_repository_sees_none_of_the_firsts_skills() {
        using var first    = Checkout("first");
        using var second   = Checkout("second");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       beta     = SkillsSyncFixture.Skill("beta");
        var       firstFx  = new SkillsSyncFixture(Tmp, first.Path, StubSkillsApi.Serving("etag-a", alpha),
                                                   repoName: "first");
        var       secondFx = new SkillsSyncFixture(Tmp, second.Path, StubSkillsApi.Serving("etag-b", beta),
                                                   repoName: "second");

        await Assert.That(await firstFx.Command.HandleSync(dryRun: false)).IsEqualTo(0);
        await Assert.That(await secondFx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        // A precondition, not a verdict: both hashes are the fixture's own.
        await Assert.That(firstFx.RepoHash).IsNotEqualTo(secondFx.RepoHash);
        await Assert.That(Materialized(firstFx.SkillsRoot)).IsEquivalentTo(["kcap-alpha"]);
        await Assert.That(Materialized(secondFx.SkillsRoot)).IsEquivalentTo(["kcap-beta"]);
        await Assert.That(secondFx.Api.Requests.Single().RepoHash).IsEqualTo(secondFx.RepoHash);
    }

    [Test]
    public async Task A_hand_authored_skill_beside_kcaps_survives_a_deletion() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2"));

        fx.WriteLedger(fx.Owning(fx.Materialize(alpha)) with { Etag = "etag-1" });

        // A kcap- prefix is no claim of ownership: deletion walks the rows, never the root.
        var authored  = repo.CreateFile([".claude", "skills", "authored", "SKILL.md"], "mine");
        var lookalike = repo.CreateFile([".claude", "skills", "kcap-not-ours", "SKILL.md"], "also mine");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(fx.HasSkill("alpha")).IsFalse();
        await Assert.That(File.ReadAllText(authored)).IsEqualTo("mine");
        await Assert.That(File.ReadAllText(lookalike)).IsEqualTo("also mine");
        await Assert.That(fx.Rows()).IsEmpty();
    }

    /// <summary>A served slug can name a directory the repository already has — impossible while the
    /// destination lived outside the checkout. Writing it would record ownership of a directory the
    /// repository may track.</summary>
    [Test]
    public async Task A_destination_the_ledger_does_not_own_is_refused() {
        using var repo   = Checkout("repo");
        var       alpha  = SkillsSyncFixture.Skill("alpha");
        var       beta   = SkillsSyncFixture.Skill("beta");
        var       fx     = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));
        var       theirs = repo.CreateFile([".claude", "skills", "kcap-alpha", "notes.md"], "committed");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(File.ReadAllText(theirs)).IsEqualTo("committed");
        await Assert.That(File.Exists(fx.SkillFile("alpha"))).IsFalse();
        await Assert.That(File.ReadAllText(fx.SkillFile("beta"))).IsEqualTo(Rendered(beta));

        var ledger = fx.ReadLedger();

        // Neither claimed nor cached: a 304 answered to a recorded etag would report the
        // repository's own directory as a materialized skill.
        await Assert.That(ledger.Rows.Select(r => r.Confirmed!.Document.Slug)).IsEquivalentTo(["beta"]);
        await Assert.That(ledger.Etag).IsNull();
        await Assert.That(ledger.SyncedAt).IsNull();
    }

    /// <summary>A row records the anchor that authorised it, and the root that anchor produces. A
    /// hand-edited or corrupted ledger naming a directory of the same shape somewhere else is a row
    /// nothing answers for.</summary>
    [Test]
    public async Task A_row_whose_root_is_not_its_anchors_is_refused() {
        using var repo   = Checkout("repo");
        var       alpha  = SkillsSyncFixture.Skill("alpha");
        var       fx     = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", alpha));
        var       victim = Tmp.CreateFile(["elsewhere", ".claude", "skills", "kcap-victim", "SKILL.md"],
                                          "not kcap's");
        var       aimed  = Tmp.PathTo("elsewhere", ".claude", "skills", "kcap-victim");

        fx.WriteLedger(fx.Owning(fx.Materialize(alpha), new OwnedSkillRow {
            Path = aimed, Root = fx.SkillsRoot, Anchor = fx.Anchor, Origin = SkillOrigin.Repository,
            State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Revoked,
            Confirmed = new SkillReceipt {
                FileHash = SkillsMaterializer.FileHash("not kcap's"),
                Document = SkillDocument.Of(SkillsSyncFixture.Skill("victim"), fx.RepoHome),
            },
        }) with { Etag = "etag-1" });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(File.ReadAllText(victim)).IsEqualTo("not kcap's");
        await Assert.That(fx.Rows().Select(r => r.Path)).Contains(aimed);
    }

    /// <summary>The receipt is what authorises deleting bytes. A directory a row genuinely owns,
    /// holding a file the row never confirmed writing, keeps that file.</summary>
    [Test]
    public async Task A_file_the_row_never_confirmed_writing_survives_its_deletion() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2"));
        var       owned = fx.Materialize(alpha);

        File.WriteAllText(fx.SkillFile("alpha"), "replaced by hand after the receipt was taken");
        fx.WriteLedger(fx.Owning(owned) with { Etag = "etag-1" });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha")))
            .IsEqualTo("replaced by hand after the receipt was taken");
        await Assert.That(fx.RowFor("alpha")!.State).IsEqualTo(OwnedSkillState.Owed);
    }

    /// <summary>A global ledger that will not parse names directories nothing else can. Deleting it
    /// would leave them with nothing able to remove them.</summary>
    [Test]
    public async Task A_global_ledger_that_will_not_parse_survives_the_sync() {
        using var repo   = Checkout("repo");
        var       alpha  = SkillsSyncFixture.Skill("alpha");
        var       fx     = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha));
        var       global = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLegacyLedger("{ truncated");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsTrue();
        await Assert.That(Directory.Exists(global)).IsTrue();
    }

    [Test]
    public async Task An_account_change_retires_both_catalogues_before_a_replacement_that_fails() {
        using var repo     = Checkout("repo");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));
        var       retired  = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       owned    = fx.Materialize(alpha);
        var       global   = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLedger(fx.Owning(owned) with { Etag = "etag-1", Identity = retired });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = retired, Owned = [fx.Global(alpha, global, "the global copy")],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(fx.HasSkill("alpha")).IsFalse();
        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsFalse();

        var ledger = fx.ReadLedger();

        await Assert.That(ledger.Rows).IsEmpty();
        await Assert.That(ledger.Identity).IsEqualTo(fx.Identity);
        await Assert.That(ledger.Etag).IsNull();
        // No refresh stamp: a failed replacement must not read as a completed sync.
        await Assert.That(ledger.SyncedAt).IsNull();
        await Assert.That(ledger.LegacyRetirement).IsNull();
        await Assert.That(fx.Api.Requests.Single().Etag).IsNull();
        // The block goes in before anything is written, so even a run that never settled leaves
        // kcap's directories excluded rather than visible to Git.
        await Assert.That(Exclude(fx)).Contains("/.claude/skills/kcap-*/");
    }

    /// <summary>A target is adopted on a legacy global ledger alone, so a retirement recorded only
    /// there is still one this run owes before the fetch — a replacement that fails must leave none
    /// of the previous account's skills loadable.</summary>
    [Test]
    public async Task A_retirement_known_only_to_the_legacy_ledger_runs_before_the_fetch() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));
        var       retired = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       global  = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = retired, Owned = [fx.Global(alpha, global, "the global copy")],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsFalse();
        // Nothing local was owned, so nothing local is retired and no ledger is invented for it.
        await Assert.That(File.Exists(fx.LedgerPath)).IsFalse();
    }

    /// <summary>A legacy ledger under a previous account that migration can prove nothing about
    /// stays superseded on every start. The local catalogue belongs to the current account and is
    /// not part of that: deleting it here would re-materialize it every session, and a fetch that
    /// then failed would leave the checkout with no skills at all.</summary>
    [Test]
    public async Task A_legacy_retirement_that_cannot_finish_leaves_the_local_catalogue_alone() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));
        var       retired = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       global  = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLedger(fx.Owning(fx.Materialize(alpha)) with { Etag = "etag-1" });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = retired, Owned = [fx.Global(alpha, global, "the global copy")],
        });
        // A sibling ledger that will not parse could be hiding the only other owner of that copy,
        // so migration can prove nothing and the legacy ledger outlives every run.
        Tmp.CreateFile(["config", "skills", "othersibling", "claude", "manifest.json"], "{ truncated");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(fx.HasSkill("alpha")).IsTrue();
        await Assert.That(fx.Rows().Single().Confirmed!.Document.Slug).IsEqualTo("alpha");
        await Assert.That(Directory.Exists(global)).IsTrue();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsTrue();
    }

    /// <summary>The released version recorded no identity at all, so every global ledger an
    /// upgrading machine already has converts with none. Absent is not "the current account's": an
    /// account transition has to settle it, or the previous account's global catalogue stays
    /// readable from every repository on the machine and no later run can tell.</summary>
    [Test]
    public async Task An_account_change_settles_a_global_ledger_the_shipped_version_wrote() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));
        var       retired = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       global  = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLedger(fx.Owning(fx.Materialize(alpha)) with { Etag = "etag-1", Identity = retired });
        // The shape at the branch point: etag, synced_at and skills, and nothing else.
        fx.WriteLegacyLedger($$"""
            {"etag":"etag-0","synced_at":"2026-09-17T09:00:00+00:00",
             "skills":[{"doc_id":"{{alpha.DocId}}","slug":"alpha","version":1,
                        "content_hash":"h","path":"{{JsonPath(global)}}",
                        "file_hash":"{{SkillsMaterializer.FileHash("the global copy")}}"}]}
            """);

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsFalse();
        await Assert.That(fx.HasSkill("alpha")).IsFalse();
    }

    /// <summary>A converted entry with no file hash is a claim nothing can vouch for: it is named
    /// on the error stream and never deleted, and the current bytes on disk are never adopted as
    /// proof. Preserving it silently would leave the operator with a global copy nobody mentions.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task A_converted_entry_without_a_file_hash_is_reported_rather_than_deleted() {
        using var repo   = Checkout("repo");
        var       alpha  = SkillsSyncFixture.Skill("alpha");
        var       fx     = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha));
        var       global = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLegacyLedger($$"""
            {"etag":"etag-0",
             "skills":[{"doc_id":"{{alpha.DocId}}","slug":"alpha","version":1,
                        "content_hash":"h","path":"{{JsonPath(global)}}"}]}
            """);

        string reported;
        using (var console = ConsoleOutput.StartErrorCapture("\n")) {
            await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);
            reported = console.GetCapturedError();
        }

        await Assert.That(reported).Contains(global);
        await Assert.That(Directory.Exists(global)).IsTrue();
        await Assert.That(fx.ReadLegacyLedger().Rows.Single().State).IsEqualTo(OwnedSkillState.Unverified);
        // The repository-local copy is materialized all the same.
        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
    }

    [Test]
    public async Task An_anchor_change_rewrites_the_paths_even_under_an_unchanged_etag() {
        using var repo     = Checkout("repo");
        var       previous = Tmp.CreateDir("previous");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha));
        var       oldDir   = SkillsMaterializer.SkillDirFor(
            Path.Combine(previous.Path, ClaudePaths.RepoSkillsRelativePath), alpha.Slug);

        Tmp.CreateFile(["previous", ".claude", "skills", "kcap-alpha", "SKILL.md"], Rendered(alpha));
        fx.WriteLedger(Recorded(fx, alpha, previous));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(Directory.Exists(oldDir)).IsFalse();

        var ledger = fx.ReadLedger();

        await Assert.That(ledger.Rows.Single().Path).IsEqualTo(fx.SkillDir("alpha"));
        await Assert.That(ledger.Rows.Single().Anchor).IsEqualTo(fx.Anchor);
        await Assert.That(ledger.Etag).IsEqualTo("etag-1");
        // The destination is in neither the etag nor the plan, so a ledger at another anchor cannot
        // be allowed to answer a conditional request.
        await Assert.That(fx.Api.Requests.Single().Etag).IsNull();
    }

    /// <summary>Where the filesystem is case-insensitive, two casings of one checkout are one
    /// checkout: the second launch must not read as an anchor move and delete the directory the same
    /// run has just republished.</summary>
    [Test]
    public async Task A_launch_through_another_casing_keeps_what_the_first_published() {
        using var repo  = Checkout("repo");
        var       other = Tmp.PathTo("REPO");

        Skip.When(!Directory.Exists(other), "the volume under the test root is case-sensitive");

        var alpha = SkillsSyncFixture.Skill("alpha");
        var first = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha));

        await Assert.That(await first.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var second = new SkillsSyncFixture(Tmp, other, StubSkillsApi.Serving("etag-1", alpha));

        await Assert.That(await second.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(first.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(second.Rows().Single().State).IsEqualTo(OwnedSkillState.Published);
    }

    /// <summary>A deletion refused at one anchor leaves a row aimed there. A second move must not
    /// strand it: each row carries the anchor that authorised it, so a run at a third anchor can
    /// still answer for the first one's paths.
    ///
    /// <para>The refusal is real rather than simulated — the first anchor's skills root is a link
    /// out of it, which containment refuses — and it is lifted before the third run, so what that
    /// run has to supply is the authority and nothing else.</para></summary>
    [Test]
    public async Task A_row_left_at_a_first_anchor_survives_a_move_to_a_third() {
        using var repo   = Checkout("repo");
        var       ghostDoc = SkillsSyncFixture.Skill("ghost");
        var       first  = Tmp.CreateDir("first");
        var       second = Tmp.CreateDir("second");
        var       third  = Tmp.CreateDir("third");
        var       away   = Tmp.CreateDir("away");
        var       fx     = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1"));
        var       target = fx.Target;
        var       at     = SkillDestination.For(target, first, "ghost");
        var       body   = Rendered(ghostDoc);

        // The first anchor's tree is a link out of it, so its rows cannot be deleted from anywhere.
        Directory.CreateDirectory(Path.GetDirectoryName(at.Root)!);
        Directory.CreateSymbolicLink(at.Root, away.Path);
        away.CreateFile(["kcap-ghost", "SKILL.md"], body);

        fx.WriteLedger(new SkillsLedger {
            Etag = "etag-0", SyncedAt = SkillsSyncFixture.Now.AddDays(-1), Identity = fx.Identity,
            Owned = [fx.Published(ghostDoc, at, SkillsMaterializer.FileHash(body)) with {
                State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Revoked,
            }],
        });

        var moved = await fx.Command.AttemptTargetAsync(
            target, second, fx.GitDir, fx.RepoHash, fx.RepoHome, fx.Identity,
            dryRun: false, auto: false, takeMigration: false);

        // A precondition: the deletion genuinely could not be carried out at the second anchor.
        await Assert.That(moved.Code).IsEqualTo(1);
        await Assert.That(fx.Rows().Single().Path).IsEqualTo(at.Path);

        // The link goes; the orphan is now an ordinary owned directory under the first anchor.
        Directory.Delete(at.Root);
        new TempDirHandle(at.Path).CreateFile("SKILL.md", body);

        var resumed = await fx.Command.AttemptTargetAsync(
            target, third, fx.GitDir, fx.RepoHash, fx.RepoHome, fx.Identity,
            dryRun: false, auto: false, takeMigration: false);

        await Assert.That(resumed.Code).IsEqualTo(0);
        await Assert.That(Directory.Exists(at.Path)).IsFalse();
        await Assert.That(fx.Rows()).IsEmpty();
    }

    /// <summary>A ledger recording a path at one anchor says nothing about a directory that happens
    /// to carry the same name at another. Claiming it would let the write overwrite the repository's
    /// own file and a later revocation delete whatever else was kept beside it.</summary>
    [Test]
    public async Task An_anchor_change_does_not_claim_a_directory_that_did_not_travel() {
        using var repo     = Checkout("repo");
        var       previous = Tmp.CreateDir("previous");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", alpha));
        var       theirs   = repo.CreateFile([".claude", "skills", "kcap-alpha", "notes.md"], "committed");

        fx.WriteLedger(Recorded(fx, alpha, previous));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(File.ReadAllText(theirs)).IsEqualTo("committed");
        await Assert.That(File.Exists(fx.SkillFile("alpha"))).IsFalse();
        await Assert.That(fx.Rows().Select(r => r.Path)).DoesNotContain(fx.SkillDir("alpha"));
    }

    /// <summary>A checkout that moved with its files leaves the ledger naming the old anchor's paths
    /// while the real copies sit at the new one. A revocation that only knows the old paths finds
    /// them absent, calls the deletion done, and leaves the copies loadable for good.</summary>
    [Test]
    public async Task A_relocated_copy_is_deleted_when_its_document_is_revoked() {
        using var repo     = Checkout("repo");
        var       previous = Tmp.CreateDir("previous");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2"));

        // The copy travelled with the checkout; nothing is left at the anchor the ledger records.
        fx.Materialize(alpha);
        fx.WriteLedger(Recorded(fx, alpha, previous));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(fx.HasSkill("alpha")).IsFalse();
        await Assert.That(fx.Rows()).IsEmpty();
    }

    /// <summary>A source that is present but edited is not absent, so nothing at the new anchor is
    /// treated as the same copy moved: the row keeps its old path, and the deletion that follows
    /// refuses bytes it never confirmed writing.</summary>
    [Test]
    public async Task An_edited_source_is_not_read_as_absence() {
        using var repo     = Checkout("repo");
        var       previous = Tmp.CreateDir("previous");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", alpha));
        var       oldFile  = Tmp.CreateFile(["previous", ".claude", "skills", "kcap-alpha", "SKILL.md"],
                                            "edited since kcap wrote it");

        fx.WriteLedger(Recorded(fx, alpha, previous));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(File.ReadAllText(oldFile)).IsEqualTo("edited since kcap wrote it");

        var stale = fx.Rows().Single(r => !PathComparison.Equal(r.Path, fx.SkillDir("alpha")));

        await Assert.That(stale.Anchor).IsEqualTo(previous.Path);
        await Assert.That(stale.State).IsEqualTo(OwnedSkillState.Owed);
    }

    /// <summary>Copy before delete. A rewrite the destination refuses leaves the old copy the only
    /// one there is, so settling its deletion anyway leaves the document served from nowhere — and
    /// a target that could not publish is not one the legacy migration may follow.</summary>
    [Test]
    public async Task A_refused_rewrite_keeps_the_copy_it_was_meant_to_replace() {
        using var repo     = Checkout("repo");
        var       previous = Tmp.CreateDir("previous");
        var       away     = Tmp.CreateDir("away");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", alpha));
        var       oldDir   = SkillsMaterializer.SkillDirFor(
            Path.Combine(previous.Path, ClaudePaths.RepoSkillsRelativePath), alpha.Slug);

        // The only working copy, at the anchor the ledger records.
        Tmp.CreateFile(["previous", ".claude", "skills", "kcap-alpha", "SKILL.md"], Rendered(alpha));
        // Every destination at the new anchor leaves it through a link, so every write is refused.
        Directory.CreateDirectory(Path.GetDirectoryName(fx.SkillsRoot)!);
        Directory.CreateSymbolicLink(fx.SkillsRoot, away.Path);
        fx.WriteLedger(Recorded(fx, alpha, previous));

        var attempt = await fx.Command.AttemptTargetAsync(
            fx.Target, repo.Path, fx.GitDir, fx.RepoHash, fx.RepoHome, fx.Identity,
            dryRun: false, auto: false, takeMigration: false);

        await Assert.That(attempt.Code).IsEqualTo(1);
        await Assert.That(File.ReadAllText(SkillsMaterializer.SkillFileFor(oldDir))).IsEqualTo(Rendered(alpha));
        // A target whose write was refused has not migrated, so the tail must not follow it.
        await Assert.That(attempt.Settled).IsFalse();
        // Still owned, and still published: a refused write transfers no ownership at all.
        var kept = fx.Rows().Single(r => PathComparison.Equal(r.Path, oldDir));

        await Assert.That(kept.State).IsEqualTo(OwnedSkillState.Published);
        await Assert.That(fx.ReadLedger().Etag).IsNull();
    }

    /// <summary>A deletion a retirement ordered outlives the ledger's identity: the replacement
    /// catalogue is saved under the new account, so the next run no longer reads as superseded. The
    /// row has to remember it, or the previous account's files wait on a fetch that may never
    /// succeed.</summary>
    [Test]
    public async Task A_retirement_deletion_refused_once_is_retried_before_the_next_fetch() {
        using var repo  = Checkout("repo");
        var       away  = Tmp.CreateDir("away");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));
        var       body  = Rendered(alpha);

        // The catalogue sits behind a link out of the anchor, so containment refuses its deletion.
        Directory.CreateDirectory(Path.GetDirectoryName(fx.SkillsRoot)!);
        Directory.CreateSymbolicLink(fx.SkillsRoot, away.Path);
        away.CreateFile(["kcap-alpha", "SKILL.md"], body);
        fx.WriteLedger(fx.Owning(fx.Published(alpha, SkillDestination.For(fx.Target, fx.Anchor, "alpha"),
                                              SkillsMaterializer.FileHash(body))) with {
            Etag = "etag-1", Identity = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl),
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);
        // A precondition: the deletion was genuinely refused and the ledger now reads as current.
        await Assert.That(Directory.Exists(away.PathTo("kcap-alpha"))).IsTrue();
        await Assert.That(fx.ReadLedger().Identity).IsEqualTo(fx.Identity);
        await Assert.That(fx.Rows().Single().Cause).IsEqualTo(SkillDeletionCause.Retired);

        // The blocker clears; the same directory is now an ordinary owned one under the anchor.
        Directory.Delete(fx.SkillsRoot);
        Tmp.CreateFile(["repo", ".claude", "skills", "kcap-alpha", "SKILL.md"], body);

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(fx.HasSkill("alpha")).IsFalse();
        await Assert.That(fx.Rows()).IsEmpty();
    }

    /// <summary>The other half of an anchor change: the checkout moved and took its materialized
    /// directories with it, while the ledger — which lives in the git directory — came along
    /// recording the anchor it was written at. Every destination therefore exists before the run
    /// starts, and every one of them is still kcap's.</summary>
    [Test]
    public async Task A_checkout_that_moved_with_its_files_still_owns_them() {
        using var repo     = Checkout("repo");
        var       previous = Tmp.CreateDir("previous");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", alpha));

        fx.Materialize(alpha);
        fx.WriteLedger(Recorded(fx, alpha, previous));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));

        var ledger = fx.ReadLedger();

        await Assert.That(ledger.Rows.Single().Path).IsEqualTo(fx.SkillDir("alpha"));
        await Assert.That(ledger.Rows.Single().Anchor).IsEqualTo(fx.Anchor);
        // A run that refused its own directories would drop both and re-refuse forever.
        await Assert.That(ledger.Etag).IsEqualTo("etag-2");
        await Assert.That(ledger.SyncedAt).IsEqualTo(SkillsSyncFixture.Now);
    }

    /// <summary>A <c>304</c> settles the ledger like any other outcome: the deletion still owed
    /// carried out, the exclusion block rewritten and the legacy copy retired.</summary>
    [Test]
    public async Task A_not_modified_answer_still_settles_excludes_and_migrates() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       gone    = SkillsSyncFixture.Skill("gone");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Unchanged());
        var       owned   = fx.Materialize(alpha);
        var       orphan  = fx.Materialize(gone);
        var       globals = Tmp.CreateDir("home", ".claude", "skills");
        var       global  = globals.PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLedger(fx.Owning(owned, orphan with {
            State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Revoked,
        }) with { Etag = "etag-1" });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = fx.Identity, Owned = [fx.Global(alpha, global, "the global copy")],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var ledger = fx.ReadLedger();

        await Assert.That(ledger.Rows.Single().Confirmed!.Document.Slug).IsEqualTo("alpha");
        await Assert.That(ledger.SyncedAt).IsEqualTo(SkillsSyncFixture.Now);
        await Assert.That(fx.HasSkill("gone")).IsFalse();
        await Assert.That(fx.HasSkill("alpha")).IsTrue();
        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsFalse();

        var exclude = Exclude(fx);

        foreach (var root in SkillsCommand.Targets(fx.LegacyRoots))
            await Assert.That(exclude).Contains(
                "/" + root.RelativePath.Replace(Path.DirectorySeparatorChar, '/') + "/kcap-*/");
    }

    /// <summary>A local sync stamps a fresh refresh while its tail can still leave a global copy
    /// behind. The stamp is not evidence that cleanup finished, so the throttle must not read it as
    /// one — outstanding legacy ownership is owed work, and the local ledger cannot see it.</summary>
    [Test]
    public async Task An_auto_run_retries_legacy_cleanup_the_throttle_would_otherwise_skip() {
        using var repo   = Checkout("repo");
        var       alpha  = SkillsSyncFixture.Skill("alpha");
        var       fx     = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Unchanged());
        var       global = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        // A stamp well inside the refresh interval, over a local ledger with nothing left to do.
        fx.WriteLedger(fx.Owning(fx.Materialize(alpha)) with {
            Etag = "etag-1", SyncedAt = SkillsSyncFixture.Now.AddMinutes(-1),
        });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = fx.Identity, Owned = [fx.Global(alpha, global, "the global copy")],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false, auto: true)).IsEqualTo(0);

        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsFalse();
        await Assert.That(fx.HasSkill("alpha")).IsTrue();
    }

    /// <summary>A legacy ledger that will not parse names paths nothing can act on, so it is not
    /// work the throttle owes — counting it would buy a refresh at every session start that never
    /// clears.</summary>
    [Test]
    public async Task An_auto_run_is_still_throttled_by_a_legacy_ledger_that_will_not_parse() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("never asked"));

        fx.WriteLedger(fx.Owning(fx.Materialize(alpha)) with {
            Etag = "etag-1", SyncedAt = SkillsSyncFixture.Now.AddMinutes(-1),
        });
        fx.WriteLegacyLedger("{ truncated");

        await Assert.That(await fx.Command.HandleSync(dryRun: false, auto: true)).IsEqualTo(0);

        await Assert.That(fx.Api.Requests).IsEmpty();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsTrue();
    }

    /// <summary>The lock-free peek that decides whether to take the migration lock can be superseded
    /// by a peer between the peek and the locked read. The attempt then hands both locks back for
    /// one retry rather than holding a shared lock across a fetch.</summary>
    [Test]
    public async Task A_retirement_found_under_the_lock_asks_for_a_retry_instead_of_fetching() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("never asked"));

        fx.WriteLedger(fx.Owning(fx.Materialize(alpha)) with {
            Etag = "etag-1", Identity = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl),
        });

        var attempt = await fx.Command.AttemptTargetAsync(
            fx.Target, fx.Anchor, fx.GitDir, fx.RepoHash, fx.RepoHome, fx.Identity,
            dryRun: false, auto: false, takeMigration: false);

        await Assert.That(attempt.NeedsMigration).IsTrue();
        await Assert.That(attempt.Settled).IsFalse();
        await Assert.That(attempt.Code).IsEqualTo(0);
        await Assert.That(fx.Api.Requests).IsEmpty();
        await Assert.That(fx.HasSkill("alpha")).IsTrue();
        await Assert.That(fx.ReadLedger().Identity!.Account).IsEqualTo("previous-user");
    }

    /// <summary>A retirement runs before the fetch and deletes both catalogues, so a preview that
    /// listed only what it would write would show none of the destruction.</summary>
    [Test]
    [NotInParallel]
    public async Task A_dry_run_names_the_catalogue_a_retirement_would_delete() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", alpha));
        var       retired = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       owned   = fx.Materialize(alpha);
        var       global  = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLedger(fx.Owning(owned) with { Etag = "etag-1", Identity = retired });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = retired, Owned = [fx.Global(alpha, global, "the global copy")],
        });

        string preview;
        using (var console = ConsoleOutput.StartCapture("\n")) {
            await Assert.That(await fx.Command.HandleSync(dryRun: true)).IsEqualTo(0);
            preview = console.GetCapturedOutput();
        }

        await Assert.That(preview).Contains($"would retire {owned.Path}");
        await Assert.That(preview).Contains($"would retire {global}");
        await Assert.That(preview).Contains($"would write  {fx.SkillDir("alpha")}");
        await Assert.That(fx.HasSkill("alpha")).IsTrue();
        await Assert.That(Directory.Exists(global)).IsTrue();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsTrue();
    }

    /// <summary>A peer can legitimately hold the machine-wide migration lock across its own local
    /// work. A retirement that cannot take it reports incomplete, so the next start retries it
    /// instead of reading the holder's work as its own — and until then it fetches nothing and
    /// deletes nothing.
    ///
    /// <para>The lease is real and cross-process, but its name hashes the fixture's own config root,
    /// so it can contend with nothing outside this test; the wait the sync then makes is bounded by
    /// the lock's own timeout, so a failure cannot hang the suite.</para></summary>
    [Test]
    public async Task A_migration_lock_held_elsewhere_leaves_the_retirement_for_the_next_run() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("never asked"));
        var       retired = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       owned   = fx.Materialize(alpha);
        var       global  = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLedger(fx.Owning(owned) with { Etag = "etag-1", Identity = retired });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = retired, Owned = [fx.Global(alpha, global, "the global copy")],
        });

        // 2 is the command's "incomplete", distinct from a failure so the next start retries.
        using (fx.Config.AcquireLock(SkillsLocks.Migration))
            await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(2);

        await Assert.That(fx.Api.Requests).IsEmpty();
        await Assert.That(fx.HasSkill("alpha")).IsTrue();
        await Assert.That(Directory.Exists(global)).IsTrue();
        await Assert.That(fx.ReadLedger().Identity).IsEqualTo(retired);
    }

    /// <summary>Two repositories retiring at once own one global directory between them. The single
    /// migration key serializes them, so the shared copy is deleted once, each private copy by its
    /// own owner, and no ledger outlives a directory it named.</summary>
    [Test]
    public async Task Two_repositories_retiring_one_shared_copy_leave_nothing_unowned() {
        using var first    = Checkout("first");
        using var second   = Checkout("second");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       retired  = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       firstFx  = new SkillsSyncFixture(Tmp, first.Path, StubSkillsApi.Serving("etag-a"),
                                                   repoName: "first");
        var       secondFx = new SkillsSyncFixture(Tmp, second.Path, StubSkillsApi.Serving("etag-b"),
                                                   repoName: "second");
        var       globals  = Tmp.CreateDir("home", ".claude", "skills");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-shared", "SKILL.md"], "owned twice");
        Tmp.CreateFile(["home", ".claude", "skills", "kcap-first", "SKILL.md"], "owned once");
        Tmp.CreateFile(["home", ".claude", "skills", "kcap-second", "SKILL.md"], "owned once");
        Tmp.CreateFile(["home", ".claude", "skills", "authored", "SKILL.md"], "not kcap's");

        Retiring(firstFx, alpha, retired, ("kcap-shared", "owned twice"), ("kcap-first", "owned once"));
        Retiring(secondFx, alpha, retired, ("kcap-shared", "owned twice"), ("kcap-second", "owned once"));

        var runs = await Task.WhenAll(firstFx.Command.HandleSync(dryRun: false),
                                      secondFx.Command.HandleSync(dryRun: false));

        // A second deletion of the same directory is the intent satisfied, not a failure.
        await Assert.That(runs).IsEquivalentTo([0, 0]);
        await Assert.That(Directory.GetDirectories(globals)).IsEquivalentTo([globals.PathTo("authored")]);
        await Assert.That(File.Exists(firstFx.LegacyLedgerPath)).IsFalse();
        await Assert.That(File.Exists(secondFx.LegacyLedgerPath)).IsFalse();
    }

    /// <summary>Two repositories owning one global copy under the current credential each give up
    /// their own claim as they migrate, so the second finds itself the last owner and the files go.
    /// A claim neither ever relinquished would leave each seeing the other as an owner forever.
    /// </summary>
    [Test]
    public async Task Two_repositories_sharing_a_global_copy_relinquish_it_until_one_can_delete() {
        using var first    = Checkout("first");
        using var second   = Checkout("second");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       firstFx  = new SkillsSyncFixture(Tmp, first.Path, StubSkillsApi.Serving("etag-a", alpha),
                                                   repoName: "first");
        var       secondFx = new SkillsSyncFixture(Tmp, second.Path, StubSkillsApi.Serving("etag-b", alpha),
                                                   repoName: "second");
        var       shared   = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-shared");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-shared", "SKILL.md"], "owned twice");
        foreach (var fx in (SkillsSyncFixture[])[firstFx, secondFx]) {
            fx.WriteLedger(fx.Owning(fx.Materialize(alpha)));
            fx.WriteLegacyLedger(new SkillsLedger {
                Identity = fx.Identity, Owned = [fx.Global(alpha, shared, "owned twice")],
            });
        }

        await Assert.That(await firstFx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        // The second repository still owns it, so the files stay — but this one no longer claims it.
        await Assert.That(Directory.Exists(shared)).IsTrue();
        await Assert.That(File.Exists(firstFx.LegacyLedgerPath)).IsFalse();

        await Assert.That(await secondFx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(Directory.Exists(shared)).IsFalse();
        await Assert.That(File.Exists(secondFx.LegacyLedgerPath)).IsFalse();
    }

    /// <summary>A checkout whose recorded account is no longer the current one, owning one local
    /// copy and the given global ones.</summary>
    static void Retiring(SkillsSyncFixture fx, SkillSnapshotItem item, SkillsIdentity retired,
                         params (string Dir, string Body)[] globals) {
        var owned = fx.Materialize(item);
        var home  = Path.Combine(fx.Home, ".claude", "skills");

        fx.WriteLedger(fx.Owning(owned) with { Etag = "etag-0", Identity = retired });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = retired,
            Owned    = [.. globals.Select(g => fx.Global(item, Path.Combine(home, g.Dir), g.Body))],
        });
    }

    /// <summary>A checkout with one commit, so a linked worktree can be added to it.</summary>
    GitRepo Checkout(string name) {
        var repo = GitRepo.InitIn(Tmp.CreateDir(name));

        repo.CreateFile("README.md", "initial");
        repo.CommitAll("initial");

        return repo;
    }

    internal static string Rendered(SkillSnapshotItem item) => SkillsRendering.RenderSkillFile(item);

    /// <summary>A path inside a JSON string literal — Windows separators are escapes there.</summary>
    static string JsonPath(string path) => path.Replace("\\", "\\\\");

    /// <summary>A settled ledger written at <paramref name="anchor"/>, recording the receipt a
    /// completed sync would have — so a run at another anchor has real evidence to weigh.</summary>
    static SkillsLedger Recorded(SkillsSyncFixture fx, SkillSnapshotItem item, string anchor) => new() {
        Etag = "etag-1", SyncedAt = SkillsSyncFixture.Now.AddHours(-1), Identity = fx.Identity,
        Owned = [fx.Published(item, SkillDestination.For(fx.Target, anchor, item.Slug),
                              SkillsMaterializer.FileHash(Rendered(item)))],
    };

    static IEnumerable<string> Materialized(string root) =>
        Directory.GetDirectories(root).Select(d => Path.GetFileName(d)!);

    internal static string Exclude(SkillsSyncFixture fx) {
        var path = Path.Combine(fx.GitDir, "info", "exclude");

        return File.Exists(path) ? File.ReadAllText(path) : "";
    }
}
