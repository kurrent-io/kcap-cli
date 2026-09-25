using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

[ParallelLimiter<SubprocessLimit>]
public class GitHookTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly SessionId Session = SessionId.Parse("s1")!;

    GitRepo Repo => field ??= GitRepo.InitIn(Tmp.CreateDir("repo"));

    // Stops at the test process, so a real agent above it on a developer machine never answers.
    AgentSessions Sessions => field ??= new(Config.Root, _ => null);

    Task Hook(params string[] args) =>
        GitHook.RecordAsync(new GitHookInvocation(args, Environment.ProcessId, Repo.Path), Sessions, Config.Root, TimeProvider.System);

    void Claim() => Sessions.Claim(Environment.ProcessId, Session);

    IReadOnlyList<ObservedCommit> Filed() => SessionCommits.Of(Config.Root, Session).ReadFrom(0).Commits;

    [Test]
    public async Task A_commit_is_filed_under_the_claimed_session_with_its_message_redacted() {
        Repo.CreateFile("a.txt", "x");
        Repo.CommitAll("Fix watcher (#12)\n\nAPI_KEY=sk-live-1234567890abcdef");
        Claim();

        await Hook();

        await Assert.That(Filed().Single().Sha).IsEqualTo(Repo.Head);
        await Assert.That(Filed().Single().Message).StartsWith("Fix watcher (#12)");
        await Assert.That(Filed().Single().Message).DoesNotContain("sk-live-1234567890abcdef");
    }

    [Test]
    public async Task No_claimed_process_files_nothing() {
        Repo.CreateFile("a.txt", "x");
        Repo.CommitAll("Fix watcher");

        await Hook();

        await Assert.That(Filed()).IsEmpty();
    }

    [Test]
    public async Task A_merge_is_filed_only_when_git_just_made_it() {
        Repo.CreateFile("a.txt", "x");
        Repo.CommitAll("base");
        var main = Repo.CurrentBranch;
        Repo.Checkout("topic", create: true);
        Repo.CreateFile("b.txt", "y");
        Repo.CommitAll("topic work");
        Repo.Checkout(main);
        Claim();

        Repo.Do("merge", "-q", "--ff-only", "topic");
        await Hook("0");
        await Assert.That(Filed()).IsEmpty();

        Repo.Do("reset", "-q", "--hard", "HEAD~1");
        Repo.Do("merge", "-q", "--no-ff", "--no-edit", "topic");
        await Hook("0");
        await Assert.That(Filed().Single().Sha).IsEqualTo(Repo.Head);
    }

    [Test]
    [Arguments("kcap", true)]
    [Arguments("husky\nkcap\n", true)]
    [Arguments("disabled\tkcap", false)]
    [Arguments(null, false)]
    public async Task Only_an_enabled_kcap_entry_counts_as_listed(string? hookList, bool listed) =>
        await Assert.That(GitHook.ListedIn(hookList)).IsEqualTo(listed);
}
