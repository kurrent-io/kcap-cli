using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class RelativeWorktreesNudgeEmitterTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Repo => Tmp.PathTo("repo");

    UserHome Home => new(Tmp.PathTo("home"));

    void RepoConfig(string content) => Tmp.CreateFile(Path.Combine("repo", ".git", "config"), content);

    string? Resolve(bool sandboxed, string? cwd = null, bool optedOut = false) =>
        RelativeWorktreesNudgeEmitter.Resolve(cwd ?? Repo, optedOut, Home, _ => sandboxed, _ => null);

    [Test]
    public async Task A_sandboxed_repository_without_the_setting_is_nudged() {
        RepoConfig("[core]\n  bare = false\n");

        var nudge = Resolve(sandboxed: true);

        await Assert.That(nudge).IsEqualTo(RelativeWorktreesNudgeEmitter.Text);
        await Assert.That(nudge!).Contains("git config worktree.useRelativePaths true");
        await Assert.That(nudge).Contains("git config kcap.relativeWorktrees declined");
    }

    [Test]
    public async Task A_host_session_is_not_nudged() {
        RepoConfig("[core]\n  bare = false\n");

        await Assert.That(Resolve(sandboxed: false)).IsNull();
    }

    [Test]
    public async Task An_enabled_repository_is_not_nudged() {
        RepoConfig("[worktree]\n  useRelativePaths = true\n");

        await Assert.That(Resolve(sandboxed: true)).IsNull();
    }

    [Test]
    public async Task A_declined_repository_is_not_nudged() {
        RepoConfig("[kcap]\n  relativeWorktrees = declined\n");

        await Assert.That(Resolve(sandboxed: true)).IsNull();
    }

    [Test]
    public async Task Opting_out_suppresses_it() {
        RepoConfig("[core]\n  bare = false\n");

        await Assert.That(Resolve(sandboxed: true, optedOut: true)).IsNull();
    }

    [Test]
    public async Task Outside_a_repository_nothing_is_said() {
        await Assert.That(Resolve(sandboxed: true, cwd: Tmp.CreateDir("plain").Path)).IsNull();
    }

    [Test]
    public async Task A_subdirectory_of_a_linked_worktree_resolves_the_shared_config() {
        RepoConfig("[kcap]\n  relativeWorktrees = declined\n");
        Tmp.CreateDir("repo", ".git", "worktrees", "x");
        Tmp.CreateFile(Path.Combine("repo", ".git", "worktrees", "x", "commondir"), "../..\n");
        Tmp.CreateFile(Path.Combine("repo", ".capacitor", "worktrees", "x", ".git"), $"gitdir: {Tmp.PathTo("repo", ".git", "worktrees", "x")}\n");
        var sub = Tmp.CreateDir("repo", ".capacitor", "worktrees", "x", "src").Path;

        await Assert.That(Resolve(sandboxed: true, cwd: sub)).IsNull();
    }
}
