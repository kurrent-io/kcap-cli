using Capacitor.Cli.Daemon.Services;
using Capacitor.Tests.Helpers.Guards;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

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

    /// <summary>A user's own attributes file is not the project's: its <c>* text=auto</c> would normalise a copied CRLF
    /// file in the snapshot, while the project's <c>.gitattributes</c> still applies.</summary>
    [Test]
    public async Task A_standalone_snapshot_keeps_crlf_files_under_the_users_own_attributes_file() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("crlf.txt", "one\r\ntwo\r\n");
        source.CreateFile("project.txt", "one\r\ntwo\r\n");
        source.CreateFile(".gitattributes", "project.txt text eol=lf\n");
        var attributes = Tmp.CreateFile("user.gitattributes", "* text=auto\n");
        using var config = UserGlobalConfig($"[core]\n    attributesFile = {attributes.Replace('\\', '/')}\n");

        var worktree = await NewManager().CreateAsync(source);

        var snapshot = GitRepo.At(worktree.Path);
        await Assert.That(snapshot.Do("cat-file", "blob", "HEAD:crlf.txt").StdOut).IsEqualTo("one\r\ntwo\r\n");
        await Assert.That(snapshot.Do("cat-file", "blob", "HEAD:project.txt").StdOut).IsEqualTo("one\ntwo\n");
        // The agent's later git commands do not carry GIT_ATTR_NOSYSTEM. The snapshot's attributes file sits above the
        // machine's in git's precedence and below the project's, and its `!text` resets text to unspecified there, so a
        // machine-wide `* text` cannot reach these files.
        await Assert.That(snapshot.Do("check-attr", "text", "crlf.txt").Text).IsEqualTo("crlf.txt: text: unspecified");
        await Assert.That(snapshot.Do("check-attr", "text", "project.txt").Text).IsEqualTo("project.txt: text: set");
        await Assert.That(snapshot.Do("status", "--porcelain").Text).IsEqualTo("");
    }

    /// <summary>The snapshot's attributes file replaces the user's for the agent's later commands, so it keeps the user's
    /// own rules, such as an LFS filter, and resets only their line endings</summary>
    [Test]
    public async Task A_standalone_snapshot_keeps_the_users_own_attribute_rules() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("README.md", "hello");
        var attributes = Tmp.CreateFile("user.gitattributes", "*.psd filter=lfs diff=lfs merge=lfs -text\n*.txt eol=crlf");
        using var config = UserGlobalConfig($"[core]\n    attributesFile = {attributes.Replace('\\', '/')}\n");

        var worktree = await NewManager().CreateAsync(source);

        var snapshot = GitRepo.At(worktree.Path);
        await Assert.That(snapshot.Do("check-attr", "filter", "diff", "--", "art.psd").Text)
            .IsEqualTo("art.psd: filter: lfs\nart.psd: diff: lfs");
        await Assert.That(snapshot.Do("check-attr", "text", "eol", "--", "notes.txt").Text)
            .IsEqualTo("notes.txt: text: unspecified\nnotes.txt: eol: unspecified");
    }

    /// <summary>An empty <c>core.attributesFile</c> turns the user's attributes off, so git's default file is not copied</summary>
    [Test]
    public async Task A_standalone_snapshot_copies_no_rules_when_the_user_turns_attributes_off() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("README.md", "hello");
        Tmp.CreateFile("xdg/git/attributes", "*.psd filter=lfs\n");
        using var xdg    = EnvScope.Exclusive("XDG_CONFIG_HOME", Tmp.PathTo("xdg"));
        using var config = UserGlobalConfig("[core]\n    attributesFile =\n");

        var worktree = await NewManager().CreateAsync(source);

        await Assert.That(GitRepo.At(worktree.Path).Do("check-attr", "filter", "--", "art.psd").Text).IsEqualTo("art.psd: filter: unspecified");
    }

    /// <summary>A relative <c>core.attributesFile</c> names a file in the repository git runs in, the snapshot here</summary>
    [Test]
    public async Task A_standalone_snapshot_keeps_rules_from_a_relative_attributes_file() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile(".attrs", "*.psd filter=lfs\n");
        using var config = UserGlobalConfig("[core]\n    attributesFile = .attrs\n");

        var worktree = await NewManager().CreateAsync(source);

        await Assert.That(GitRepo.At(worktree.Path).Do("check-attr", "filter", "--", "art.psd").Text).IsEqualTo("art.psd: filter: lfs");
    }

    /// <summary>
    /// The machine's attributes file sits below the snapshot's, and git resolves each attribute on its own, so the
    /// snapshot's file must reset <c>eol</c> as well as <c>text</c>: a machine-wide <c>* eol=crlf</c> would otherwise
    /// still convert every file. The machine's file cannot be staged, so the same ranking is built in a scratch
    /// repository, where a subdirectory's attributes outrank the root's.
    /// </summary>
    [Test]
    public async Task The_snapshot_attributes_reset_a_lower_ranked_text_and_eol() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("README.md", "hello");
        var worktree = await NewManager().CreateAsync(source);
        var rules    = File.ReadAllText(GitRepo.At(worktree.Path).Do("config", "--local", "core.attributesFile").Text);

        using var scratch = GitRepo.CreateWithCommit("ranking");
        scratch.CreateFile(".gitattributes", "* text eol=crlf\n");
        scratch.CreateFile(Path.Combine("snapshot", ".gitattributes"), rules);

        await Assert.That(scratch.Do("check-attr", "text", "eol", "--", "snapshot/file.txt").Text)
            .IsEqualTo("snapshot/file.txt: text: unspecified\nsnapshot/file.txt: eol: unspecified");
    }

    /// <summary>A project rule that sets only <c>eol</c> relies on <c>text</c> being unspecified, so the snapshot's own
    /// attributes must not unset it: a checkout then writes the line ending the project asks for.</summary>
    [Test]
    public async Task A_standalone_snapshot_honours_a_project_rule_that_sets_only_eol() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("eol.txt", "one\ntwo\n");
        source.CreateFile(".gitattributes", "eol.txt eol=crlf\n");

        var worktree = await NewManager().CreateAsync(source);

        var snapshot = GitRepo.At(worktree.Path);
        File.Delete(Path.Combine(worktree.Path, "eol.txt"));
        snapshot.Do("checkout", "--", "eol.txt");
        await Assert.That(File.ReadAllText(Path.Combine(worktree.Path, "eol.txt"))).IsEqualTo("one\r\ntwo\r\n");
    }

    /// <summary>The machine's attributes file lives at a path compiled into git, so no test can stage one. This pins
    /// instead that the snapshot's add, diff and commit run with <c>GIT_ATTR_NOSYSTEM</c>, read from a recorder in front
    /// of the real git. The <c>init</c> before them carries no such variable, which proves the recorder reads it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_standalone_snapshot_skips_the_machines_attributes_file() {
        var source = Tmp.CreateDir("proj");
        source.CreateFile("README.md", "hello");
        var inherited = Environment.GetEnvironmentVariable("PATH") ?? "";
        var realGit   = inherited.Split(Path.PathSeparator).Select(dir => Path.Combine(dir, "git")).First(File.Exists);
        var log       = Tmp.PathTo("git.log");
        var bin       = Tmp.CreateDir("bin");
        bin.CreateExecutable("git",
            $"#!/bin/sh\nprintf '%s %s\\n' \"${{GIT_ATTR_NOSYSTEM:-unset}}\" \"$1\" >> '{log}'\nexec '{realGit}' \"$@\"\n");
        using var path = EnvScope.Exclusive("PATH", $"{bin}{Path.PathSeparator}{inherited}");

        await NewManager().CreateAsync(source);

        var calls = File.ReadAllLines(log);
        await Assert.That(calls).Contains("unset init");
        await Assert.That(calls).Contains("1 add");
        await Assert.That(calls).Contains("1 diff");
        await Assert.That(calls).Contains("1 commit");
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
