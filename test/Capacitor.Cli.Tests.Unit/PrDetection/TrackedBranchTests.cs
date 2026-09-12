using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.PrDetection;

public class TrackedBranchTests {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>A github.com/acme/widget clone on <c>local-name</c>, tracking <paramref name="merge"/>
    /// on <paramref name="remote"/> (or nothing when <paramref name="merge"/> is null).</summary>
    static GitRepo Repo(string? merge = "refs/heads/remote-name", string remote = "origin", bool knownDefault = true) {
        var repo = GitRepo.CreateWithCommit();

        repo.AddRemote("git@github.com:acme/widget.git");
        if (knownDefault) repo.Do("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        repo.Checkout("local-name", create: true);

        if (merge is not null) {
            repo.Config("branch.local-name.remote", remote);
            repo.Config("branch.local-name.merge", merge);
        }

        return repo;
    }

    static Task<string?> Resolve(GitRepo repo, string? branch = "local-name") =>
        TrackedBranch.ResolveAsync(
            branch, "github.com", "acme", "widget", repo, () => Budget, RepositoryDetection.DefaultRunner);

    [Test]
    public async Task Resolves_an_upstream_that_goes_by_another_name() {
        using var repo = Repo();
        await Assert.That(await Resolve(repo)).IsEqualTo("remote-name");
    }

    [Test]
    public async Task An_upstream_of_the_same_name_resolves_nothing() {
        using var repo = Repo(merge: "refs/heads/local-name");
        await Assert.That(await Resolve(repo)).IsNull();
    }

    [Test]
    public async Task No_upstream_resolves_nothing() {
        using var repo = Repo(merge: null);
        await Assert.That(await Resolve(repo)).IsNull();
    }

    [Test]
    public async Task A_detached_head_resolves_nothing() {
        using var repo = Repo();
        repo.Do("checkout", "-q", "--detach");

        await Assert.That(repo.CurrentBranch).IsEmpty();
        await Assert.That(await Resolve(repo, branch: repo.CurrentBranch)).IsNull();
    }

    [Test]
    public async Task An_upstream_on_a_fork_resolves_nothing() {
        using var repo = Repo(remote: "fork");
        repo.AddRemote("git@github.com:someone/widget.git", "fork");
        repo.Do("symbolic-ref", "refs/remotes/fork/HEAD", "refs/remotes/fork/main");

        await Assert.That(await Resolve(repo)).IsNull();
    }

    [Test]
    public async Task An_upstream_on_another_remote_of_the_same_repository_resolves() {
        using var repo = Repo(remote: "mirror");
        repo.AddRemote("https://github.com/Acme/Widget.git", "mirror");
        repo.Do("symbolic-ref", "refs/remotes/mirror/HEAD", "refs/remotes/mirror/main");

        await Assert.That(await Resolve(repo)).IsEqualTo("remote-name");
    }

    [Test]
    public async Task A_local_upstream_resolves_nothing() {
        using var repo = Repo(remote: ".");
        await Assert.That(await Resolve(repo)).IsNull();
    }

    [Test]
    public async Task Tracking_the_remote_default_branch_resolves_nothing() {
        using var repo = Repo(merge: "refs/heads/main");
        await Assert.That(await Resolve(repo)).IsNull();
    }

    /// <summary>Without the remote's HEAD there is no telling the default branch apart from a
    /// feature branch, so the fallback does not guess.</summary>
    [Test]
    public async Task An_unknown_remote_default_resolves_nothing() {
        using var repo = Repo(knownDefault: false);
        await Assert.That(await Resolve(repo)).IsNull();
    }

    static CommandRunner FakeGit(string merge, List<string> calls) =>
        (_, args, _, _) => {
            calls.Add(args);

            string? reply = args switch {
                "config --get branch.local-name.remote" => "origin",
                "config --get branch.local-name.merge"  => merge,
                "remote get-url origin"                 => "git@github.com:acme/widget.git",
                _ when args.StartsWith("symbolic-ref ", StringComparison.Ordinal) => "refs/remotes/origin/main",
                _ => null
            };

            return Task.FromResult(reply);
        };

    /// <summary>The first row keeps the fake honest: a plain name resolves through it.</summary>
    [Test]
    [Arguments("refs/heads/remote-name", "remote-name")]
    [Arguments("refs/heads/feat%x,1=2", "feat%x,1=2")] // legal in a ref, and one argument
    [Arguments("refs/heads/x --repo evil/repo", null)]
    [Arguments("refs/heads/-x", null)]
    [Arguments("refs/heads/a\"b", null)]
    public async Task Only_a_plain_tracked_name_resolves(string merge, string? expected) {
        var tracked = await TrackedBranch.ResolveAsync(
            "local-name", "github.com", "acme", "widget", "/cwd", () => Budget, FakeGit(merge, []));

        await Assert.That(tracked).IsEqualTo(expected);
    }

    [Test]
    public async Task An_unsafe_local_branch_is_never_passed_to_git() {
        var calls = new List<string>();

        var tracked = await TrackedBranch.ResolveAsync(
            "a\"b", "github.com", "acme", "widget", "/cwd", () => Budget, FakeGit("refs/heads/remote-name", calls));

        await Assert.That(tracked).IsNull();
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task A_spent_budget_spawns_nothing() {
        var calls = new List<string>();

        var tracked = await TrackedBranch.ResolveAsync(
            "local-name", "github.com", "acme", "widget", "/cwd", () => TimeSpan.Zero,
            FakeGit("refs/heads/remote-name", calls));

        await Assert.That(tracked).IsNull();
        await Assert.That(calls).IsEmpty();
    }
}
