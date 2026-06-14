using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// Local-attach (Phase 1) behaviours on AgentOrchestrator. Partial of
/// AgentOrchestratorVendorTests to reuse its BuildOrchestrator + test doubles.
public partial class AgentOrchestratorVendorTests {
    static DaemonConfig LauncherCfg() => new() { Name = "t", ServerUrl = "http://127.0.0.1:1" };

    static LauncherContext CtxFor(string path)
        => new("a", path, new WorktreeInfo(path, "", path, IsStandalone: true), null, "", null, null, false, null, null) {
            Work = WorkLocation.BorrowedCwd
        };

    [Test]
    public async Task Claude_borrowed_cwd_prepare_writes_no_repo_files() {
        var dir = Directory.CreateTempSubdirectory("kcap-inplace-");

        try {
            var launcher = new ClaudeLauncher(LauncherCfg(), NullLogger<ClaudeLauncher>.Instance);
            launcher.Prepare(CtxFor(dir.FullName));

            await Assert.That(File.Exists(Path.Combine(dir.FullName, ".mcp.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir.FullName, ".claude", "settings.local.json"))).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(dir.FullName, ".claude"))).IsFalse();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task Claude_passthrough_forwards_user_args_verbatim() {
        var launcher = new ClaudeLauncher(LauncherCfg(), NullLogger<ClaudeLauncher>.Instance);
        var a = launcher.BuildPassthrough(CtxFor("/r"), ["--model", "opus", "fix it"]);
        await Assert.That(a.Args).IsEquivalentTo(new[] { "--model", "opus", "fix it" });
    }

    [Test]
    public async Task Codex_passthrough_injects_mandatory_flags_then_user_args() {
        var launcher = new CodexLauncher(LauncherCfg(), NullLogger<CodexLauncher>.Instance);
        var a = launcher.BuildPassthrough(CtxFor("/r"), ["-m", "gpt"]);
        await Assert.That(a.Args).Contains("--cd");
        await Assert.That(a.Args).Contains("--no-alt-screen");
        await Assert.That(a.Args[^2]).IsEqualTo("-m");
        await Assert.That(a.Args[^1]).IsEqualTo("gpt");
    }

    [Test]
    public async Task Codex_passthrough_rejects_user_duplicate_of_mandatory_flag() {
        var launcher = new CodexLauncher(LauncherCfg(), NullLogger<CodexLauncher>.Instance);
        await Assert.That(() => launcher.BuildPassthrough(CtxFor("/r"), ["--cd", "/elsewhere"]))
            .Throws<ArgumentException>();
    }

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
