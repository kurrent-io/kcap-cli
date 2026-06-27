using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Validates <see cref="WorktreeManager.CreateAsync"/> with a baseRef param.
/// We build a real local git repo with two commits on a side ref so we can
/// fetch it back as if it were a PR head and assert the worktree HEAD lines up.
/// </summary>
/// <remarks>
/// Also covers <see cref="WorktreeManager.SyncFromSourceAsync"/>: new/modified files are
/// copied from source into the worktree, deleted source files are removed from the worktree,
/// and special paths (<c>.git/</c>, excluded paths) are never touched.
/// </remarks>
public class WorktreeManagerTests {
    static (string upstream, string clone) MakeUpstreamWithSideRef(string sideRefName, out string sideCommitSha) {
        var upstream = Path.Combine(Path.GetTempPath(), "kcap-upstream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(upstream);

        Git(upstream, "init", "-q");
        Git(upstream, "config", "user.email", "test@example.com");
        Git(upstream, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(upstream, "main.txt"), "main");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "initial");

        // Capture the default branch name; git's default has shifted from
        // master to main and varies by user config.
        var defaultBranch = GitCapture(upstream, "branch", "--show-current").Trim();

        // Create a second commit on a detached side branch and store it under
        // a custom ref so the clone can fetch it like a PR head.
        Git(upstream, "checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(upstream, "side.txt"), "side");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "side commit");
        sideCommitSha = GitCapture(upstream, "rev-parse", "HEAD").Trim();
        Git(upstream, "update-ref", sideRefName, sideCommitSha);
        Git(upstream, "checkout", "-q", defaultBranch);
        Git(upstream, "branch", "-D", "side");

        // Allow `git clone` of a non-bare repo over the file:// protocol.
        Git(upstream, "config", "uploadpack.allowAnySHA1InWant", "true");

        var clone = Path.Combine(Path.GetTempPath(), "kcap-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Git(Path.GetTempPath(), "clone", "-q", upstream, clone);

        return (upstream, clone);
    }

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }

    static string GitCapture(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        return proc.ExitCode != 0 ? throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}") : proc.StandardOutput.ReadToEnd();
    }

