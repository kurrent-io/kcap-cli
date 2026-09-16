namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="GitRepoKey"/>. The key is what a repo allow or deny list compares
/// against, so both halves have to hold: a real checkout must actually RESOLVE — a key generator
/// that only ever returned null would refuse every repository under an allowlist while looking
/// correct from the fail-closed side — and a checkout with nothing to resolve must come back null
/// rather than guess.
/// </summary>
public class GitRepoKeyTests {
    [Test]
    public async Task An_ssh_origin_resolves_to_owner_and_repo() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("git@github.com:acme/widgets.git");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsEqualTo("acme/widgets");
    }

    [Test]
    public async Task An_https_origin_resolves_to_the_same_key() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("https://github.com/acme/widgets.git");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsEqualTo("acme/widgets");
    }

    // The mode-independence the daemon's gate rests on: a reviewer borrowing a linked worktree and
    // one running in the primary checkout must produce ONE key, or a repo list would admit the same
    // repository under one containment and refuse it under another.
    [Test]
    public async Task A_linked_worktree_resolves_to_the_repository_behind_it() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("git@github.com:acme/widgets.git");

        var worktree = repo.AddWorktree("linked", "side");

        await Assert.That(GitRepoKey.ForCheckout(worktree.Path)).IsEqualTo("acme/widgets");
    }

    [Test]
    public async Task A_subdirectory_resolves_to_its_repositorys_key() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("git@github.com:acme/widgets.git");

        var nested = repo.CreateDir("src", "deep");

        await Assert.That(GitRepoKey.ForCheckout(nested.Path)).IsEqualTo("acme/widgets");
    }

    [Test]
    public async Task A_repository_with_no_origin_remote_has_no_key() {
        using var repo = GitRepo.CreateWithCommit();

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsNull();
    }

    [Test]
    public async Task A_remote_that_is_not_a_recognizable_git_url_has_no_key() {
        using var repo = GitRepo.CreateWithCommit();
        repo.AddRemote("not-a-url");

        await Assert.That(GitRepoKey.ForCheckout(repo.Path)).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task A_path_that_names_nothing_has_no_key(string? path) {
        await Assert.That(GitRepoKey.ForCheckout(path)).IsNull();
        await Assert.That(GitRepoKey.ForMainRepoRoot(path)).IsNull();
    }
}
