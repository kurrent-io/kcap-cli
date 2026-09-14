using Capacitor.Cli.Commands;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A branch pushed under another name (<c>git push origin HEAD:refs/heads/remote-name</c>, then
/// tracked) is invisible to an argument-free <c>gh pr view</c> under the default push.default: gh
/// cannot resolve a push destination and falls back to the local name. Real git answers the
/// branch-config probes; only gh is faked.
/// </summary>
public class RepositoryDetectionTrackedBranchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string NormalLookup  = "pr view --json number,title,url,headRefName";
    const string TrackedLookup = "pr view remote-name --repo github.com/acme/widget --json number,title,url,headRefName,isCrossRepository";
    const string TrackedPr     = """{"number":874,"title":"T","url":"https://github.com/acme/widget/pull/874","headRefName":"remote-name","isCrossRepository":false}""";

    static CommandRunner RealGitFakeGh(Func<string, string?> gh, List<string>? ghCalls = null) =>
        (cmd, args, cwd, cap) => {
            if (cmd != "gh") return RepositoryDetection.DefaultRunner(cmd, args, cwd, cap);

            ghCalls?.Add(args);

            return Task.FromResult(gh(args));
        };

    static GitRepo PushedUnderAnotherName() {
        var repo = GitRepo.CreateWithCommit();

        repo.AddRemote("git@github.com:acme/widget.git");
        repo.Do("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        repo.Checkout("local-name", create: true);
        repo.Config("branch.local-name.remote", "origin");
        repo.Config("branch.local-name.merge", "refs/heads/remote-name");

        return repo;
    }

    [Test]
    public async Task Finds_the_PR_by_the_tracked_remote_name_when_the_local_name_misses() {
        using var repo = PushedUnderAnotherName();

        // The precondition gh trips on: no push destination under the default push.default.
        await Assert.That(repo.Try("rev-parse", "--symbolic-full-name", "@{push}").ExitCode).IsNotEqualTo(0);

        var ghCalls = new List<string>();
        var payload = await RepositoryDetection.DetectRepositoryAsync(
            Config.Root, repo, run: RealGitFakeGh(args => args == TrackedLookup ? TrackedPr : null, ghCalls));

        await Assert.That(payload!.Branch).IsEqualTo("local-name");
        await Assert.That(payload.PrNumber).IsEqualTo(874);
        await Assert.That(payload.PrHeadRef).IsEqualTo("remote-name");
        await Assert.That(string.Join(" | ", ghCalls)).IsEqualTo($"{NormalLookup} | {TrackedLookup}");
    }

    [Test]
    public async Task A_normal_lookup_hit_never_runs_the_fallback() {
        using var repo = PushedUnderAnotherName();

        var ghCalls = new List<string>();
        var payload = await RepositoryDetection.DetectRepositoryAsync(
            Config.Root, repo,
            run: RealGitFakeGh(args => args == NormalLookup
                ? """{"number":5,"headRefName":"local-name"}"""
                : TrackedPr, ghCalls));

        await Assert.That(payload!.PrNumber).IsEqualTo(5);
        await Assert.That(string.Join(" | ", ghCalls)).IsEqualTo(NormalLookup);
    }

    [Test]
    public async Task A_later_hit_is_a_repository_change_the_watcher_sends() {
        using var repo = PushedUnderAnotherName();

        var prOpened = false;
        var run      = RealGitFakeGh(args => prOpened && args == TrackedLookup ? TrackedPr : null);

        var before = await RepositoryDetection.DetectRepositoryAsync(Config.Root, repo, run: run);
        prOpened = true;
        var after = await RepositoryDetection.DetectRepositoryAsync(Config.Root, repo, run: run);

        await Assert.That(before!.PrNumber).IsNull();
        await Assert.That(after!.PrNumber).IsEqualTo(874);
        await Assert.That(WatchCommand.RepoPayloadChanged(after, before)).IsTrue();
    }
}