    [Test]
    public async Task CreateAsync_WithBaseRef_WorktreeHeadMatchesFetchedCommit() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/42/head", out var sideSha);

        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone, name: "review-pr-42", baseRef: "refs/pull/42/head");

            try {
                var head = GitCapture(worktree.Path, "rev-parse", "HEAD").Trim();

                await Assert.That(head).IsEqualTo(sideSha);
                await Assert.That(worktree.Branch).IsEqualTo("capacitor/review-pr-42");
            } finally {
                await WorktreeManager.RemoveAsync(worktree);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }

    [Test]
    public async Task CreateAsync_WithoutBaseRef_StillWorks() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/1/head", out _);

        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone);

            try {
                await Assert.That(Directory.Exists(worktree.Path)).IsTrue();
                await Assert.That(worktree.Branch).StartsWith("capacitor/");
            } finally {
                await WorktreeManager.RemoveAsync(worktree);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }

    /// <summary>
    /// Concurrent review launches against the same source repo previously
    /// raced on the shared <c>FETCH_HEAD</c> ref — fetch N would land on
    /// <c>FETCH_HEAD</c> after fetch M, then worktree-add for M would create
    /// the wrong commit. The fix routes each fetch into a per-worktree
    /// <c>refs/kcap/review/{name}</c> and worktree-adds from that ref.
    /// This test asserts each worktree HEAD lines up with the SHA we asked
    /// for, even when 5 launches are issued in parallel.
    /// </summary>
    [Test]
    public async Task CreateAsync_ConcurrentBaseRefs_EachWorktreePinnedToCorrectSha() {
        var upstream = Path.Combine(Path.GetTempPath(), "kcap-upstream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(upstream);

        Git(upstream, "init", "-q");
        Git(upstream, "config", "user.email", "test@example.com");
        Git(upstream, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(upstream, "main.txt"), "main");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "initial");

        var defaultBranch = GitCapture(upstream, "branch", "--show-current").Trim();

        // Build 5 distinct side commits, each saved as its own ref so the clone
        // can fetch them as if they were PR heads.
        const int concurrency = 5;
        var       refs        = new (string RefName, string Sha)[concurrency];

        for (var i = 0; i < concurrency; i++) {
            var refName = $"refs/pull/{100 + i}/head";
            Git(upstream, "checkout", "-q", "-b", $"side-{i}");
            File.WriteAllText(Path.Combine(upstream, $"side-{i}.txt"), $"side-{i}");
            Git(upstream, "add", "-A");
            Git(upstream, "commit", "-q", "-m", $"side {i}");
            var sha = GitCapture(upstream, "rev-parse", "HEAD").Trim();
            Git(upstream, "update-ref", refName, sha);
            Git(upstream, "checkout", "-q", defaultBranch);
            Git(upstream, "branch", "-D", $"side-{i}");
            refs[i] = (refName, sha);
        }

        var clone = Path.Combine(Path.GetTempPath(), "kcap-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Git(Path.GetTempPath(), "clone", "-q", upstream, clone);

        try {
            var manager   = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktrees = new WorktreeInfo[concurrency];

            await Task.WhenAll(
                Enumerable.Range(0, concurrency)
                    .Select(async i => {
                            worktrees[i] = await manager.CreateAsync(clone, name: $"review-{i}", baseRef: refs[i].RefName);
                        }
                    )
            );

            try {
                for (var i = 0; i < concurrency; i++) {
                    var head = GitCapture(worktrees[i].Path, "rev-parse", "HEAD").Trim();
                    await Assert.That(head).IsEqualTo(refs[i].Sha);
                    await Assert.That(worktrees[i].FetchedRef).IsEqualTo($"refs/kcap/review/review-{i}");
                }
            } finally {
                foreach (var w in worktrees) {
                    if (w is not null) await WorktreeManager.RemoveAsync(w);
                }
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }

    // ── SyncFromSourceAsync tests ─────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal source git repo (no upstream) plus a pre-existing target worktree dir.
    /// Returns (sourcePath, targetPath).
    /// </summary>
    static (string source, string target) MakeSourceAndTarget(out string sourceFile) {
        var source = Path.Combine(Path.GetTempPath(), "kcap-src-" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(Path.GetTempPath(), "kcap-tgt-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        Git(source, "init", "-q");
        Git(source, "config", "user.email", "test@example.com");
        Git(source, "config", "user.name", "Test");

        sourceFile = Path.Combine(source, "hello.txt");
        File.WriteAllText(sourceFile, "hello");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "initial");

        // Also init target as a git repo so it looks like a real worktree.
        Git(target, "init", "-q");
        Git(target, "config", "user.email", "test@example.com");
        Git(target, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(target, "hello.txt"), "old-content");
        Git(target, "add", "-A");
        Git(target, "commit", "-q", "-m", "initial");

        return (source, target);
    }

    /// <summary>
    /// A new uncommitted file in the source is copied into the worktree, and a
    /// pre-existing file whose content changed is overwritten.
    /// </summary>
    [Test]
    public async Task SyncFromSourceAsync_CopiesNewAndModifiedFiles() {
        var (source, target) = MakeSourceAndTarget(out var sourceHello);

        try {
            // Modify an existing tracked file (uncommitted change in source).
            await File.WriteAllTextAsync(sourceHello, "updated-hello");

            // Add a brand-new untracked file.
            var newFile = Path.Combine(source, "new-untracked.txt");
            await File.WriteAllTextAsync(newFile, "new content");

            var manager = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            await manager.SyncFromSourceAsync(source, target, [], CancellationToken.None);

            // Modified tracked file must be overwritten.
            var targetHello = Path.Combine(target, "hello.txt");
            await Assert.That(File.Exists(targetHello)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(targetHello)).IsEqualTo("updated-hello");

            // Untracked file must be present in target.
            var targetNew = Path.Combine(target, "new-untracked.txt");
            await Assert.That(File.Exists(targetNew)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(targetNew)).IsEqualTo("new content");
        } finally {
            try { Directory.Delete(source, true); } catch { /* best-effort */ }
            try { Directory.Delete(target, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A file that exists in the target worktree but is absent from the source (simulating a
    /// deletion in source) is removed from the target after sync.
    /// </summary>
    [Test]
    public async Task SyncFromSourceAsync_DeletesFilesRemovedInSource() {
        var (source, target) = MakeSourceAndTarget(out _);

        try {
            // Place an extra file in the target that does NOT exist in the source.
            var staleFile = Path.Combine(target, "stale.txt");
            await File.WriteAllTextAsync(staleFile, "should be deleted");

            var manager = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            await manager.SyncFromSourceAsync(source, target, [], CancellationToken.None);

            // Stale file must have been deleted.
            await Assert.That(File.Exists(staleFile)).IsFalse();

            // The tracked source file must still be present.
            await Assert.That(File.Exists(Path.Combine(target, "hello.txt"))).IsTrue();
        } finally {
            try { Directory.Delete(source, true); } catch { /* best-effort */ }
            try { Directory.Delete(target, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Files under <c>.git/</c> in the target are never touched (the git plumbing must
    /// remain intact), and paths matching the caller-supplied <paramref name="excludePaths"/>
    /// are neither copied from source nor deleted from target.
    /// </summary>
    [Test]
    public async Task SyncFromSourceAsync_SkipsGitDirAndExcludedPaths() {
        var (source, target) = MakeSourceAndTarget(out _);

        try {
            // Write a sentinel into .git/ inside the target — it must survive the sync.
            var gitSentinel = Path.Combine(target, ".git", "SENTINEL");
            await File.WriteAllTextAsync(gitSentinel, "untouched");

            // Write a file in an excluded directory inside the target — must survive.
            var excludedDir = Path.Combine(target, ".excluded-dir");
            Directory.CreateDirectory(excludedDir);
            var excludedFile = Path.Combine(excludedDir, "keep.txt");
            await File.WriteAllTextAsync(excludedFile, "kept");

            // Also write the excluded dir name into source as an untracked file so the
            // exclude logic is exercised on the copy side too.
            var sourceExcludedDir = Path.Combine(source, ".excluded-dir");
            Directory.CreateDirectory(sourceExcludedDir);
            await File.WriteAllTextAsync(Path.Combine(sourceExcludedDir, "should-not-appear.txt"), "x");

            var manager = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            await manager.SyncFromSourceAsync(source, target, [".excluded-dir"], CancellationToken.None);

            // .git sentinel must be intact.
            await Assert.That(File.Exists(gitSentinel)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(gitSentinel)).IsEqualTo("untouched");

            // Excluded dir file must be intact.
            await Assert.That(File.Exists(excludedFile)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(excludedFile)).IsEqualTo("kept");

            // The excluded-dir source file must NOT have been copied into target.
            await Assert.That(File.Exists(Path.Combine(target, ".excluded-dir", "should-not-appear.txt"))).IsFalse();
        } finally {
            try { Directory.Delete(source, true); } catch { /* best-effort */ }
            try { Directory.Delete(target, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The fetched ref should be cleaned up by <see cref="WorktreeManager.RemoveAsync"/>
    /// so the source repo doesn't accumulate stale per-worktree refs after
    /// many review launches.
    /// </summary>
    [Test]
    public async Task RemoveAsync_DeletesFetchedRef() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/77/head", out _);

        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone, name: "review-77", baseRef: "refs/pull/77/head");

            await Assert.That(worktree.FetchedRef).IsEqualTo("refs/kcap/review/review-77");

            // Sanity: ref exists before cleanup.
            var beforeRefs = GitCapture(clone, "for-each-ref", "refs/kcap/review/").Trim();
            await Assert.That(beforeRefs).Contains("refs/kcap/review/review-77");

            await WorktreeManager.RemoveAsync(worktree);

            var afterRefs = GitCapture(clone, "for-each-ref", "refs/kcap/review/").Trim();
            await Assert.That(afterRefs).IsEmpty();
        } finally {
            try { Directory.Delete(upstream, true); } catch {
                /* best-effort */
            }

            try { Directory.Delete(clone, true); } catch {
                /* best-effort */
            }
        }
    }
}
