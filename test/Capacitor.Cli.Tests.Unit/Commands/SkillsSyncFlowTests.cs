using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The whole of one target's sync, driven through <c>kcap skills sync</c> against a real checkout
/// and a stubbed snapshot endpoint: manifest read, fetch, plan, write, prune, settle, migrate and
/// exclude.
///
/// <para>Every assertion is on the filesystem or on the manifest. The auto path sends both console
/// streams to a null writer, so a message proves nothing about the mode that actually delivers
/// skills, and the exit code is the only other channel.</para>
/// </summary>
public class SkillsSyncFlowTests {
    [TempDir("skillsync")] public required TempDir Tmp { get; init; }

    /// <summary>The slug for a rename is new; the document id is what stays the same.</summary>
    static readonly Guid RenamedDoc = new("7c9a1f02-0000-4000-8000-000000000001");

    [Test]
    [NotInParallel]
    public async Task A_first_sync_writes_into_the_checkout_and_nothing_into_the_home() {
        // HOME is repointed so that a destination resolved from the home rather than the anchor
        // lands somewhere this test can see, instead of in the developer's own trees.
        var       home  = Tmp.CreateDir("home");
        using var pin   = EnvScope.Exclusive("HOME", home);
        using var repo  = Checkout("repo");
        var       sent  = SkillsSyncFixture.Skill("alpha", home: "project:acme", applicability: "always");
        var       plain = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", sent, plain));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(sent));
        await Assert.That(File.ReadAllText(fx.SkillFile("beta"))).IsEqualTo(Rendered(plain));

        var manifest = fx.ReadManifest();
        var entries  = manifest.Skills!.ToDictionary(e => e.Slug);

        await Assert.That(manifest.Anchor).IsEqualTo(fx.Anchor);
        await Assert.That(manifest.Identity).IsEqualTo(fx.Identity);
        await Assert.That(manifest.Etag).IsEqualTo("etag-1");
        await Assert.That(manifest.SyncedAt).IsEqualTo(SkillsSyncFixture.Now);
        await Assert.That(manifest.Pending).IsFalse();
        await Assert.That(manifest.PendingPrunes!).IsEmpty();
        await Assert.That(manifest.Exposure!).IsEquivalentTo(["claude", "copilot", "cursor", "opencode"]);
        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries["alpha"].Path).IsEqualTo(fx.SkillDir("alpha"));
        await Assert.That(entries["alpha"].Home).IsEqualTo("project:acme");
        await Assert.That(entries["alpha"].Applicability).IsEqualTo("always");
        await Assert.That(entries["beta"].Home).IsEqualTo(fx.RepoHome);

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
        await Assert.That(mainFx.ReadManifest().Anchor).IsEqualTo(main.Path);
        await Assert.That(wtFx.ReadManifest().Anchor).IsEqualTo(linked.Path);
        await Assert.That(wtFx.ReadManifest().Skills!.Single().Path).IsEqualTo(wtFx.SkillDir("alpha"));

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
    public async Task A_hand_authored_skill_beside_kcaps_survives_a_prune() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2"));

        fx.WriteManifest(Owning(fx, fx.Materialize(alpha)) with { Etag = "etag-1" });

