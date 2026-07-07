using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1207 Phase A (tasks A5 + A6): the borrowed-launch branch in
/// <see cref="AgentOrchestrator.HandleLaunchAgent"/> and — the reason A5 and A6 ship together —
/// the failed-launch cleanup guard.
///
/// THE TOP SAFETY INVARIANT: a borrowed cwd is the user's REAL checkout. It must NEVER be
/// removed / <c>git worktree remove</c>d / branch-deleted on ANY path (normal stop, failed
/// launch, anywhere). These tests lock that in behaviourally.
///
/// Reuses the <c>partial</c> harness in <see cref="AgentOrchestratorVendorTests"/>
/// (<c>BuildOrchestrator</c>, <c>CreateGitRepo</c>, <c>CaptureServerConnection</c>,
/// <c>SpyPtyProcessFactory</c>, <c>FixedPtyProcessFactory</c>, <c>OneChunkThenBlockPtyProcess</c>,
/// <c>SpyHostedAgentLauncher</c>).
/// </summary>
public partial class AgentOrchestratorVendorTests {
    // ── A5: borrowed launch runs in the user's cwd and creates no daemon worktree ─────────

    [Test]
    public async Task Borrowed_launch_creates_no_worktree_and_runs_in_the_cwd() {
        var (cwd, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            // A blocking PTY keeps the agent registered so we can inspect Work/Worktree before cleanup.
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            // Empty allowlist ⇒ BorrowAuthorizer authorizes any local git repo (allow-all-repos).
            await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

            var before = SnapshotTree(cwd);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-borrow-1",
                Prompt: "do work",
                Model: "opus",
                Effort: null,
                RepoPath: cwd,
                Tools: null,
                AttachmentIds: ["would-be-attachment"], // set so we prove the attachment download-into-cwd is skipped
                Vendor: "claude",
                Borrowed: true,
                BorrowCwd: cwd
            );

            await orch.HandleLaunchAgentForTest(cmd);

            var canonicalCwd = BorrowAuthorizer.Canonicalize(cwd);

            // No daemon-owned worktree was created under the user's checkout...
            await Assert.That(Directory.Exists(Path.Combine(cwd, ".capacitor", "worktrees"))).IsFalse();
            // ...no attachments were downloaded into it...
            await Assert.That(Directory.Exists(Path.Combine(cwd, ".attached"))).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(canonicalCwd, ".attached"))).IsFalse();
            // ...and the cwd tree is byte-identical (no worktree add, no launch-time mirror, no attachment).
            await Assert.That(SnapshotTree(cwd)).IsEquivalentTo(before);

            // The agent runs in the user's real (canonicalized) checkout, marked as a borrowed cwd.
            var agent = orch.GetAgentForTest("agent-borrow-1");
            await Assert.That(agent).IsNotNull();
            await Assert.That(agent!.Work).IsEqualTo(WorkLocation.BorrowedCwd);
            await Assert.That(agent.Worktree.Path).IsEqualTo(canonicalCwd);

            // Clean stop (also exercises the normal-stop cleanup guard for a borrowed agent).
            await orch.HandleStopAgentForTest("agent-borrow-1");
            await Assert.That(Directory.Exists(cwd)).IsTrue();
        } finally {
            cleanup();
        }
    }

    // ── A5: launch-time re-authorization fails loudly, leaving the cwd untouched ───────────

    [Test]
    public async Task Borrowed_launch_reauth_failure_fails_loudly() {
        // RepoPath is an allowed git repo (passes the early repo-allowed/exists guards); the borrow
        // cwd is a NON-git directory, which the authorizer rejects under an empty allowlist.
        var (repoPath, cleanupRepo) = CreateGitRepo();
        var borrowCwd = Path.Combine(Path.GetTempPath(), "kcap-borrow-nogit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(borrowCwd);
        File.WriteAllText(Path.Combine(borrowCwd, "user-file.txt"), "precious");

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

            var before = SnapshotTree(borrowCwd);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-borrow-auth",
                Prompt: "do work",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude",
                Borrowed: true,
                BorrowCwd: borrowCwd
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // Fails loudly with the machine-readable prefix Phase B (server) keys off.
            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-borrow-auth");
            await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("borrow_auth_failed");

            // No PTY ever spawned, and the user's directory is byte-identical (nothing created/removed).
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
            await Assert.That(Directory.Exists(borrowCwd)).IsTrue();
            await Assert.That(SnapshotTree(borrowCwd)).IsEquivalentTo(before);
            await Assert.That(File.ReadAllText(Path.Combine(borrowCwd, "user-file.txt"))).IsEqualTo("precious");
        } finally {
            cleanupRepo();
            try { Directory.Delete(borrowCwd, true); } catch { /* best-effort */ }
        }
    }

    // ── A6 (SAFETY): a failed borrowed launch must NOT remove the user's checkout ──────────

    [Test]
    public async Task Failed_borrowed_launch_does_not_remove_the_cwd() {
        // The borrow cwd is a *linked* git worktree — the realistic danger: `git worktree remove`
        // succeeds on a linked worktree and would silently delete the user's checkout. The launch
        // fails AFTER the borrowed worktree is assigned (runtime StartAsync throws), reaching the
        // failed-launch cleanup. Without the A6 guard that cleanup git-worktree-removes the cwd;
        // this test fails there and passes once the removal is gated on OwnedWorktree.
        var (_, linkedCwd, cleanup) = CreateLinkedWorktree();

        try {
            var server          = new CaptureServerConnection();
            var ptyFactory      = new SpyPtyProcessFactory();
            var throwingFactory = new ThrowingHostedAgentRuntimeFactory("boomvendor", "kaboom during start");

            // No launcher for the vendor; the throwing runtime factory is injected directly.
            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [throwingFactory]
            );

            // Capture BEFORE the launch: Canonicalize falls back to the lexical path once the dir is
            // gone, so a post-failure recompute would mask a deletion instead of exposing it.
            var canonicalCwd = BorrowAuthorizer.Canonicalize(linkedCwd);
            var before       = SnapshotTree(linkedCwd);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-borrow-fail",
                Prompt: "do work",
                Model: "opus",
                Effort: null,
                RepoPath: linkedCwd,
                Tools: null,
                AttachmentIds: null,
                Vendor: "boomvendor",
                Borrowed: true,
                BorrowCwd: linkedCwd
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // SAFETY (asserted first, clearest): the user's REAL checkout SURVIVES, byte-identical.
            // Pre-A6 guard the failed-launch cleanup `git worktree remove`d it and this is False —
            // the whole reason A5 and A6 ship in one commit.
            await Assert.That(Directory.Exists(linkedCwd)).IsTrue();
            await Assert.That(SnapshotTree(linkedCwd)).IsEquivalentTo(before);
            // The launch failed, and the runtime had received the borrowed (canonicalized) cwd.
            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            await Assert.That(throwingFactory.LastWorktreePath).IsEqualTo(canonicalCwd);
        } finally {
            cleanup();
        }
    }

    // ── Regression: the OWNED path still creates a worktree and removes it on failure ──────

    [Test]
    public async Task Owned_launch_still_creates_and_on_failure_removes_the_worktree() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server          = new CaptureServerConnection();
            var ptyFactory      = new SpyPtyProcessFactory();
            var throwingFactory = new ThrowingHostedAgentRuntimeFactory("boomvendor", "kaboom during start");

            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath,
                extraRuntimeFactories: [throwingFactory]
            );

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-owned-fail",
                Prompt: "do work",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "boomvendor",
                Borrowed: false
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // The launch failed after a daemon-OWNED worktree was created...
            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            await Assert.That(throwingFactory.LastWorktreePath).IsNotNull();
            // ...under the repo's .capacitor/worktrees...
            await Assert.That(throwingFactory.LastWorktreePath!)
                .StartsWith(Path.Combine(repoPath, ".capacitor", "worktrees"));
            // ...and the failed-launch cleanup removed it (owned behaviour unchanged).
            await Assert.That(Directory.Exists(throwingFactory.LastWorktreePath!)).IsFalse();
        } finally {
            cleanup();
        }
    }

    // ── Helpers / test doubles ─────────────────────────────────────────────────────────────

    /// <summary>Sorted list of every file-system entry (relative path) under <paramref name="root"/>,
    /// so a before/after comparison catches ANY addition or removal in the user's tree.</summary>
    static List<string> SnapshotTree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Creates a main git repo plus a linked worktree checked out from it, returning the
    /// linked worktree path — a realistic "borrow the user's git worktree" cwd.</summary>
    static (string mainRepo, string linkedCwd, Action cleanup) CreateLinkedWorktree() {
        var (mainRepo, cleanupMain) = CreateGitRepo();
        var linkedCwd = Path.Combine(Path.GetTempPath(), "kcap-borrow-link-" + Guid.NewGuid().ToString("N")[..8]);

        Git(mainRepo, "worktree", "add", linkedCwd);

        return (mainRepo, linkedCwd, () => {
            try { if (Directory.Exists(linkedCwd)) Directory.Delete(linkedCwd, true); } catch { /* best-effort */ }
            cleanupMain();
        });
    }

    /// <summary>A runtime factory that records the worktree it was handed then throws a generic
    /// (non-<see cref="CodexHooksNotInstalledException"/>) failure, driving the launch into the
    /// main failed-launch cleanup path AFTER the worktree is assigned.</summary>
    sealed class ThrowingHostedAgentRuntimeFactory(string vendor, string message) : IHostedAgentRuntimeFactory {
        public string  Vendor            { get; }              = vendor;
        public bool    SupportsUnattended                       => true;
        public string? LastWorktreePath  { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            LastWorktreePath = ctx.Worktree.Path;

            throw new InvalidOperationException(message);
        }
    }
}
