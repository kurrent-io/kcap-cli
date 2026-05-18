using System.Diagnostics;
using System.Text.Json;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Daemon;
using Kapacitor.Cli.Daemon.Pty;
using Kapacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kapacitor.Cli.Tests.Unit;

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
        var repoPath = Path.Combine(Path.GetTempPath(), "kapacitor-orch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-q");
        Git(repoPath, "config", "user.email", "test@example.com");
        Git(repoPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "test");
        Git(repoPath, "add", "-A");
        Git(repoPath, "commit", "-q", "-m", "initial");

        return (repoPath, () => {
            try { Directory.Delete(repoPath, true); } catch { /* best-effort */ }
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

    static Daemon.Services.AgentOrchestrator BuildOrchestrator(
            CaptureServerConnection                   server,
            IPtyProcessFactory                        ptyFactory,
            IReadOnlyDictionary<string, IHostedAgentLauncher> launchers,
            string?                                   allowedRepoPath = null
        ) {
        var config = new DaemonConfig {
            Name                = "test",
            ServerUrl           = "http://127.0.0.1:1",
            ClaudePath          = "claude",
            MaxConcurrentAgents = 5,
            WorktreeRoot        = Path.Combine(Path.GetTempPath(), "kapacitor-orch-wt-" + Guid.NewGuid().ToString("N")[..8])
        };

        if (allowedRepoPath is not null) {
            config.AllowedRepoPaths = [allowedRepoPath];
        }

        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new Daemon.Services.RepoMatcher(config, NullLogger<Daemon.Services.RepoMatcher>.Instance);
        var httpFactory      = new StubHttpClientFactory();
        var permissionBridge = new Daemon.Services.LocalPermissionBridge(server, NullLogger<Daemon.Services.LocalPermissionBridge>.Instance);

        return new Daemon.Services.AgentOrchestrator(
            config,
            server,
            worktreeManager,
            repoMatcher,
            ptyFactory,
            httpFactory,
            permissionBridge,
            launchers,
            NullLogger<Daemon.Services.AgentOrchestrator>.Instance
        );
    }

    [Test]
    public async Task Launch_with_unknown_vendor_emits_launch_failed_and_does_not_spawn_pty() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var launchers  = new Dictionary<string, IHostedAgentLauncher>();

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId:       "agent-bogus",
            Prompt:        "hi",
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/does-not-matter",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "bogus"
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
            var codexSpy   = new SpyHostedAgentLauncher("codex",  cliPath: "spy-codex");
            var launchers  = new Dictionary<string, IHostedAgentLauncher> {
                ["claude"] = claudeSpy,
                ["codex"]  = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId:       "agent-c1",
                Prompt:        "do work",
                Model:         "opus",
                Effort:        null,
                RepoPath:      repoPath,
                Tools:         null,
                AttachmentIds: null,
                Vendor:        "claude"
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
            var codexSpy   = new SpyHostedAgentLauncher("codex",  cliPath: "spy-codex");
            var launchers  = new Dictionary<string, IHostedAgentLauncher> {
                ["claude"] = claudeSpy,
                ["codex"]  = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId:       "agent-x1",
                Prompt:        "do work",
                Model:         "gpt-5",
                Effort:        null,
                RepoPath:      repoPath,
                Tools:         null,
                AttachmentIds: null,
                Vendor:        "codex"
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
        var launchers  = new Dictionary<string, IHostedAgentLauncher> {
            ["codex"] = codexSpy
        };

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId:       "agent-r1",
            Prompt:        null,
            Model:         "gpt-5",
            Effort:        null,
            RepoPath:      "/tmp/whatever",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "codex",
            Kind:          LaunchKind.Review,
            Review:        new ReviewLaunchInfo("acme", "widgets", 42)
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
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex") {
                PrepareThrow = new CodexHooksNotInstalledException("Run plugin install --codex")
            };
            var launchers = new Dictionary<string, IHostedAgentLauncher> {
                ["codex"] = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId:       "agent-h1",
                Prompt:        "go",
                Model:         "gpt-5",
                Effort:        null,
                RepoPath:      repoPath,
                Tools:         null,
                AttachmentIds: null,
                Vendor:        "codex"
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

    // ── Test doubles ─────────────────────────────────────────────────────

    sealed class SpyHostedAgentLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
        public string Vendor  { get; } = vendor;
        public string CliPath { get; } = cliPath;

        public int                 PrepareCalls   { get; private set; }
        public int                 BuildArgsCalls { get; private set; }
        public int                 CleanupCalls   { get; private set; }
        public Exception?          PrepareThrow   { get; init; }

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

    sealed class SpyPtyProcessFactory : IPtyProcessFactory {
        public int     SpawnCalls  { get; private set; }
        public string? LastCommand { get; private set; }

        public IPtyProcess Spawn(string command, string[] args, string cwd,
                                 Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40) {
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

        public ValueTask DisposeAsync()                  => default;
        public Task      WaitForExitAsync(TimeSpan? _)   => Task.CompletedTask;
        public Task      TerminateAsync(TimeSpan?  _)    => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default
            ) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string  _) => Task.CompletedTask;
        public Task WriteAsync(byte[]  _) => Task.CompletedTask;
        public void Resize(ushort _, ushort __) { }
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

        public override Task SendTerminalOutputAsync(string agentId, string base64Data)
            => Task.CompletedTask;

        public override Task AppendAgentRunEventAsync(string agentId, object evt)
            => Task.CompletedTask;

        public override Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason)
            => Task.FromResult(new EndAgentSessionResult());

        public override Task<PermissionDecision> RequestPermissionAsync(
                string            sessionId,
                string?           toolName,
                JsonElement?      toolInput,
                JsonElement?      suggestions,
                string            vendor,
                CancellationToken ct = default
            ) => Task.FromResult(new PermissionDecision("deny", null, null));
    }

    /// <summary>Minimal IHttpClientFactory so the orchestrator can be constructed without DI.</summary>
    sealed class StubHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }
}
