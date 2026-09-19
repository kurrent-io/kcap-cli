using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Runs that stop part-way and runs that start from what they left. Every case here interrupts a
/// real sync — a plain file where a destination directory has to be created ends the run after the
/// ownership save and before any outcome is recorded — then inspects the disk and the ledger and
/// starts another run over them.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class SkillsOwnershipRecoveryTests {
    [TempDir("skillown")] public required TempDir Tmp { get; init; }

    /// <summary>A rename keeps the document id and changes the slug.</summary>
    static readonly Guid Renamed = new("7c9a1f02-0000-4000-8000-000000000001");

    [Test]
    public async Task A_crash_after_the_write_completes_on_the_next_run_without_rewriting() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));

        fx.Block("beta");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        // A precondition: the file landed and the outcome was never recorded.
        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(fx.RowFor("alpha")!.Prepared).IsNotNull();
        await Assert.That(fx.RowFor("alpha")!.Confirmed).IsNull();

        var written = File.GetLastWriteTimeUtc(fx.SkillFile("alpha")).AddDays(-1);
        File.SetLastWriteTimeUtc(fx.SkillFile("alpha"), written);
        fx.Unblock("beta");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var completed = fx.RowFor("alpha")!;

        await Assert.That(completed.State).IsEqualTo(OwnedSkillState.Published);
        await Assert.That(completed.Prepared).IsNull();
        await Assert.That(completed.Confirmed!.FileHash)
            .IsEqualTo(SkillsMaterializer.FileHash(Rendered(alpha)));
        // Completed rather than rewritten: the bytes were already the intended ones.
        await Assert.That(File.GetLastWriteTimeUtc(fx.SkillFile("alpha"))).IsEqualTo(written);
        await Assert.That(File.ReadAllText(fx.SkillFile("beta"))).IsEqualTo(Rendered(beta));
    }

    /// <summary>Prepare at one anchor, write, stop, and the checkout moves before the retry: the row
    /// still names a path whose file is genuinely absent, while the bytes sit at the path the
    /// operation named under the new anchor.</summary>
    [Test]
    public async Task A_crash_and_then_a_checkout_move_completes_at_the_moved_path() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));

        fx.Block("beta");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        var moved = Tmp.PathTo("moved");

        Directory.Move(repo.Path, moved);

        var after = new SkillsSyncFixture(Tmp, moved, StubSkillsApi.Serving("etag-1", alpha, beta));

        after.Unblock("beta");

        await Assert.That(await after.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var completed = after.RowFor("alpha")!;

        await Assert.That(completed.State).IsEqualTo(OwnedSkillState.Published);
        await Assert.That(completed.Path).IsEqualTo(after.SkillDir("alpha"));
        await Assert.That(completed.Anchor).IsEqualTo(moved);
        await Assert.That(File.ReadAllText(after.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(after.Rows().Count).IsEqualTo(2);
    }

    /// <summary>A first write interrupted after its reservation and before the file leaves a row the
    /// validator accepts, and the destination is never adopted from the hash it was about to
    /// receive.</summary>
    [Test]
    public async Task A_reservation_taken_before_the_file_is_a_row_the_validator_accepts() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));

        fx.Block("alpha");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        var reserved = fx.RowFor("beta")!;

        await Assert.That(reserved.State).IsEqualTo(OwnedSkillState.Reserved);
        await Assert.That(reserved.Confirmed).IsNull();
        await Assert.That(reserved.Prepared!.Intended.FileHash)
            .IsEqualTo(SkillsMaterializer.FileHash(Rendered(beta)));
        await Assert.That(SkillsLedgerValidation.Reject(reserved)).IsNull();
        await Assert.That(Directory.Exists(fx.SkillDir("beta"))).IsFalse();

        fx.Unblock("alpha");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("beta"))).IsEqualTo(Rendered(beta));
        await Assert.That(fx.RowFor("beta")!.State).IsEqualTo(OwnedSkillState.Published);
    }

    /// <summary>A reservation outlives the attempt that made it, so the next run retries the
    /// destination rather than finding the directory the failed attempt created and refusing it as
    /// somebody else's.</summary>
    [Test]
    public async Task A_first_write_that_fails_keeps_its_reservation_and_is_retried() {
        using var repo  = Checkout("repo");
        var       away  = Tmp.CreateDir("away");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));

        fx.Block("alpha");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        // Every destination now leaves the anchor through a link, so every write is refused.
        fx.Unblock("alpha");
        Directory.Delete(fx.SkillsRoot);
        Directory.CreateSymbolicLink(fx.SkillsRoot, away.Path);

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        foreach (var kept in fx.Rows()) {
            await Assert.That(kept.State).IsEqualTo(OwnedSkillState.Reserved);
            await Assert.That(kept.Prepared).IsNull();
            await Assert.That(kept.Confirmed).IsNull();
        }

        await Assert.That(fx.Rows().Count).IsEqualTo(2);
        await Assert.That(Directory.GetFileSystemEntries(away)).IsEmpty();

        Directory.Delete(fx.SkillsRoot);

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("alpha"))).IsEqualTo(Rendered(alpha));
        await Assert.That(File.ReadAllText(fx.SkillFile("beta"))).IsEqualTo(Rendered(beta));
        await Assert.That(fx.Rows().All(r => r.State == OwnedSkillState.Published)).IsTrue();
    }

    /// <summary>The destination a failed first write created is one the reservation still owns, so
    /// the next run writes into it rather than refusing it as somebody else's directory.</summary>
    [Test]
    public async Task A_reservation_owns_the_directory_its_attempt_created() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));

        fx.Block("alpha");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        fx.Unblock("alpha");
        // What a first write that got as far as creating its destination and no further leaves.
        Directory.CreateDirectory(fx.SkillDir("beta"));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(fx.SkillFile("beta"))).IsEqualTo(Rendered(beta));
        await Assert.That(fx.RowFor("beta")!.State).IsEqualTo(OwnedSkillState.Published);
    }

    /// <summary>A reservation is released when the reason for it goes away, taking an empty
    /// directory with it and leaving a non-empty one standing with no claim over it.</summary>
    [Test]
    public async Task A_reservation_whose_document_stops_being_served_is_released() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       gamma = SkillsSyncFixture.Skill("gamma");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path,
                                                StubSkillsApi.Serving("etag-1", alpha, beta, gamma));

        fx.Block("alpha");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);
        await Assert.That(fx.RowFor("beta")!.State).IsEqualTo(OwnedSkillState.Reserved);

        fx.Unblock("alpha");
        // What an interrupted first write leaves at each destination: the directory created, and
        // nothing in one of them.
        Directory.CreateDirectory(fx.SkillDir("beta"));
        new TempDirHandle(fx.SkillDir("gamma")).CreateFile("notes.md", "mine");

        var second = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", alpha));

        await Assert.That(await second.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(Directory.Exists(fx.SkillDir("beta"))).IsFalse();
        await Assert.That(fx.RowFor("beta")).IsNull();
        await Assert.That(File.ReadAllText(Path.Combine(fx.SkillDir("gamma"), "notes.md"))).IsEqualTo("mine");
        await Assert.That(fx.RowFor("gamma")!.State).IsEqualTo(OwnedSkillState.Settled);
        await Assert.That(fx.RowFor("gamma")!.Confirmed).IsNull();
    }

    /// <summary>An operation prepared under one account is resolved before the next one retires
    /// anything: deleting first would compare the old receipt against bytes the old account itself
    /// wrote and refuse its own file. Completing it records what was written and neither publishes
    /// nor revives the row.</summary>
    [Test]
    public async Task An_operation_prepared_under_one_account_is_resolved_and_not_replayed() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       first = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));

        first.Block("beta");

        await Assert.That(await first.Command.HandleSync(dryRun: false)).IsEqualTo(1);
        await Assert.That(File.Exists(first.SkillFile("alpha"))).IsTrue();

        first.Unblock("beta");

        // The next run signs in as somebody else, and its replacement fetch fails.
        var next = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"),
                                         subject: "someone-else");

        await Assert.That(await next.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(next.HasSkill("alpha")).IsFalse();
        await Assert.That(next.HasSkill("beta")).IsFalse();

        var ledger = next.ReadLedger();

        await Assert.That(ledger.Rows).IsEmpty();
        await Assert.That(ledger.Identity!.Account).IsEqualTo("someone-else");
        await Assert.That(ledger.Etag).IsNull();
        await Assert.That(ledger.SyncedAt).IsNull();
    }

    /// <summary>A third party's bytes at a prepared path are refused, reported, and neither adopted
    /// nor deleted — and the document is withheld from the run that found them.</summary>
    [Test]
    [NotInParallel]
    public async Task A_third_partys_bytes_at_a_prepared_path_are_refused() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       beta  = SkillsSyncFixture.Skill("beta");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha, beta));

        fx.Block("beta");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        fx.Unblock("beta");
        File.WriteAllText(fx.SkillFile("alpha"), "written by something that is not kcap");

        string errors;
        using (var console = ConsoleOutput.StartErrorCapture("\n")) {
            await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);
            errors = console.GetCapturedError();
        }

        await Assert.That(errors).Contains(fx.SkillFile("alpha"));
        await Assert.That(File.ReadAllText(fx.SkillFile("alpha")))
            .IsEqualTo("written by something that is not kcap");

        var refused = fx.RowFor("alpha")!;

        await Assert.That(refused.Confirmed).IsNull();
        await Assert.That(refused.Prepared).IsNotNull();
        // Nothing that refused a write records its etag.
        await Assert.That(fx.ReadLedger().Etag).IsNull();
        await Assert.That(fx.ReadLedger().SyncedAt).IsNull();
    }

    /// <summary>A rename interrupted between the write and the deletion, followed by a checkout
    /// move: both copies relocate, the new one completes where its bytes are, and the stale one
    /// goes.</summary>
    [Test]
    public async Task An_interrupted_rename_and_a_checkout_move_relocate_both_copies() {
        using var repo    = Checkout("repo");
        var       before  = SkillsSyncFixture.Skill("alpha", Renamed);
        var       after   = SkillsSyncFixture.Skill("beta", Renamed, version: 2);
        var       gamma   = SkillsSyncFixture.Skill("gamma");
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", before));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var renaming = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2", after, gamma));

        renaming.Block("gamma");

        await Assert.That(await renaming.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        // A precondition: the new copy landed and the old one is still owned and still there.
        await Assert.That(File.Exists(fx.SkillFile("beta"))).IsTrue();
        await Assert.That(File.Exists(fx.SkillFile("alpha"))).IsTrue();

        var moved = Tmp.PathTo("moved");

        Directory.Move(repo.Path, moved);

        var resumed = new SkillsSyncFixture(Tmp, moved, StubSkillsApi.Serving("etag-3", after));

        await Assert.That(await resumed.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(resumed.SkillFile("beta"))).IsEqualTo(Rendered(after));
        await Assert.That(resumed.HasSkill("alpha")).IsFalse();
        await Assert.That(resumed.HasSkill("gamma")).IsFalse();

        var row = resumed.Rows().Single();

        await Assert.That(row.Path).IsEqualTo(resumed.SkillDir("beta"));
        await Assert.That(row.Anchor).IsEqualTo(moved);
        await Assert.That(row.State).IsEqualTo(OwnedSkillState.Published);
    }

    /// <summary>The anchor changes while the files stay where they are: the old path goes on being
    /// owned until the new copy exists, and only then is it deleted.</summary>
    [Test]
    public async Task An_anchor_change_keeps_owning_the_old_files_and_materializes_fresh() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha));
        var       here  = Tmp.CreateDir("launched-from");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var moved = await fx.Command.AttemptTargetAsync(
            fx.Target, here, fx.GitDir, fx.RepoHash, fx.RepoHome, fx.Identity,
            dryRun: false, auto: false, takeMigration: false);

        await Assert.That(moved.Code).IsEqualTo(0);

        var fresh = SkillsMaterializer.SkillDirFor(fx.Target.Root(here), "alpha");

        await Assert.That(File.ReadAllText(SkillsMaterializer.SkillFileFor(fresh))).IsEqualTo(Rendered(alpha));
        await Assert.That(fx.HasSkill("alpha")).IsFalse();

        var row = fx.Rows().Single();

        await Assert.That(row.Path).IsEqualTo(fresh);
        await Assert.That(row.Anchor).IsEqualTo(here.Path);
    }

    /// <summary>A directory holding an authored file beside ours loses only ours, and the row then
    /// holds no claim — not even over a document that comes back to that path.</summary>
    [Test]
    public async Task A_settled_directory_holds_no_claim_over_what_comes_next() {
        using var repo  = Checkout("repo");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha));

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        var authored = new TempDirHandle(fx.SkillDir("alpha")).CreateFile("notes.md", "mine");
        var revoking = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-2"));

        await Assert.That(await revoking.Command.HandleSync(dryRun: false)).IsEqualTo(0);

        await Assert.That(File.Exists(fx.SkillFile("alpha"))).IsFalse();
        await Assert.That(File.ReadAllText(authored)).IsEqualTo("mine");
        await Assert.That(fx.RowFor("alpha")!.State).IsEqualTo(OwnedSkillState.Settled);

        var returning = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-3", alpha));

        await Assert.That(await returning.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(File.Exists(fx.SkillFile("alpha"))).IsFalse();
        await Assert.That(fx.RowFor("alpha")!.State).IsEqualTo(OwnedSkillState.Settled);
    }

    /// <summary>Requesting a replacement never clears a retirement cause: the deletion the previous
    /// account's transition ordered is still owed, and the write that would overwrite it is refused
    /// until it is discharged.</summary>
    [Test]
    public async Task A_replacement_request_does_not_clear_a_retirement_still_owed() {
        using var repo  = Checkout("repo");
        var       away  = Tmp.CreateDir("away");
        var       alpha = SkillsSyncFixture.Skill("alpha");
        var       fx    = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Serving("etag-1", alpha));
        var       body  = Rendered(alpha);

        // The catalogue sits behind a link out of the anchor, so containment refuses its deletion.
        Directory.CreateDirectory(Path.GetDirectoryName(fx.SkillsRoot)!);
        Directory.CreateSymbolicLink(fx.SkillsRoot, away.Path);
        away.CreateFile(["kcap-alpha", "SKILL.md"], body);
        fx.WriteLedger(fx.Owning(fx.Published(alpha, SkillDestination.For(fx.Target, fx.Anchor, "alpha"),
                                              SkillsMaterializer.FileHash(body))) with {
            Etag = "etag-0", Identity = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl),
        });

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        var owed = fx.Rows().Single();

        await Assert.That(owed.Cause).IsEqualTo(SkillDeletionCause.Retired);
        await Assert.That(owed.IdentityRetired!.Account).IsEqualTo("previous-user");
        await Assert.That(File.ReadAllText(away.PathTo("kcap-alpha", "SKILL.md"))).IsEqualTo(body);
        // The fetch under the new identity asked for the same document and was refused its path.
        await Assert.That(fx.Api.Requests.Single().Etag).IsNull();
    }

    /// <summary>An account transition whose legacy cleanup is blocked records the obligation before
    /// the local catalogue's identity is replaced, and retries it on a later run once the blocker
    /// clears — without waiting for a fetch to succeed.</summary>
    [Test]
    public async Task An_account_transition_retries_a_blocked_legacy_retirement() {
        using var repo    = Checkout("repo");
        var       alpha   = SkillsSyncFixture.Skill("alpha");
        var       retired = new SkillsIdentity("previous-user", SkillsSyncFixture.ServerUrl);
        var       fx      = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));
        var       global  = Tmp.CreateDir("home", ".claude", "skills").PathTo("kcap-alpha");

        Tmp.CreateFile(["home", ".claude", "skills", "kcap-alpha", "SKILL.md"], "the global copy");
        fx.WriteLedger(fx.Owning(fx.Materialize(alpha)) with { Etag = "etag-1", Identity = retired });
        fx.WriteLegacyLedger(new SkillsLedger {
            Identity = retired, Owned = [fx.Global(alpha, global, "the global copy")],
        });
        // A sibling ledger that will not parse could be hiding the only other owner of that copy.
        var blocker = Tmp.CreateFile(["config", "skills", "othersibling", "claude", "manifest.json"],
                                     "{ truncated");

        await Assert.That(await fx.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        // The local catalogue is gone and the obligation outlives the identity that ordered it.
        await Assert.That(fx.HasSkill("alpha")).IsFalse();
        await Assert.That(fx.ReadLedger().Identity).IsEqualTo(fx.Identity);
        await Assert.That(fx.ReadLedger().LegacyRetirement).IsEqualTo(retired);
        await Assert.That(Directory.Exists(global)).IsTrue();

        File.Delete(blocker);

        var again = new SkillsSyncFixture(Tmp, repo.Path, StubSkillsApi.Refusing("HTTP 401"));

        await Assert.That(await again.Command.HandleSync(dryRun: false)).IsEqualTo(1);

        await Assert.That(Directory.Exists(global)).IsFalse();
        await Assert.That(File.Exists(fx.LegacyLedgerPath)).IsFalse();
        await Assert.That(fx.ReadLedger().LegacyRetirement).IsNull();
    }

    static string Rendered(SkillSnapshotItem item) => SkillsRendering.RenderSkillFile(item);

    GitRepo Checkout(string name) {
        var repo = GitRepo.InitIn(Tmp.CreateDir(name));

        repo.CreateFile("README.md", "initial");
        repo.CommitAll("initial");

        return repo;
    }
}
