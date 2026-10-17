using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>The repository a hosted session runs in, as its session_started reports it: the origin of
/// the worktree's main checkout, and the branch the worktree itself has out.</summary>
public class HostedSessionRepositoryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_worktree_reports_its_main_checkouts_origin_and_its_own_branch() {
        using var repo = GitRepo.CreateWithCommit();
        repo.Do("remote", "add", "origin", "https://github.com/kurrent-io/widgets.git");
        var worktree = Tmp.PathTo("agent-1");
        repo.Do("worktree", "add", "-b", "capacitor/agent-1", worktree);

        var found = HostedSessionRepository.Read(worktree);

        await Assert.That(found).IsEqualTo(new HostedSessionRepository("kurrent-io", "widgets", "https://github.com/kurrent-io/widgets.git", "capacitor/agent-1"));
    }

    [Test]
    public async Task A_checkout_with_no_origin_reports_none() {
        using var repo = GitRepo.CreateWithCommit();

        await Assert.That(HostedSessionRepository.Read(repo.Path)).IsNull();
    }

    [Test]
    public async Task A_directory_outside_any_repository_reports_none() {
        await Assert.That(HostedSessionRepository.Read(Tmp.CreateDir("plain"))).IsNull();
        await Assert.That(HostedSessionRepository.Read(null)).IsNull();
    }
}
