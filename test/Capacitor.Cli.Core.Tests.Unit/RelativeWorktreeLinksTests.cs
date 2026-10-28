namespace Capacitor.Cli.Core.Tests.Unit;

public class RelativeWorktreeLinksTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string CommonDir => Tmp.PathTo("repo", ".git");

    UserHome Home => new(Tmp.PathTo("home"));

    static string? NoEnv(string _) => null;

    void RepoConfig(string content) => Tmp.CreateFile(Path.Combine("repo", ".git", "config"), content);

    [Test]
    public async Task Unset_everywhere_is_not_enabled() {
        RepoConfig("[core]\n  bare = false\n");

        await Assert.That(RelativeWorktreeLinks.IsEnabled(CommonDir, Home, NoEnv)).IsFalse();
    }

    [Test]
    public async Task The_repository_config_enables_it() {
        RepoConfig("[worktree]\n  useRelativePaths = true\n");

        await Assert.That(RelativeWorktreeLinks.IsEnabled(CommonDir, Home, NoEnv)).IsTrue();
    }

    [Test]
    public async Task The_global_config_enables_it_unless_the_repository_says_otherwise() {
        Tmp.CreateFile(Path.Combine("home", ".gitconfig"), "[worktree]\n  useRelativePaths = true\n");
        RepoConfig("[core]\n  bare = false\n");

        await Assert.That(RelativeWorktreeLinks.IsEnabled(CommonDir, Home, NoEnv)).IsTrue();

        RepoConfig("[worktree]\n  useRelativePaths = false\n");

        await Assert.That(RelativeWorktreeLinks.IsEnabled(CommonDir, Home, NoEnv)).IsFalse();
    }

    [Test]
    public async Task The_xdg_config_counts_as_global() {
        Tmp.CreateFile(Path.Combine("xdg", "git", "config"), "[worktree]\n  useRelativePaths = true\n");
        var xdg = Tmp.PathTo("xdg");

        await Assert.That(RelativeWorktreeLinks.IsEnabled(CommonDir, Home, v => v == "XDG_CONFIG_HOME" ? xdg : null)).IsTrue();
    }

    [Test]
    public async Task GIT_CONFIG_GLOBAL_replaces_the_home_config() {
        Tmp.CreateFile(Path.Combine("home", ".gitconfig"), "[worktree]\n  useRelativePaths = true\n");
        var replacement = Tmp.CreateFile("other.gitconfig", "[user]\n  name = me\n");

        await Assert.That(RelativeWorktreeLinks.IsEnabled(CommonDir, Home, v => v == "GIT_CONFIG_GLOBAL" ? replacement : null)).IsFalse();
    }

    [Test]
    public async Task Declining_is_read_from_the_repository_config() {
        RepoConfig("[core]\n  bare = false\n");
        await Assert.That(RelativeWorktreeLinks.IsDeclined(CommonDir)).IsFalse();

        RepoConfig("[kcap]\n  relativeWorktrees = declined\n");
        await Assert.That(RelativeWorktreeLinks.IsDeclined(CommonDir)).IsTrue();
    }
}
