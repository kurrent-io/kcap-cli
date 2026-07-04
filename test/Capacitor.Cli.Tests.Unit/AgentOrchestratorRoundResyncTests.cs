using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1163 follow-up: a mirror-requester review-flow reviewer stays alive across rounds to keep its
/// context, so its daemon-created worktree must be re-mirrored from the requester's current working
/// tree before EACH round is delivered — otherwise the reviewer keeps reviewing round 1's snapshot
/// and never sees files the requester created/edited while addressing findings. These tests drive
/// <see cref="AgentOrchestrator.HandleSendInput"/> and assert the re-mirror happens (and only for a
/// mirror-source, daemon-owned worktree).
/// </summary>
public partial class AgentOrchestratorVendorTests {
    const string SameOrigin = "https://github.com/acme/widgets.git";

    static (string repoPath, Action cleanup) CreateGitRepoWithOrigin(string originUrl) {
        var (repoPath, cleanup) = CreateGitRepo();
        Git(repoPath, "remote", "add", "origin", originUrl);

        return (repoPath, cleanup);
    }

    static (string path, Action cleanup) CreateEmptyDir() {
        var path = Path.Combine(Path.GetTempPath(), "kcap-reviewer-wt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return (path, () => {
            try { Directory.Delete(path, true); } catch {
                /* best-effort */
            }
        });
    }

    [Test]
    public async Task HandleSendInput_re_mirrors_requester_worktree_into_a_running_review_flow_reviewer() {
        var (callerRepo, cleanupCaller) = CreateGitRepoWithOrigin(SameOrigin);
        var (baseRepo, cleanupBase)     = CreateGitRepoWithOrigin(SameOrigin);
        var (reviewerWorktree, cleanupWt) = CreateEmptyDir();

        try {
            // The requester creates a NEW untracked spec while addressing round-1 findings. It is
            // enumerated by `git ls-files -co --exclude-standard`, so the re-mirror must copy it in.
            Directory.CreateDirectory(Path.Combine(callerRepo, "docs"));
            File.WriteAllText(Path.Combine(callerRepo, "docs", "spec.md"), "SPEC BODY");

            var server = new CaptureServerConnection();
            var pty    = new RecordingPtyProcess();

            await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

            var agent = new AgentInstance(
                "agent-flow", null, "", null, baseRepo, "codex",
                new PtyHostedAgentRuntime("codex", pty), new WorktreeInfo(reviewerWorktree, "", baseRepo), new CancellationTokenSource()) {
                SyncSourceRepoRoot = callerRepo,
                Work               = WorkLocation.OwnedWorktree
            };
            orch.RegisterAgentForTest(agent);

            await orch.HandleSendInputForTest(new SendInputCommand("agent-flow", "round 2 — please re-review", null));

            // The freshly-created spec (untracked-not-ignored) is now mirrored into the reviewer's
            // worktree, alongside the committed README — the reviewer sees the requester's live tree.
            await Assert.That(File.Exists(Path.Combine(reviewerWorktree, "docs", "spec.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(reviewerWorktree, "README.md"))).IsTrue();

            // The round is still delivered as a bracketed paste + separate Enter.
            await Assert.That(pty.Writes.Count).IsEqualTo(2);
        } finally {
            cleanupWt();
            cleanupBase();
            cleanupCaller();
        }
    }

    [Test]
    public async Task HandleSendInput_does_not_re_mirror_a_borrowed_cwd_reviewer() {
        // A borrowed cwd is the requester's own checkout — never mutated. Even with a mirror source
        // set, the OwnedWorktree guard must skip the sync (its delete phase would otherwise clobber
        // files the user has locally).
        var (callerRepo, cleanupCaller) = CreateGitRepoWithOrigin(SameOrigin);
        var (borrowedCwd, cleanupCwd)   = CreateEmptyDir();

        try {
            File.WriteAllText(Path.Combine(callerRepo, "only-in-caller.txt"), "x");
            var marker = Path.Combine(borrowedCwd, "local-only.txt");
            File.WriteAllText(marker, "keep me");

            var server = new CaptureServerConnection();
            var pty    = new RecordingPtyProcess();

            await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

            var agent = new AgentInstance(
                "agent-borrowed", null, "", null, callerRepo, "codex",
                new PtyHostedAgentRuntime("codex", pty), WorktreeInfo.Borrowed(borrowedCwd), new CancellationTokenSource()) {
                SyncSourceRepoRoot = callerRepo,
                Work               = WorkLocation.BorrowedCwd
            };
            orch.RegisterAgentForTest(agent);

            await orch.HandleSendInputForTest(new SendInputCommand("agent-borrowed", "input", null));

            // No sync ran: the local file survives and the caller's file was NOT copied in.
            await Assert.That(File.Exists(marker)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(borrowedCwd, "only-in-caller.txt"))).IsFalse();
            await Assert.That(pty.Writes.Count).IsEqualTo(2);
        } finally {
            cleanupCwd();
            cleanupCaller();
        }
    }

    [Test]
    public async Task HandleSendInput_does_not_re_mirror_an_agent_launched_without_a_mirror_source() {
        // A non-review-flow agent has no SyncSourceRepoRoot, so round delivery must not touch the
        // worktree at all.
        var (worktree, cleanupWt) = CreateEmptyDir();

        try {
            var marker = Path.Combine(worktree, "keep.txt");
            File.WriteAllText(marker, "keep");

            var server = new CaptureServerConnection();
            var pty    = new RecordingPtyProcess();

            await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

            var agent = new AgentInstance(
                "agent-plain", null, "", null, "/tmp", "codex",
                new PtyHostedAgentRuntime("codex", pty), new WorktreeInfo(worktree, "", "/tmp"), new CancellationTokenSource()) {
                SyncSourceRepoRoot = null,
                Work               = WorkLocation.OwnedWorktree
            };
            orch.RegisterAgentForTest(agent);

            await orch.HandleSendInputForTest(new SendInputCommand("agent-plain", "input", null));

            await Assert.That(File.Exists(marker)).IsTrue();
            await Assert.That(pty.Writes.Count).IsEqualTo(2);
        } finally {
            cleanupWt();
        }
    }
}
