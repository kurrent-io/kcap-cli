using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the vendor-routing logic in <see cref="AgentOrchestrator.HandleLaunchAgent"/>
/// added in Task 14. Verifies that:
///   • Unknown vendors short-circuit with LaunchFailed before any worktree work.
///   • Claude/Codex commands route to the matching <see cref="IHostedAgentLauncher"/>.
///   • Review launches for Codex are rejected (v1 limitation).
///   • <see cref="CodexHooksNotInstalledException"/> from Prepare surfaces as a
///     LaunchFailed with the exception's message and no PTY ever spawns.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    static (string repoPath, Action cleanup) CreateGitRepo() {
        var repoPath = Path.Combine(Path.GetTempPath(), "kcap-orch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-q");
        Git(repoPath, "config", "user.email", "test@example.com");
        Git(repoPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "test");
        Git(repoPath, "add", "-A");
        Git(repoPath, "commit", "-q", "-m", "initial");

        return (repoPath, () => {
            try { Directory.Delete(repoPath, true); } catch {
                /* best-effort */
            }
        });
    }

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }

    static AgentOrchestrator BuildOrchestrator(
            ServerConnection                                    server,
            IPtyProcessFactory                                  ptyFactory,
            IReadOnlyDictionary<string, IHostedAgentLauncher>   launchers,
            string?                                             allowedRepoPath        = null,
            IEnumerable<IHostedAgentRuntimeFactory>?            extraRuntimeFactories  = null,
            Action<DaemonConfig>?                               configure              = null
        ) {
        var config = new DaemonConfig {
            Name                = "test",
            ServerUrl           = "http://127.0.0.1:1",
            ClaudePath          = "claude",
            MaxConcurrentAgents = 5,
            WorktreeRoot        = Path.Combine(Path.GetTempPath(), "kcap-orch-wt-" + Guid.NewGuid().ToString("N")[..8]),
            // AI-1313 Phase B (D4): isolate the PID-record store to a temp dir so tests never touch the
            // real ~/.config/kcap/daemons.
            StateDir            = Path.Combine(Path.GetTempPath(), "kcap-orch-state-" + Guid.NewGuid().ToString("N")[..8])
        };

        if (allowedRepoPath is not null) {
            config.AllowedRepoPaths = [allowedRepoPath];
        }

        configure?.Invoke(config); // AI-1313 Phase B: let a test tweak the config (e.g. reviewer TTL bounds)

        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var httpFactory      = new StubHttpClientFactory();
        var permissionBridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance);

        // Mirror DaemonRunner's DI wiring: one PtyHostedAgentRuntimeFactory per registered launcher,
        // all sharing the same (spied) IPtyProcessFactory so SpyPtyProcessFactory's
        // SpawnCalls/LastCommand assertions stay valid through the runtime-selection seam.
        // extraRuntimeFactories lets a test inject a non-PTY factory (e.g. a fake "cursor" ACP
        // factory) without disturbing every other call site of this helper.
        IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> runtimeFactories = launchers.Values
            .Select(l => (IHostedAgentRuntimeFactory) new PtyHostedAgentRuntimeFactory(l, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance))
            .Concat(extraRuntimeFactories ?? [])
            .ToDictionary(f => f.Vendor);

        return new AgentOrchestrator(
            config,
            server,
            worktreeManager,
            repoMatcher,
            ptyFactory,
            httpFactory,
            permissionBridge,
            launchers,
            runtimeFactories,
            new StubHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance
        );
    }

    sealed class StubHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    // AI-864: re-registration is awaited inside RegisterDaemon before readiness is restored.
    // A transient per-agent failure must be retried (not swallowed on first try), so the agent's
    // ownership is restored before the daemon flips ready — narrowing the "ready despite reregister
    // failure" window qodo flagged.
    [Test]
    public async Task ReRegister_retries_a_transient_per_agent_failure_then_succeeds() {
        var server = new CaptureServerConnection { AgentRegisteredFailTimes = 1 };

        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance(
            "agent-rereg", null, "", null, "/tmp", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource()
        ));

        // The orchestrator wires ReRegisterAgentsHook in its ctor; invoking it runs the same
        // path RegisterDaemon awaits on reconnect.
        await server.ReRegisterAgentsHook!();

        // First attempt threw a transient failure; the bounded retry succeeded on the second.
        await Assert.That(server.AgentRegisteredCallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Launch_with_unknown_vendor_emits_launch_failed_and_does_not_spawn_pty() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var launchers  = new Dictionary<string, IHostedAgentLauncher>();

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-bogus",
            Prompt: "hi",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: "bogus"
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-bogus");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("Unknown vendor");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    // Qodo review on #234: a null Vendor (SignalR boundary — non-null annotation not enforced) must
    // emit LaunchFailed, NOT throw ArgumentNullException from the dictionary lookup (which SafeInvoke
    // would swallow, dropping the launch silently).
    [Test]
    public async Task Launch_with_null_vendor_emits_launch_failed_and_does_not_throw() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var launchers  = new Dictionary<string, IHostedAgentLauncher>();

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-null-vendor",
            Prompt: "hi",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: null!
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-null-vendor");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("Unknown vendor");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    // AI-1124: the orchestrator's unattended-launch guard (UnattendedLaunchPolicy.RejectionReason)
    // must actually be wired into HandleLaunchAgent — reject a review-flow launch whose vendor
    // can't run unattended, and do it before any worktree/PTY side effects.
    [Test]
    public async Task Unattended_review_flow_launch_is_rejected_for_vendor_without_unattended_support() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") { SupportsUnattended = false };

        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-unattended",
            Prompt: "review this",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            Kind: LaunchKind.ReviewFlow
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-unattended");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("unattended");

        // Rejected before any worktree/PTY side effects.
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
        await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Launch_with_vendor_claude_calls_claude_launcher() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

            var launchers = new Dictionary<string, IHostedAgentLauncher> {
                ["claude"] = claudeSpy,
                ["codex"]  = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-c1",
                Prompt: "do work",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(0);
            await Assert.That(codexSpy.PrepareCalls).IsEqualTo(0);

            // PTY spawn must have used the claude launcher's CLI path.
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(1);
            await Assert.That(ptyFactory.LastCommand).IsEqualTo("spy-claude");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Launch_with_vendor_codex_calls_codex_launcher() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

            var launchers = new Dictionary<string, IHostedAgentLauncher> {
                ["claude"] = claudeSpy,
                ["codex"]  = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-x1",
                Prompt: "do work",
                Model: "gpt-5",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(1);
            await Assert.That(codexSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);

            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(1);
            await Assert.That(ptyFactory.LastCommand).IsEqualTo("spy-codex");
        } finally {
            cleanup();
        }
    }

    // AI-684 Task 10: a "cursor" launch must route to its registered IHostedAgentRuntimeFactory
    // (the ACP seam) rather than falling through to a PTY launcher/factory. This is a pure unit
    // test — SpyHostedAgentRuntimeFactory never spawns a real cursor-agent process.
    [Test]
    public async Task Launch_with_vendor_cursor_routes_to_the_acp_runtime_factory_not_pty() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server      = new CaptureServerConnection();
            var ptyFactory  = new SpyPtyProcessFactory();
            var claudeSpy   = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var cursorSpy   = new SpyHostedAgentRuntimeFactory("cursor");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                launchers,
                allowedRepoPath: repoPath,
                extraRuntimeFactories: [cursorSpy]
            );

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-cursor1",
                Prompt: "do work",
                Model: "auto",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(cursorSpy.StartCalls).IsEqualTo(1);
            await Assert.That(cursorSpy.LastAgentId).IsEqualTo("agent-cursor1");

            // Must NOT have gone through the PTY path at all.
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }

    // AI-684 Fix B/E (PR #244 review, BLOCKER): a runtime whose ReadOutputAsync never yields a byte
    // (ACP/cursor) must NOT wait for the orchestrator's on-first-chunk Starting→Running flip — that
    // flip lives in ReadAgentOutputAsync and only fires on an output CHUNK, which never arrives for
    // such a runtime. Before the fix this left the agent stuck in "Starting" (eventually auto-
    // stopped by the heartbeat's stuck-Starting timeout) and, worse, the runtime's ReadOutputAsync
    // completing immediately made FinalizeAgentRunAsync run right after launch and misclassify the
    // still-live agent as a startup failure. This test proves: (1) the agent flips to "Running"
    // synchronously within HandleLaunchAgent, before any output; (2) it is NOT finalized-as-Failed
    // while still live (the fake's ReadOutputAsync stays open exactly like the real ACP runtime).
    [Test]
    public async Task Launch_of_a_no_terminal_output_runtime_flips_to_Running_immediately_and_is_not_finalized_as_failed() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var cursorSpy  = new SpyHostedAgentRuntimeFactory("cursor") { EmitsTerminalOutput = false };

            var launchers = new Dictionary<string, IHostedAgentLauncher>();

            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                launchers,
                allowedRepoPath: repoPath,
                extraRuntimeFactories: [cursorSpy]
            );

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-acp-live",
                Prompt: "do work",
                Model: "auto",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // Flipped to Running synchronously within the launch call itself — no output chunk
            // was ever produced (the fake's ReadOutputAsync blocks on ExitGate, exactly like the
            // real ACP runtime blocks on process-exit/ct).
            await Assert.That(server.StatusChangedCalls).Contains(("agent-acp-live", "Running"));

            // Give the (fire-and-forget) read loop / finalize path a moment to run if it were
            // (incorrectly) going to — before the fix, ReadOutputAsync completing immediately
            // would have driven FinalizeAgentRunAsync to run right here and mark the agent Failed.
            await Task.Delay(200);

            await Assert.That(server.StatusChangedCalls).DoesNotContain(("agent-acp-live", "Failed"));
            await Assert.That(server.StatusChangedCalls).DoesNotContain(("agent-acp-live", "Completed"));
            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(0);

            // Cleanly finish the still-live agent so the harness doesn't leak an ExitGate-blocked
            // read loop past the test.
            await orch.HandleStopAgentForTest("agent-acp-live");
        } finally {
            cleanup();
        }
    }

    // Companion test: the existing PTY on-first-chunk Starting→Running flip must remain exactly
    // unchanged for a runtime with EmitsTerminalOutput=true — the new immediate-flip branch in
    // HandleLaunchAgent must not fire for it.
    [Test]
    public async Task Launch_of_a_terminal_output_runtime_does_not_flip_to_Running_before_the_first_output_chunk() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var cursorSpy  = new SpyHostedAgentRuntimeFactory("cursor") { EmitsTerminalOutput = true };

            var launchers = new Dictionary<string, IHostedAgentLauncher>();

            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                launchers,
                allowedRepoPath: repoPath,
                extraRuntimeFactories: [cursorSpy]
            );

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-terminal-live",
                Prompt: "do work",
                Model: "auto",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // No output chunk was ever produced (ExitGate is still open) — must NOT have flipped
            // to Running yet, preserving the existing PTY on-first-chunk contract exactly.
            await Task.Delay(100);
            await Assert.That(server.StatusChangedCalls).DoesNotContain(("agent-terminal-live", "Running"));

            await orch.HandleStopAgentForTest("agent-terminal-live");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Launch_review_kind_with_vendor_codex_is_accepted_and_reaches_review_validation() {
        // A git repo with NO origin remote: a Codex review now passes the vendor
        // gate (which used to reject it) and fails later at origin validation —
        // the SAME point a Claude review would. Proves the gate is lifted.
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["codex"] = codexSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-r1",
                Prompt: null,
                Model: "gpt-5",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex",
                Kind: LaunchKind.Review,
                Review: new ReviewLaunchInfo("acme", "widgets", 42)
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            // The old Codex-specific rejection is gone...
            await Assert.That(server.LaunchFailedCalls[0].Reason).DoesNotContain("PR review for Codex");
            // ...and it failed at the shared origin check instead.
            await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("origin");
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Codex_hooks_not_installed_exception_during_prepare_yields_actionable_launch_failed() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();

            var codexSpy = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex") {
                PrepareThrow = new CodexHooksNotInstalledException("Run plugin install --codex")
            };

            var launchers = new Dictionary<string, IHostedAgentLauncher> {
                ["codex"] = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-h1",
                Prompt: "go",
                Model: "gpt-5",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-h1");
            await Assert.That(server.LaunchFailedCalls[0].Reason).IsEqualTo("Run plugin install --codex");
            await Assert.That(codexSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(0);
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Stopping_an_agent_releases_a_read_loop_blocked_on_a_full_terminal_queue() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            // The send blocks (full/down queue) until its ct cancels; the PTY keeps the
            // stream open so the read loop is genuinely parked inside the blocked send.
            var sendEntered   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendUnblocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server     = new CaptureServerConnection { SendEntered = sendEntered, SendUnblocked = sendUnblocked };
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-bp",
                Prompt: "go",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            ));

            // Wait until the read loop has produced a chunk and is parked in the blocked send.
            await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Stopping the agent cancels ReadCts. The blocked enqueue MUST be released by
            // that cancellation; otherwise the read loop (and its finally-block cleanup)
            // stalls until daemon shutdown (AI-846). Before the fix the enqueue awaited the
            // daemon-lifetime token instead, so this never completes.
            await orch.HandleStopAgentForTest("agent-bp");

            await sendUnblocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Stopping_an_agent_terminates_promptly_even_when_end_session_is_blocked() {
        // Option B: EndAgentSession is the post-exit backstop and retries across SignalR
        // reconnects, so it can block while the connection recovers. A user-initiated stop
        // must NOT wait on it — HandleStopAgent terminates the process, and the read-loop's
        // finalize backstop ends the session afterwards. With EndAgentSession blocked,
        // termination must still happen promptly (before the fix HandleStopAgent awaited
        // its own EndAgentSession call and never reached TerminateAsync).
        var (repoPath, cleanup) = CreateGitRepo();
        using var endSessionBlock = new CancellationTokenSource();

        try {
            var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server     = new CaptureServerConnection { EndSessionBlockUntil = endSessionBlock };
            var ptyFactory = new FixedPtyProcessFactory(new TerminateSignalingPtyProcess(terminated));
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-stop",
                Prompt: "go",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            ));

            // Fire-and-forget: before the fix, HandleStopAgent awaits the blocked
            // EndAgentSession and never reaches termination, so we must not await it here.
            _ = orch.HandleStopAgentForTest("agent-stop");

            // The process must be terminated even though EndAgentSession is still blocked.
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            endSessionBlock.Cancel(); // release the finalize backstop's blocked end-session
            cleanup();
        }
    }

    [Test]
    public async Task Cleanup_runs_even_when_end_session_never_recovers() {
        // Qodo: EndAgentSession now retries across reconnects, so it can block for a whole
        // outage. FinalizeAgentRunAsync must not stall local cleanup on it — it waits only
        // up to EndAgentSessionBudget, then proceeds to CleanupAgentAsync (which unregisters
        // the agent) while the retry continues in the background. Here end-session never
        // recovers, yet cleanup must still run.
        var (repoPath, cleanup) = CreateGitRepo();
        using var neverRecovers = new CancellationTokenSource();

        try {
            var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server = new CaptureServerConnection {
                EndSessionBlockUntil = neverRecovers,                  // end-session blocks for the whole test
                OnAgentUnregistered  = () => unregistered.TrySetResult() // fires when cleanup completes
            };
            var ptyFactory = new FixedPtyProcessFactory(new ImmediateExitPtyProcess());
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);
            orch.EndAgentSessionBudget = TimeSpan.FromMilliseconds(250); // don't wait the real 30s in a test

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-x",
                Prompt: "go",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            ));

            // The PTY exits immediately → the read loop ends → FinalizeAgentRunAsync runs.
            // End-session blocks (never recovers), but cleanup must still run after the budget.
            await unregistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            neverRecovers.Cancel(); // release the background end-session task
            cleanup();
        }
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    /// <summary>PTY that has already exited and produces no output, so the read loop ends
    /// immediately and FinalizeAgentRunAsync runs right after launch.</summary>
    sealed class ImmediateExitPtyProcess : IPtyProcess {
        public int  Pid       => 4244;
        public bool HasExited => true;
        public int? ExitCode  => 0;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    /// <summary>
    /// PTY that reports already-exited (so HandleStopAgent's graceful window is instant)
    /// and signals when TerminateAsync runs. ReadOutputAsync blocks until ReadCts cancels,
    /// keeping the read loop alive until the stop.
    /// </summary>
    sealed class TerminateSignalingPtyProcess(TaskCompletionSource terminated) : IPtyProcess {
        public int  Pid       => 4243;
        public bool HasExited => true;
        public int? ExitCode  => 0;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? _) {
            terminated.TrySetResult();

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) {
                /* released on stop */
            }

            yield break;
        }

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    sealed class SpyHostedAgentLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
        public string Vendor             { get; } = vendor;
        public string CliPath            { get; } = cliPath;
        public bool   SupportsUnattended { get; init; } = true;

        public int        PrepareCalls   { get; private set; }
        public int        BuildArgsCalls { get; private set; }
        public int        CleanupCalls   { get; private set; }
        public Exception? PrepareThrow   { get; init; }

        public bool IsAvailable() => true;

        public void Prepare(LauncherContext ctx) {
            PrepareCalls++;

            if (PrepareThrow is not null) throw PrepareThrow;
        }

        public LaunchArgs BuildArgs(LauncherContext ctx) {
            BuildArgsCalls++;

            return new LaunchArgs(Args: [], McpConfigPath: null);
        }

        public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) {
            BuildArgsCalls++;

            return new LaunchArgs(Args: [.. userArgs], McpConfigPath: null);
        }

        public void Cleanup(AgentInstance agent) {
            CleanupCalls++;
        }
    }

    /// <summary>
    /// Test double for the AI-684 Task 10 runtime-selection seam: records that <see cref="StartAsync"/>
    /// was called (and with what agent id) and returns a no-op <see cref="FakeHostedAgentRuntime"/>,
    /// without ever spawning a real process — proves the orchestrator routes a launch to the correct
    /// <see cref="IHostedAgentRuntimeFactory"/> by vendor.
    /// </summary>
    sealed class SpyHostedAgentRuntimeFactory(string vendor) : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended { get; init; }

        /// <summary>Threaded onto the <see cref="FakeHostedAgentRuntime"/> this factory returns —
        /// defaults to <c>false</c> (ACP-shaped) matching this factory's original "cursor" use, but
        /// settable so a test can exercise the PTY-shaped (<c>true</c>) lifecycle branch too.</summary>
        public bool EmitsTerminalOutput { get; init; }

        public int     StartCalls  { get; private set; }
        public string? LastAgentId { get; private set; }

        public FakeHostedAgentRuntime? LastRuntime { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            StartCalls++;
            LastAgentId = ctx.AgentId;

            var runtime = new FakeHostedAgentRuntime(vendor, EmitsTerminalOutput);
            LastRuntime = runtime;

            return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null));
        }
    }

    /// <summary>No-op <see cref="IHostedAgentRuntime"/> returned by <see cref="SpyHostedAgentRuntimeFactory"/>
    /// so the orchestrator's post-launch RegisterAgentAsync/ReadAgentOutputAsync run against a
    /// harmless stand-in instead of a real PTY or ACP connection. <see cref="ReadOutputAsync"/>
    /// blocks until <see cref="ExitGate"/> is released (or <c>ct</c> cancels) rather than completing
    /// immediately, mirroring the real ACP runtime's "stay open until the process exits" contract —
    /// this lets AI-684 Fix B/E tests observe orchestrator state (e.g. Status flips to "Running")
    /// WHILE the agent is still live, before ever driving it to completion.</summary>
    sealed class FakeHostedAgentRuntime(string vendor, bool emitsTerminalOutput) : IHostedAgentRuntime {
        public string Vendor              => vendor;
        public int    Pid                 => 0;
        public bool   HasExited           => ExitGate.Task.IsCompleted;
        public int?   ExitCode            => 0;
        public bool   EmitsTerminalOutput => emitsTerminalOutput;

        /// <summary>Released by a test to simulate the hosted process exiting.</summary>
        public TaskCompletionSource ExitGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            var ctTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = ct.Register(() => ctTcs.TrySetResult());
            await Task.WhenAny(ExitGate.Task, ctTcs.Task).ConfigureAwait(false);

            yield break;
        }

        public Task SendUserInputAsync(string    text) => Task.CompletedTask;
        public Task SendSpecialKeyAsync(string    key) => Task.CompletedTask;
        public Task SendRawInputAsync(byte[]      data) => Task.CompletedTask;
        public void Resize(ushort                 cols, ushort rows) { }
        public Task RequestGracefulStopAsync() => Task.CompletedTask;
        public Task WaitForExitAsync(TimeSpan?    timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            ExitGate.TrySetResult();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>Returns a caller-supplied PTY process so a test can control its output behaviour.</summary>
    sealed class FixedPtyProcessFactory(IPtyProcess process) : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string                      command,
                string[]                    args,
                string                      cwd,
                Dictionary<string, string>? extraEnv = null,
                ushort                      cols     = 120,
                ushort                      rows     = 40
            ) => process;
    }

    /// <summary>
    /// Emits one chunk (so the read loop calls SendTerminalOutputAsync) then keeps the
    /// stream open by awaiting the read token — the loop parks in the blocked send until
    /// the agent is stopped. HasExited is true so HandleStopAgent's graceful path is quick.
    /// </summary>
    sealed class OneChunkThenBlockPtyProcess : IPtyProcess {
        public int  Pid       => 4242;
        public bool HasExited => true;
        public int? ExitCode  => 0;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield return "x"u8.ToArray();

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);

            yield break;
        }

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    sealed class SpyPtyProcessFactory : IPtyProcessFactory {
        public int                         SpawnCalls  { get; private set; }
        public string?                     LastCommand { get; private set; }
        public Dictionary<string, string>? LastEnv     { get; private set; }

        public IPtyProcess Spawn(
                string                      command,
                string[]                    args,
                string                      cwd,
                Dictionary<string, string>? extraEnv = null,
                ushort                      cols     = 120,
                ushort                      rows     = 40
            ) {
            SpawnCalls++;
            LastCommand = command;
            LastEnv     = extraEnv;

            return new StubPtyProcess();
        }
    }

    /// <summary>
    /// Stand-in PTY process used after a successful Spawn so the orchestrator's
    /// read-output loop completes immediately and the test doesn't hang waiting
    /// on a real process. ReadOutputAsync yields nothing; everything else is no-op.
    /// </summary>
    sealed class StubPtyProcess : IPtyProcess {
        public int  Pid       => 0;
        public bool HasExited => true;
        public int? ExitCode  => 0;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    /// <summary>
    /// Captures LaunchFailedAsync calls and no-ops the other server-side methods
    /// so the orchestrator's launch flow doesn't touch a real SignalR connection.
    /// </summary>
    sealed class CaptureServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        public List<(string AgentId, string Reason)> LaunchFailedCalls { get; } = [];

        /// <summary>When set, EndAgentSessionAsync blocks until this token is cancelled,
        /// simulating a session-end call stuck waiting for a SignalR reconnect.</summary>
        public CancellationTokenSource? EndSessionBlockUntil { get; init; }

        /// <summary>Reasons passed to EndAgentSessionAsync, in call order.</summary>
        public List<string> EndSessionReasons { get; } = [];

        public override Task LaunchFailedAsync(string agentId, string reason) {
            LaunchFailedCalls.Add((agentId, reason));

            return Task.CompletedTask;
        }

        /// <summary>Number of times to fail AgentRegisteredAsync before succeeding (AI-864:
        /// drives the bounded per-agent re-registration retry test).</summary>
        public int AgentRegisteredFailTimes { get; init; }
        public int AgentRegisteredCallCount { get; private set; }

        public override Task AgentRegisteredAsync(string agentId, string? prompt, string? model, string? effort, string? repoPath) {
            AgentRegisteredCallCount++;
            lock (AcpCallOrder) AcpCallOrder.Add($"register:{agentId}");

            return AgentRegisteredCallCount <= AgentRegisteredFailTimes
                ? Task.FromException(new InvalidOperationException("transient re-register failure"))
                : Task.CompletedTask;
        }

        // ── AI-688 Option B task 4: ACP bind/forward capture ────────────────────────────────────
        // Overrides the RAW hub-invoke seams (mirroring AcpServerConnectionTests' TestServerConnection)
        // rather than the higher-level gated AcpSessionStartedAsync/SendAcpEventsAsync, so every call
        // still goes through the REAL ConnectionRetry/IsReady gating the production wiring relies on.
        // IsReady is overridden to true (no real hub connection exists in these tests) so that gating
        // resolves immediately instead of hanging forever waiting for a connection that never connects.

        internal override bool IsReady => true;

        /// <summary>Every register/bind/events call, in the exact order the orchestrator issued them —
        /// the single source of truth for the bind-ordering and teardown-ordering assertions.</summary>
        public List<string> AcpCallOrder { get; } = [];

        public List<(string AgentId, string Vendor, string AcpSessionId, string? Cwd, string? Model)> AcpSessionStartedCalls { get; } = [];
        public List<(string AgentId, string AcpSessionId, AcpEventEnvelope[] Envelopes)>              AcpEventsCalls         { get; } = [];

        /// <summary>Fires (unbounded, never blocks the caller) once per AcpSessionEvents call, carrying
        /// the 1-based call count — lets a test await "the Nth events call happened" deterministically
        /// instead of guessing with Task.Delay.</summary>
        public Channel<int> AcpEventsCallSignal { get; } = Channel.CreateUnbounded<int>();

        /// <summary>Overrides the ack a given batch receives; defaults to "fully accepted".</summary>
        public Func<AcpEventEnvelope[], AcpBatchAck>? AcpEventsAckOverride { get; init; }

        /// <summary>Set to make the raw AcpSessionEvents invoke hang until this token is cancelled —
        /// simulates a server call that never returns, for the bounded-final-drain test (mirrors
        /// EndSessionBlockUntil's pattern).</summary>
        public CancellationTokenSource? AcpEventsBlockUntil { get; init; }

        /// <summary>One-shot gate: when set, the NEXT raw AcpSessionEvents invoke awaits this task
        /// before returning (then the field is cleared) — lets a test deterministically control
        /// exactly when one specific events call completes, without racing unrelated background
        /// work (e.g. CleanupAgentAsync's own worktree removal) that would otherwise let a call
        /// "eventually" go through regardless of the ordering under test.</summary>
        public TaskCompletionSource? PendingAcpEventsGate { get; set; }

        /// <summary>One-shot gate: when set, the NEXT raw AcpSessionStarted invoke awaits this task
        /// before returning (then the field is cleared) — models a bind call still in flight across
        /// a reconnect outage (AI-688 reliability fix's stale-binding-race test), independent of
        /// <paramref name="ct"/> so the test controls exactly when the "late bind" resolves.</summary>
        public TaskCompletionSource? PendingAcpBindGate { get; set; }

        internal override async Task InvokeAcpSessionStartedRawAsync(
                string agentId, string vendor, string acpSessionId, string? cwd, string? model,
                IReadOnlyDictionary<string, string>? metadata, CancellationToken ct
            ) {
            lock (AcpCallOrder) {
                AcpCallOrder.Add($"bind:{agentId}");
                AcpSessionStartedCalls.Add((agentId, vendor, acpSessionId, cwd, model));
            }

            if (PendingAcpBindGate is { } gate) {
                PendingAcpBindGate = null;
                await gate.Task;
            }
        }

        internal override async Task<AcpBatchAck> InvokeAcpSessionEventsRawAsync(
                string agentId, string acpSessionId, AcpEventEnvelope[] envelopes, CancellationToken ct
            ) {
            int callCount;

            lock (AcpCallOrder) {
                AcpCallOrder.Add($"events:{agentId}:{string.Join(',', envelopes.Select(e => e.Seq))}");
                AcpEventsCalls.Add((agentId, acpSessionId, envelopes));
                callCount = AcpEventsCalls.Count;
            }

            if (AcpEventsBlockUntil is { } blockCts) {
                // Linked with ct (unlike a bare blockCts.Token wait) so a per-agent CTS cancellation
                // propagates through exactly like a real _hub.InvokeAsync(..., cancellationToken: ct)
                // call would (AI-688 reliability fix: forwarder-cancel-on-drain-timeout relies on
                // this). A ct-driven cancellation PROPAGATES (mirrors the real hub); blockCts alone
                // is purely test cleanup (releases an otherwise-abandoned background call) and falls
                // through to a normal successful return.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(blockCts.Token, ct);

                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (OperationCanceledException) {
                    /* released by the test (blockCts) */
                }
            }

            // Not interlocked: the forwarder always has exactly one send in flight at a time (its
            // single-in-flight-batch design), so this test double never sees concurrent callers.
            if (PendingAcpEventsGate is { } gate) {
                PendingAcpEventsGate = null;
                await gate.Task;
            }

            AcpEventsCallSignal.Writer.TryWrite(callCount);

            return AcpEventsAckOverride?.Invoke(envelopes) ?? new AcpBatchAck(envelopes[^1].Seq, envelopes[^1].Seq);
        }

        /// <summary>(AgentId, Status) pairs passed to every AgentStatusChangedAsync call, in
        /// call order — lets a test assert on the exact lifecycle transitions the orchestrator
        /// drove (e.g. AI-684 Fix B/E's immediate Running flip for a no-terminal runtime).</summary>
        public List<(string AgentId, string Status)> StatusChangedCalls { get; } = [];

        public override Task AgentStatusChangedAsync(string agentId, string status, string? sessionId) {
            lock (StatusChangedCalls) StatusChangedCalls.Add((agentId, status));

            return Task.CompletedTask;
        }

        /// <summary>Invoked when AgentUnregisteredAsync runs — the last step of
        /// CleanupAgentAsync, so a useful signal that local cleanup completed.</summary>
        public Action? OnAgentUnregistered { get; init; }

        /// <summary>Every agent id passed to AgentUnregisteredAsync, in call order. AI-1313 Phase B
        /// (D1): a single-flight teardown must unregister an agent exactly once even under a racing
        /// launch-catch + read-loop cleanup.</summary>
        public List<string> AgentUnregisteredCalls { get; } = [];

        public override Task AgentUnregisteredAsync(string agentId) {
            lock (AgentUnregisteredCalls) AgentUnregisteredCalls.Add(agentId);
            OnAgentUnregistered?.Invoke();

            return Task.CompletedTask;
        }

        public override Task UpdateRepoPathsAsync()
            => Task.CompletedTask;

        /// <summary>Set both to make the send block (simulating a full/down terminal
        /// queue) until its <c>ct</c> is cancelled — used by the AI-846 back-pressure
        /// test. Left null for every other test, where the send is a no-op.</summary>
        public TaskCompletionSource? SendEntered   { get; init; }
        public TaskCompletionSource? SendUnblocked { get; init; }

        public override async Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) {
            if (SendEntered is null) return;

            SendEntered.TrySetResult();

            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            } catch (OperationCanceledException) {
                /* released by the read loop's stop-linked token */
            } finally {
                SendUnblocked?.TrySetResult();
            }
        }

        public override Task AppendAgentRunEventAsync(string agentId, object evt)
            => Task.CompletedTask;

        public override async Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason) {
            EndSessionReasons.Add(reason);
            lock (AcpCallOrder) AcpCallOrder.Add($"endSession:{agentId}");

            if (EndSessionBlockUntil is { } cts) {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); } catch (OperationCanceledException) {
                    /* released by the test */
                }
            }

            return new EndAgentSessionResult();
        }

        public override Task<PermissionDecision> RequestPermissionAsync(
                string            sessionId,
                string?           toolName,
                JsonElement?      toolInput,
                JsonElement?      suggestions,
                CancellationToken ct = default
            ) => Task.FromResult(new PermissionDecision("deny", null, null));
    }

    /// <summary>Minimal IHttpClientFactory so the orchestrator can be constructed without DI.</summary>
    sealed class StubHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }
}
