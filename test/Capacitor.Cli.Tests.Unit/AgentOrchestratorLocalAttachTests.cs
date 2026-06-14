using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// Local-attach (Phase 1) behaviours on AgentOrchestrator. Partial of
/// AgentOrchestratorVendorTests to reuse its BuildOrchestrator + test doubles.
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Borrowed_cwd_cleanup_does_not_delete_user_dir_or_branch() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server = new CaptureServerConnection();
            await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

            // IsStandalone:true means RemoveAsync WOULD Directory.Delete this path —
            // the Work=BorrowedCwd guard must prevent that.
            var agent = new AgentInstance(
                "local-1", null, "", null, repoPath, "claude",
                new StubPtyProcess(), new WorktreeInfo(repoPath, "", repoPath, IsStandalone: true), new CancellationTokenSource()
            ) {
                IsPrivate = true,
                Work      = WorkLocation.BorrowedCwd
            };

            orch.RegisterAgentForTest(agent);
            await orch.CleanupAgentForTest("local-1");

            await Assert.That(Directory.Exists(repoPath)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(repoPath, "README.md"))).IsTrue();
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Owned_worktree_cleanup_still_removes_it() {
        var dir = Directory.CreateTempSubdirectory("kcap-owned-");

        try {
            var server = new CaptureServerConnection();
            await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

            var agent = new AgentInstance(
                "owned-1", null, "", null, dir.FullName, "claude",
                new StubPtyProcess(), new WorktreeInfo(dir.FullName, "", dir.FullName, IsStandalone: true), new CancellationTokenSource()
            ) {
                Work = WorkLocation.OwnedWorktree
            };

            orch.RegisterAgentForTest(agent);
            await orch.CleanupAgentForTest("owned-1");

            await Assert.That(Directory.Exists(dir.FullName)).IsFalse();
        } finally {
            try { Directory.Delete(dir.FullName, true); } catch { /* already gone — that's the assertion */ }
        }
    }
}
