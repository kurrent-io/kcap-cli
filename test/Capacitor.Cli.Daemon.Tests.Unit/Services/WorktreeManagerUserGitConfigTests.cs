using Capacitor.Cli.Daemon.Services;
using Capacitor.Tests.Helpers.Guards;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// What a user's own global git config can switch on must not decide whether a launch's workspace
/// can be built. Bare <c>[NotInParallel]</c>: the global config is the process's <c>GIT_CONFIG_GLOBAL</c>.
/// </summary>
[NotInParallel]
public class WorktreeManagerUserGitConfigTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>The suite's hermetic global config plus the user's settings under test.</summary>
    EnvScope UserGlobalConfig(string settings) {
        var path = Tmp.CreateFile("user.gitconfig", File.ReadAllText(GitConfigGlobalSetup.PinnedGlobalConfig) + "\n" + settings);

        return EnvScope.Exclusive("GIT_CONFIG_GLOBAL", path);
    }

    static WorktreeManager NewManager(string? root = null) =>
        new(root is null ? new DaemonConfig() : new DaemonConfig { WorktreeRoot = root },
            NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

    /// <summary>A superproject with one checked-out submodule, as a user's repository has it.</summary>
    static (GitRepo Super, GitRepo Sub) SuperprojectWithSubmodule() {
        var sub = GitRepo.CreateWithCommit("sub");
        var super = GitRepo.CreateWithCommit("super");

        super.Do("-c", "protocol.file.allow=always", "submodule", "add", sub, "vendored");
        super.Commit("add submodule");

        return (super, sub);
    }

    /// <summary>A linked worktree's submodule has no gitdir of its own, so a reset that recursed into it
    /// would fail the whole checkout.</summary>
    [Test]
    public async Task A_worktree_is_built_in_a_repo_with_a_submodule_when_the_user_recurses_into_submodules() {
        var (super, sub) = SuperprojectWithSubmodule();
        using var _s = super;
        using var _b = sub;
        using var config = UserGlobalConfig("[submodule]\n    recurse = true\n");

        var worktree = await NewManager().CreateAsync(super, name: "agent-recurse");

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(super.Head);
            await Assert.That(File.Exists(Path.Combine(worktree.Path, ".gitmodules"))).IsTrue();
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    /// <summary>With every submodule active, the borrowed snapshot's fresh clone has submodules a recursing
    /// checkout would try to populate, and none of them has a repository yet.</summary>
    [Test]
    public async Task A_borrowed_snapshot_is_built_when_the_user_makes_every_submodule_active_and_recursive() {
        var (super, sub) = SuperprojectWithSubmodule();
        using var _s = super;
        using var _b = sub;
        using var config = UserGlobalConfig("[submodule]\n    recurse = true\n    active = .\n");

        var snapshot = await NewManager(Tmp.PathTo("root")).CreateBorrowedSnapshotAsync(super, "review", CancellationToken.None);

        try {
            await Assert.That(File.Exists(Path.Combine(snapshot.Path, ".gitmodules"))).IsTrue();
        } finally {
            await WorktreeManager.RemoveAsync(snapshot, TimeProvider.System);
        }
    }

    /// <summary>A user who signs every commit would have the daemon's bookkeeping commit signed too, by a
    /// program an unattended daemon cannot drive.</summary>
    [Test]
    public async Task A_standalone_snapshot_is_built_when_the_user_signs_every_commit() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("README.md", "hello");
        using var config = UserGlobalConfig($"[commit]\n    gpgsign = true\n[gpg]\n    program = {Tmp.PathTo("no-such-gpg").Replace('\\', '/')}\n");

        var worktree = await NewManager().CreateAsync(source);

        await Assert.That(worktree.IsStandalone).IsTrue();
        await Assert.That(GitRepo.At(worktree.Path).Try("ls-tree", "--name-only", "HEAD").Text).Contains("README.md");
    }

    /// <summary>A user who converts line endings strictly would have the snapshot refuse an LF file, and an agent
    /// later diffing it would see every file changed. The snapshot keeps the bytes it copied.</summary>
    [Test]
    public async Task A_standalone_snapshot_keeps_lf_files_when_the_user_converts_line_endings_strictly() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("lf.txt", "one\ntwo\n");
        using var config = UserGlobalConfig("[core]\n    autocrlf = true\n    safecrlf = true\n");

        var worktree = await NewManager().CreateAsync(source);

        var snapshot = GitRepo.At(worktree.Path);
        await Assert.That(snapshot.Try("ls-tree", "--name-only", "HEAD").Text).Contains("lf.txt");
        await Assert.That(snapshot.Do("cat-file", "blob", "HEAD:lf.txt").StdOut).IsEqualTo("one\ntwo\n");
        // The snapshot's own config outranks the user's, so the agent's later checkouts and diffs keep LF too.
        await Assert.That(snapshot.Do("config", "--local", "core.autocrlf").Text).IsEqualTo("false");
        await Assert.That(snapshot.Do("config", "--local", "core.safecrlf").Text).IsEqualTo("false");
    }
}
