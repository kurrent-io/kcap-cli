using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class GitRepositoryTests {
    [Test]
    public async Task FindRoot_returns_null_for_directory_with_no_git_entry_anywhere() {
        var tmp = Directory.CreateTempSubdirectory("kcap-git-test-");
        try {
            var nested = Path.Combine(tmp.FullName, "a", "b", "c");
            Directory.CreateDirectory(nested);

            await Assert.That(GitRepository.FindRoot(nested)).IsNull();
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task FindRoot_returns_directory_when_dot_git_directory_is_present() {
        var tmp = Directory.CreateTempSubdirectory("kcap-git-test-");
        try {
            Directory.CreateDirectory(Path.Combine(tmp.FullName, ".git"));

            await Assert.That(GitRepository.FindRoot(tmp.FullName)).IsEqualTo(tmp.FullName);
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task FindRoot_returns_directory_when_dot_git_is_a_file_as_in_worktrees_or_submodules() {
        var tmp = Directory.CreateTempSubdirectory("kcap-git-test-");
        try {
            await File.WriteAllTextAsync(Path.Combine(tmp.FullName, ".git"), "gitdir: ../parent/.git/worktrees/x\n");

            await Assert.That(GitRepository.FindRoot(tmp.FullName)).IsEqualTo(tmp.FullName);
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task FindRoot_walks_up_and_returns_the_ancestor_holding_the_dot_git_entry() {
        var tmp = Directory.CreateTempSubdirectory("kcap-git-test-");
        try {
            Directory.CreateDirectory(Path.Combine(tmp.FullName, ".git"));
            var nested = Path.Combine(tmp.FullName, "a", "b", "c");
            Directory.CreateDirectory(nested);

            await Assert.That(GitRepository.FindRoot(nested)).IsEqualTo(tmp.FullName);
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task FindRoot_returns_null_for_empty_input() {
        await Assert.That(GitRepository.FindRoot("")).IsNull();
    }

    [Test]
    public async Task IsInsideRepo_matches_FindRoot_result() {
        var tmp = Directory.CreateTempSubdirectory("kcap-git-test-");
        try {
            await Assert.That(GitRepository.IsInsideRepo(tmp.FullName)).IsFalse();

            Directory.CreateDirectory(Path.Combine(tmp.FullName, ".git"));

            await Assert.That(GitRepository.IsInsideRepo(tmp.FullName)).IsTrue();
        } finally {
            tmp.Delete(recursive: true);
        }
    }
}
