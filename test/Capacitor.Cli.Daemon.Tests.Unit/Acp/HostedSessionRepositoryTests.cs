using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>The repository a hosted session runs in, as its session_started reports it: the origin of
/// the worktree's main checkout, and the branch the worktree itself has out.</summary>
[ParallelLimiter<SubprocessLimit>]
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
    public async Task A_session_in_a_subdirectory_reports_its_checkouts_origin() {
        using var repo = GitRepo.CreateWithCommit();
        repo.Do("remote", "add", "origin", "https://github.com/kurrent-io/widgets.git");
        var sub = Directory.CreateDirectory(Path.Combine(repo.Path, "src", "app")).FullName;

        await Assert.That(HostedSessionRepository.Read(sub)?.Name).IsEqualTo("widgets");
    }

    /// <summary>A submodule's <c>.git</c> is a file pointing into the superproject's
    /// <c>.git/modules/</c>, where its own origin lives.</summary>
    [Test]
    public async Task A_session_in_a_submodule_reports_the_submodules_origin() {
        using var inner = GitRepo.CreateWithCommit();
        using var outer = GitRepo.CreateWithCommit();
        outer.Do("-c", "protocol.file.allow=always", "submodule", "add", inner.Path, "gadgets");
        var submodule = Path.Combine(outer.Path, "gadgets");
        GitRepo.At(submodule).Do("remote", "set-url", "origin", "https://github.com/kurrent-io/gadgets.git");

        var found = HostedSessionRepository.Read(submodule);

        await Assert.That(found?.Owner).IsEqualTo("kurrent-io");
        await Assert.That(found?.Name).IsEqualTo("gadgets");
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
