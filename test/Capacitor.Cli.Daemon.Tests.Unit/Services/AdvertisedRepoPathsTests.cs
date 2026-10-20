using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A daemon advertises repositories, not the allowlist's patterns: a wildcard entry becomes the
/// repositories under it, so a session inside one matches a path the daemon actually hosts.
public class AdvertisedRepoPathsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Repo(params string[] parts) {
        Tmp.CreateDir([.. parts, ".git"]);
        return Tmp.GetResolvedPath(parts);
    }

    [Test]
    public async Task A_wildcard_becomes_the_repositories_under_it_two_levels_deep() {
        var a     = Repo("repos", "a");
        var b     = Repo("repos", "org", "b");
        Repo("repos", "a", "nested");                 // inside a repository: not searched
        Repo("repos", "x", "y", "too-deep");
        Repo("repos", ".hidden");
        Tmp.CreateDir("repos", "plain");
        var prefix = Tmp.GetResolvedPath("repos");

        var advertised = AdvertisedRepoPaths.Merge([], [prefix + "/*"]);

        await Assert.That(string.Join("|", advertised)).IsEqualTo($"{a}|{b}");
        await Assert.That(advertised).DoesNotContain(prefix).Because("the prefix is not a repository");
    }

    [Test]
    public async Task A_wildcard_over_a_repository_advertises_that_repository() {
        var repo = Repo("solo");

        await Assert.That(AdvertisedRepoPaths.Merge([], [repo + "/*"])).IsEquivalentTo(new[] { repo });
    }

    [Test]
    public async Task Persisted_paths_lead_a_literal_entry_is_kept_as_written_and_duplicates_drop() {
        var a = Repo("repos", "a");
        var prefix = Tmp.GetResolvedPath("repos");

        var advertised = AdvertisedRepoPaths.Merge([a, "/persisted"], ["/literal/worktree", prefix + "/*", "/persisted"]);

        await Assert.That(string.Join("|", advertised)).IsEqualTo($"{a}|/persisted|/literal/worktree");
    }

    /// A linked worktree is advertised as found, so a launch chosen from the list lands in that checkout.
    [Test]
    public async Task A_worktree_under_the_prefix_is_advertised_as_found() {
        var main = Repo("elsewhere", "main");
        var worktree = Tmp.CreateDir("repos", "wt").Path;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "wt")}\n");
        Tmp.CreateDir("elsewhere", "main", ".git", "worktrees", "wt");
        File.WriteAllText(Path.Combine(main, ".git", "worktrees", "wt", "commondir"), "../..\n");

        var advertised = AdvertisedRepoPaths.Merge([], [Tmp.GetResolvedPath("repos") + "/*"]);

        await Assert.That(advertised).IsEquivalentTo(new[] { Tmp.GetResolvedPath("repos", "wt") });
    }

    /// The allowlist matches a literal entry as that exact string, separators included.
    [Test]
    [Arguments("/literal/worktree/")]
    [Arguments("/")]
    [Arguments("C:\\repos\\")]
    public async Task A_literal_entry_is_advertised_exactly_as_written(string entry) =>
        await Assert.That(AdvertisedRepoPaths.Merge([], [entry])).IsEquivalentTo(new[] { entry });

    [Test]
    public async Task No_allowlist_advertises_the_persisted_paths_alone() =>
        await Assert.That(AdvertisedRepoPaths.Merge(["/a", "/b"], [])).IsEquivalentTo(new[] { "/a", "/b" });

    [Test]
    public async Task A_missing_prefix_advertises_nothing() =>
        await Assert.That(AdvertisedRepoPaths.Merge([], [Tmp.PathTo("absent") + "/*"])).IsEmpty();
}