        // A kcap- prefix is no claim of ownership: pruning walks the ledger, never the root.
        var authored  = repo.CreateFile([".claude", "skills", "authored", "SKILL.md"], "mine");
        var lookalike = repo.CreateFile([".claude", "skills", "kcap-not-ours", "SKILL.md"], "also mine");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(fx.HasSkill("alpha")).IsFalse();
        await Assert.That(File.ReadAllText(authored)).IsEqualTo("mine");
        await Assert.That(File.ReadAllText(lookalike)).IsEqualTo("also mine");
        await Assert.That(fx.ReadManifest().Skills!).IsEmpty();
    }

    /// <summary>The orphan a rename left behind is deleted on the retry, whatever the snapshot has
    /// since done with the document. The seed is what a crash between the write loop and settlement
    /// leaves: the flag set, the planned entry at its published path with a file hash that matches
    /// it, and the old path in the journal beside the root that authorises deleting it.</summary>
    [Test]
    public async Task A_crash_before_the_prune_removes_the_orphan_once_the_rename_moves_on() {
        using var repo  = Checkout("repo");
        var       half  = SkillsSyncFixture.Skill("beta", RenamedDoc);
        var       again = SkillsSyncFixture.Skill("gamma", RenamedDoc, version: 2);
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-3", again));
        var       stale = repo.CreateFile([".claude", "skills", "kcap-alpha", "SKILL.md"], "superseded");

        fx.WriteManifest(Owning(fx, fx.Materialize(half)) with {
            Etag          = "etag-2", Pending = true,
            PendingPrunes = [new PendingPrune(fx.SkillDir("alpha"), fx.SkillsRoot)],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(fx.HasSkill("beta")).IsFalse();
        await Assert.That(File.ReadAllText(fx.SkillFile("gamma"))).IsEqualTo(Rendered(again));

        var manifest = fx.ReadManifest();

        await Assert.That(manifest.Pending).IsFalse();
        await Assert.That(manifest.PendingPrunes!).IsEmpty();
        await Assert.That(manifest.Skills!.Single().Slug).IsEqualTo("gamma");
        await Assert.That(manifest.Etag).IsEqualTo("etag-3");
        // An interrupted publication names files that may never have landed, so it forfeits the
        // conditional request rather than risking a 304 over a missing one.
        await Assert.That(fx.Api.Requests.Single().Etag).IsNull();
    }

    /// <summary>A document renamed back to a path the journal still owes a deletion for keeps that
    /// copy, republished, and the intent is dropped rather than acted on. The seed is what a crash
    /// between the write loop and settlement leaves: the flag set, the planned entry at its
    /// published path, and the old path in the journal beside its authorising root.</summary>
    [Test]
    public async Task A_crash_before_the_prune_keeps_the_copy_the_rename_came_back_to() {
        using var repo  = Checkout("repo");
        var       half  = SkillsSyncFixture.Skill("beta", RenamedDoc);
        var       back  = SkillsSyncFixture.Skill("alpha", RenamedDoc, version: 2);
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-3", back));

        repo.CreateFile([".claude", "skills", "kcap-alpha", "SKILL.md"], "the previous rendering");
        fx.WriteManifest(Owning(fx, fx.Materialize(half)) with {
            Etag          = "etag-2", Pending = true,
            PendingPrunes = [new PendingPrune(fx.SkillDir("alpha"), fx.SkillsRoot)],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(back));
        await Assert.That(fx.HasSkill("beta")).IsFalse();

        var manifest = fx.ReadManifest();

        await Assert.That(manifest.PendingPrunes!).IsEmpty();
        await Assert.That(manifest.Pending).IsFalse();
        await Assert.That(manifest.Skills!.Single().Slug).IsEqualTo("alpha");
    }

    [Test]
    public async Task An_account_change_retires_both_catalogues_before_a_replacement_that_fails() {
        using var repo     = Checkout("repo");
        var       alpha    = SkillsSyncFixture.Skill("alpha");
        var       fx       = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));
        var       retired  = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       owned    = fx.Materialize(alpha);
        var       globals  = Tmp.CreateDir("home", ".claude", "skills");
        var       global   = globals.PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteManifest(Owning(fx, owned) with { Etag = "etag-1", Identity = retired });
        fx.WriteLegacyManifest(new SkillsManifest {
            Identity = retired, Skills = [owned with { Path = global }],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(fx.HasSkill("alpha")).IsFalse();
        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyManifestPath)).IsFalse();

        var manifest = fx.ReadManifest();

        await Assert.That(manifest.Skills!).IsEmpty();
        await Assert.That(manifest.Identity).IsEqualTo(fx.Identity);
        await Assert.That(manifest.Etag).IsNull();
        // No refresh stamp: a failed replacement must not read as a completed sync.
        await Assert.That(manifest.SyncedAt).IsNull();
        await Assert.That(manifest.PendingPrunes!).IsEmpty();
        await Assert.That(fx.Api.Requests.Single().Etag).IsNull();
        // Nothing settled, so the shared tail never ran.
        await Assert.That(Exclude(fx)).DoesNotContain("kcap skills");
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
        fx.WriteManifest(new SkillsManifest {
            Etag     = "etag-1", SyncedAt = SkillsSyncFixture.Now.AddHours(-1),
            Anchor   = previous.Path, Identity = fx.Identity,
            Skills   = [new SkillsManifestEntry {
                DocId    = alpha.DocId, Slug = alpha.Slug, Version = alpha.Version,
                ContentHash = alpha.ContentHash, Path = oldDir,
                FileHash = SkillsMaterializer.FileHash(Rendered(alpha)), Home = fx.RepoHome,
            }],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(Directory.Exists(oldDir)).IsFalse();

        var manifest = fx.ReadManifest();

        await Assert.That(manifest.Anchor).IsEqualTo(fx.Anchor);
        await Assert.That(manifest.Skills!.Single().Path).IsEqualTo(fx.SkillDir("alpha"));
        await Assert.That(manifest.PendingPrunes!).IsEmpty();
        await Assert.That(manifest.Etag).IsEqualTo("etag-1");
        // The destination is in neither the etag nor the plan, so a manifest at another anchor
        // cannot be allowed to answer a conditional request.
        await Assert.That(fx.Api.Requests.Single().Etag).IsNull();
    }

    /// <summary>A <c>304</c> settles the ledger like any other outcome: the pending flag cleared,
    /// the deletion still owed carried out, the exclusion block rewritten and the legacy copy
    /// retired. A pending manifest forfeits the conditional request, so the double answers the
    /// unconditional one — nothing past the answer may depend on which request produced it.
    /// </summary>
    [Test]
    public async Task A_not_modified_answer_still_settles_excludes_and_migrates() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Unchanged());
        var       owned   = fx.Materialize(alpha);
        var       globals = Tmp.CreateDir("home", ".claude", "skills");
        var       global  = globals.PathTo("kcap-alpha");
        var       orphan  = repo.CreateFile([".claude", "skills", "kcap-gone", "SKILL.md"], "revoked");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteManifest(Owning(fx, owned) with {
            Etag          = "etag-1", Pending = true,
            PendingPrunes = [new PendingPrune(fx.SkillDir("gone"), fx.SkillsRoot)],
        });
        fx.WriteLegacyManifest(new SkillsManifest {
            Identity = fx.Identity, Skills = [owned with { Path = global }],
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var manifest = fx.ReadManifest();

        await Assert.That(manifest.Pending).IsFalse();
        await Assert.That(manifest.PendingPrunes!).IsEmpty();
        await Assert.That(manifest.SyncedAt).IsEqualTo(SkillsSyncFixture.Now);
        await Assert.That(File.Exists(orphan)).IsFalse();
        await Assert.That(fx.HasSkill("alpha")).IsTrue();
        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyManifestPath)).IsFalse();

        var exclude = Exclude(fx);

        foreach (var root in SkillsCommand.Targets())
            await Assert.That(exclude).Contains(
                "/" + root.RelativePath.Replace(Path.DirectorySeparatorChar, '/') + "/kcap-*/");
    }

    /// <summary>The lock-free peek that decides whether to take the migration lock can be
    /// superseded by a peer between the peek and the locked read. The attempt then hands both locks
    /// back for one retry rather than holding a shared lock across a fetch — so nothing is
    /// retired and nothing is requested.</summary>
    [Test]
    public async Task A_retirement_found_under_the_lock_asks_for_a_retry_instead_of_fetching() {
        using var repo   = Checkout("repo");
        var       alpha  = SkillsSyncFixture.Skill("alpha");
        var       fx     = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("never asked"));
        var       target = SkillsCommand.Targets().Single(t => t.Key == SkillsSyncFixture.TargetKey);

        fx.WriteManifest(Owning(fx, fx.Materialize(alpha)) with {
            Etag = "etag-1", Identity = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl),
        });

        var attempt = await fx.Command.AttemptTargetAsync(
            target, fx.Anchor, fx.GitDir, fx.RepoHash, fx.RepoHome, fx.Identity,
            dryRun: false, auto: false, takeMigration: false);

        await Assert.That(attempt.NeedsMigration).IsTrue();
        await Assert.That(attempt.Settled).IsFalse();
        await Assert.That(attempt.Code).IsEqualTo(0);
        await Assert.That(fx.Api.Requests).IsEmpty();
        await Assert.That(fx.HasSkill("alpha")).IsTrue();
        await Assert.That(fx.ReadManifest().Identity!.Account).IsEqualTo("previous-user");
    }

    /// <summary>A checkout with one commit, so a linked worktree can be added to it.</summary>
    GitRepo Checkout(string name) {
        var repo = GitRepo.InitIn(Tmp.CreateDir(name));

        repo.CreateFile("README.md", "initial");
        repo.CommitAll("initial");

        return repo;
    }

    static string Rendered(SkillSnapshotItem item) => SkillsSyncPlanner.RenderSkillFile(item);

    static IEnumerable<string> Materialized(string root) =>
        Directory.GetDirectories(root).Select(d => Path.GetFileName(d)!);

    /// <summary>A settled ledger owning <paramref name="entry"/> under the current identity — the
    /// state a completed sync leaves, for a test that starts from one.</summary>
    static SkillsManifest Owning(SkillsSyncFixture fx, SkillsManifestEntry entry) => new() {
        SyncedAt = SkillsSyncFixture.Now.AddDays(-1), Anchor = fx.Anchor, Identity = fx.Identity,
        Skills   = [entry],
    };

    static string Exclude(SkillsSyncFixture fx) {
        var path = Path.Combine(fx.GitDir, "info", "exclude");

        return File.Exists(path) ? File.ReadAllText(path) : "";
    }
}
