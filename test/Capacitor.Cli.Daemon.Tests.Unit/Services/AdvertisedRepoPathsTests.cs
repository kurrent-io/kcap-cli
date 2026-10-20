using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A daemon advertises repositories, not the allowlist's patterns: a wildcard entry becomes the
/// repositories under it, so a session inside one matches a path the daemon actually hosts.
public class AdvertisedRepoPathsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Repo(params string[] parts) {
        var dir = Tmp.CreateDir(parts).Path;
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
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

        var advertised = AdvertisedRepoPaths.Merge([a, "/persisted"], ["/literal/worktree/", prefix + "/*", "/persisted"]);

        await Assert.That(string.Join("|", advertised)).IsEqualTo($"{a}|/persisted|/literal/worktree");
    }

    [Test]
    public async Task No_allowlist_advertises_the_persisted_paths_alone() =>
        await Assert.That(AdvertisedRepoPaths.Merge(["/a", "/b"], [])).IsEquivalentTo(new[] { "/a", "/b" });

    [Test]
    public async Task A_missing_prefix_advertises_nothing() =>
        await Assert.That(AdvertisedRepoPaths.Merge([], [Tmp.PathTo("absent") + "/*"])).IsEmpty();
}
