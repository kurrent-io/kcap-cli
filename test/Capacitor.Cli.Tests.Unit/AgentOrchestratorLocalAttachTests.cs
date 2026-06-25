using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// Local-attach (Phase 1) behaviours on AgentOrchestrator. Partial of
/// AgentOrchestratorVendorTests to reuse its BuildOrchestrator + test doubles.
public partial class AgentOrchestratorVendorTests {
    sealed class NoopRestartStrategy : IRestartStrategy { public RestartOutcome Restart() => RestartOutcome.NoOp; }

    static RestartCoordinator TestCoordinator() =>
        RestartCoordinator.ForTest("test", "test", new NoopRestartStrategy());

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

    [Test]
    public async Task Private_spawn_makes_no_server_calls_and_omits_hosted_agent_env() {
        var dir = Directory.CreateTempSubdirectory("kcap-priv-");

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers);

            // Client read side: one Detach frame so the attach loop returns promptly.
            var readBuf = new MemoryStream();
            await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
            readBuf.Position = 0;
            using var client = new DuplexTestStream(readBuf, new MemoryStream());

            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: true, dir.FullName, ["--model", "opus"], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);

            // Let the fire-and-forget read loop + cleanup finish, then assert no server call landed.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_URL")).IsTrue();            // records as a plain local session
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_AGENT_ID")).IsFalse();      // unregistered in Phase 1 → no agent_host_id tag
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_RENDERED_AGENT")).IsFalse(); // native terminal permissions
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_DAEMON_URL")).IsFalse();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task RegisterAgentAsync_registers_public_agent_and_skips_private() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var pub = new AgentInstance("pub-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = false };
        await orch.RegisterAgentForTestAsync(pub);
        await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentRegisteredAsync));

        server.Calls.Clear();
        var priv = new AgentInstance("priv-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = true };
        await orch.RegisterAgentForTestAsync(priv);
        await Assert.That(server.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Registered_spawn_calls_server_and_sets_hosted_env() {
        var dir = Directory.CreateTempSubdirectory("kcap-reg-");

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers);

            var readBuf = new MemoryStream();
            await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
            readBuf.Position = 0;
            using var client = new DuplexTestStream(readBuf, new MemoryStream());

            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: false, dir.FullName, ["--model", "opus"], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

            await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentRegisteredAsync));
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_URL")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_AGENT_ID")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_RENDERED_AGENT")).IsTrue();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task Reconnect_resends_stored_dims_not_the_hosted_constant() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance("reg-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 73, CurrentRows = 19
        });

        await orch.ReRegisterAgentsForTestAsync();

        await Assert.That(server.LastDims).IsEqualTo((73, 19));
    }

    [Test]
    public async Task Web_resize_updates_stored_dims_then_reconnect_resends_them() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("reg-2", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 80, CurrentRows = 24
        };
        orch.RegisterAgentForTest(agent);

        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-2", 51, 200));
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)51);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)200);

        await orch.ReRegisterAgentsForTestAsync();
        await Assert.That(server.LastDims).IsEqualTo((51, 200));
    }

    [Test]
    public async Task Private_agent_ignores_server_origin_resize_and_stop() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("priv-2", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = true, Status = "Running", CurrentCols = 80, CurrentRows = 24
        };
        orch.RegisterAgentForTest(agent);

        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("priv-2", 51, 200));
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)80); // server-origin resize ignored

        await orch.HandleStopAgentForTest("priv-2");
        await Assert.That(agent.Status).IsEqualTo("Running");       // server-origin stop ignored
    }

    [Test]
    [NotInParallel]
    public async Task Registered_spawn_env_includes_daemon_bridge_url_and_preserves_api_key() {
        var dir     = Directory.CreateTempSubdirectory("kcap-reg-env-");
        var prevKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-key");

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers);
            await orch.PermissionBridgeForTest.StartAsync(default); // binds 127.0.0.1 + sets BaseUrl

            try {
                var readBuf = new MemoryStream();
                await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
                readBuf.Position = 0;
                using var client = new DuplexTestStream(readBuf, new MemoryStream());

                var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: false, dir.FullName, [], 80, 24);
                await orch.HandleLocalSpawnAsync(spawn, client, default);

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

                await Assert.That(pty.LastEnv!["KCAP_DAEMON_URL"]).IsEqualTo(orch.PermissionBridgeForTest.BaseUrl);
                await Assert.That(pty.LastEnv!["ANTHROPIC_API_KEY"]).IsEqualTo("sk-test-key");
            } finally {
                await orch.PermissionBridgeForTest.StopAsync(default);
            }
        } finally {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prevKey);
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task Private_agents_are_excluded_from_live_agent_ids() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance("pub-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = false, Status = "Running" });
        orch.RegisterAgentForTest(new AgentInstance("priv-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = true, Status = "Running" });

        var ids = server.GetLiveAgentIds!();

        await Assert.That(ids).Contains("pub-1");
        await Assert.That(ids).DoesNotContain("priv-1");
    }

    [Test]
    public async Task Local_socket_list_round_trips_registered_agents_over_a_real_socket() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        var sockDir = Directory.CreateTempSubdirectory("kcap-sock-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        LocalControlServer? listener = null;
        AgentOrchestrator?  orch     = null;

        try {
            orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
            orch.RegisterAgentForTest(new AgentInstance(
                "agent-xyz", null, "", null, "/tmp/repo", "claude",
                new StubPtyProcess(), new WorktreeInfo("/tmp/repo", "", "/tmp/repo"), new CancellationTokenSource()
            ) {
                IsPrivate = true, Work = WorkLocation.BorrowedCwd, Status = "Running"
            });

            var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" };
            listener = new LocalControlServer(config, orch, TestCoordinator(), NullLogger<LocalControlServer>.Instance);
            await listener.StartAsync(cts.Token);

            var sockPath = LocalSocketPaths.Socket("test");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, cts.Token);
            await Assert.That(File.Exists(sockPath)).IsTrue();

            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), cts.Token);
            await using var stream = new NetworkStream(sock, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.List), cts.Token);
            var resp = await FrameCodec.ReadAsync(stream, cts.Token);

            await Assert.That(resp!.Type).IsEqualTo(FrameType.AgentList);
            await Assert.That(resp.Text).Contains("agent-xyz");
            await Assert.That(resp.Text).Contains("Running");
        } finally {
            if (orch is not null) await orch.DisposeAsync();
            if (listener is not null) { await listener.StopAsync(CancellationToken.None); listener.Dispose(); }
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { /* best-effort */ }
        }
    }

    // ── Test doubles for the local-spawn lifecycle ──────────────────────

    sealed class EnvCapturingPtyFactory : IPtyProcessFactory {
        public Dictionary<string, string>? LastEnv { get; private set; }

        public IPtyProcess Spawn(string command, string[] args, string cwd, Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40) {
            LastEnv = extraEnv;

            return new StubPtyProcess();
        }
    }

    /// Read and write go to separate underlying streams, so a test can preload client input
    /// while the daemon's frames are captured/discarded independently (a MemoryStream can't
    /// do both at once — it has a single position).
    sealed class DuplexTestStream(Stream readSide, Stream writeSide) : Stream {
        public override int Read(byte[] b, int o, int c) => readSide.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => readSide.ReadAsync(b, ct);
        public override void Write(byte[] b, int o, int c) => writeSide.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => writeSide.WriteAsync(b, ct);
        public override void Flush() => writeSide.Flush();
        public override Task FlushAsync(CancellationToken ct) => writeSide.FlushAsync(ct);
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { readSide.Dispose(); writeSide.Dispose(); } }
    }

    /// Records the name of any per-agent server method invoked. A PrivateLocal agent must
    /// invoke none of them — the test asserts Calls is empty.
    sealed class TripwireServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        public ConcurrentBag<string> Calls { get; } = [];
        public (int Cols, int Rows)? LastDims { get; private set; }

        public override Task SendTerminalDimensionsAsync(string agentId, int cols, int rows) { LastDims = (cols, rows); Calls.Add(nameof(SendTerminalDimensionsAsync)); return Task.CompletedTask; }
        public override Task LaunchFailedAsync(string agentId, string reason) { Calls.Add(nameof(LaunchFailedAsync)); return Task.CompletedTask; }
        public override Task AgentRegisteredAsync(string agentId, string? prompt, string? model, string? effort, string? repoPath) { Calls.Add(nameof(AgentRegisteredAsync)); return Task.CompletedTask; }
        public override Task AgentStatusChangedAsync(string agentId, string status, string? sessionId) { Calls.Add(nameof(AgentStatusChangedAsync)); return Task.CompletedTask; }
        public override Task AgentUnregisteredAsync(string agentId) { Calls.Add(nameof(AgentUnregisteredAsync)); return Task.CompletedTask; }
        public override Task UpdateRepoPathsAsync() { Calls.Add(nameof(UpdateRepoPathsAsync)); return Task.CompletedTask; }
        public override Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) { Calls.Add(nameof(SendTerminalOutputAsync)); return Task.CompletedTask; }
        public override Task AppendAgentRunEventAsync(string agentId, object evt) { Calls.Add(nameof(AppendAgentRunEventAsync)); return Task.CompletedTask; }
        public override Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason) { Calls.Add(nameof(EndAgentSessionAsync)); return Task.FromResult(new EndAgentSessionResult()); }

        public override Task<PermissionDecision> RequestPermissionAsync(
                string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct = default
            ) { Calls.Add(nameof(RequestPermissionAsync)); return Task.FromResult(new PermissionDecision("deny", null, null)); }
    }
}
