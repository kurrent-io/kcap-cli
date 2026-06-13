using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
public class AgentOrchestratorVendorTests {
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
            CaptureServerConnection                           server,
            IPtyProcessFactory                                ptyFactory,
            IReadOnlyDictionary<string, IHostedAgentLauncher> launchers,
            string?                                           allowedRepoPath = null
        ) {
        var config = new DaemonConfig {
            Name                = "test",
            ServerUrl           = "http://127.0.0.1:1",
            ClaudePath          = "claude",
            MaxConcurrentAgents = 5,
            WorktreeRoot        = Path.Combine(Path.GetTempPath(), "kcap-orch-wt-" + Guid.NewGuid().ToString("N")[..8])
        };

        if (allowedRepoPath is not null) {
            config.AllowedRepoPaths = [allowedRepoPath];
        }

        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var httpFactory      = new StubHttpClientFactory();
        var permissionBridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance);

        return new AgentOrchestrator(
            config,
            server,
            worktreeManager,
            repoMatcher,
            ptyFactory,
            httpFactory,
            permissionBridge,
            launchers,
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

    [Test]
    public async Task Launch_review_kind_with_vendor_codex_emits_launch_failed() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

        var launchers = new Dictionary<string, IHostedAgentLauncher> {
            ["codex"] = codexSpy
        };

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-r1",
            Prompt: null,
            Model: "gpt-5",
            Effort: null,
            RepoPath: "/tmp/whatever",
            Tools: null,
            AttachmentIds: null,
            Vendor: "codex",
            Kind: LaunchKind.Review,
            Review: new ReviewLaunchInfo("acme", "widgets", 42)
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-r1");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("PR review for Codex");
        await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(0);
        await Assert.That(codexSpy.PrepareCalls).IsEqualTo(0);
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
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

    // ── Test doubles ─────────────────────────────────────────────────────

    sealed class SpyHostedAgentLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
        public string Vendor  { get; } = vendor;
        public string CliPath { get; } = cliPath;

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

        public void Cleanup(AgentInstance agent) {
            CleanupCalls++;
        }
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
        public int     SpawnCalls  { get; private set; }
        public string? LastCommand { get; private set; }

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

        public override Task LaunchFailedAsync(string agentId, string reason) {
            LaunchFailedCalls.Add((agentId, reason));

            return Task.CompletedTask;
        }

        public override Task AgentRegisteredAsync(string agentId, string? prompt, string? model, string? effort, string? repoPath)
            => Task.CompletedTask;

        public override Task AgentStatusChangedAsync(string agentId, string status, string? sessionId)
            => Task.CompletedTask;

        public override Task AgentUnregisteredAsync(string agentId)
            => Task.CompletedTask;

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

        public override Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason)
            => Task.FromResult(new EndAgentSessionResult());

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
