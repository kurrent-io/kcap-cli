using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A write-contained runtime that accepts follow-ups is the one case a fetch into the worktree
/// could be steered; only default-kind Codex is that case today.
public class AttachmentPlacementTests {
    [TempHome] public required TempHome Home { get; init; }

    // Placement never reaches the wrapped PTY factory, so this double only needs to exist.
    sealed class UnusedPtyDelegate : IHostedAgentRuntimeFactory {
        public string Vendor  => "codex";
        public string CliPath => "unused-by-this-double";
        public bool   IsAvailable() => true;
        public bool   SupportsUnattended => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    sealed class FakeRuntimeFactory(string vendor) : IHostedAgentRuntimeFactory {
        public string Vendor  => vendor;
        public string CliPath => "unused-by-this-double";
        public bool   IsAvailable() => true;
        public bool   SupportsUnattended => false;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    CodexHostedAgentRuntimeFactory BuildCodexFactory() {
        var launcher = new CodexLauncher(
            new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" },
            TestHarnesses.Under(Home), NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServers = static () => [],
        };
        return new CodexHostedAgentRuntimeFactory(
            launcher, new UnusedPtyDelegate(), new DaemonConfig(), NullLoggerFactory.Instance);
    }

    [Test]
    public async Task Codex_uses_the_daemon_store_for_default_kind_only() {
        var codex = BuildCodexFactory();
        await Assert.That(codex.AttachmentPlacementFor(LaunchKind.Default)).IsEqualTo(AttachmentPlacement.DaemonStore);
        await Assert.That(codex.AttachmentPlacementFor(LaunchKind.Review)).IsEqualTo(AttachmentPlacement.Worktree);
        await Assert.That(codex.AttachmentPlacementFor(LaunchKind.ReviewFlow)).IsEqualTo(AttachmentPlacement.Worktree);
    }

    [Test]
    public async Task Every_other_factory_keeps_the_worktree_default() {
        IHostedAgentRuntimeFactory acp = new FakeRuntimeFactory("cursor");
        await Assert.That(acp.AttachmentPlacementFor(LaunchKind.Default)).IsEqualTo(AttachmentPlacement.Worktree);
    }

    [Test]
    public async Task Local_spawn_records_the_factory_answer_on_the_agent() {
        var server    = new CaptureServerConnection();
        var pty       = new FixedPtyProcessFactory(new ParkedPtyProcess());
        var launchers = new Dictionary<string, IHostedAgentLauncher> {
            ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude"),
            ["codex"]  = new SpyHostedAgentLauncher("codex", "spy-codex"),
        };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, pty, launchers, extraRuntimeFactories: [BuildCodexFactory()]);

        using var claudeTmp = new TempDir();
        using var codexTmp  = new TempDir();

        var claudeAgent = orch.GetAgentForTest(await SpawnAndGetAgentId(orch, "claude", claudeTmp.Path));
        var codexAgent  = orch.GetAgentForTest(await SpawnAndGetAgentId(orch, "codex",  codexTmp.Path));

        await Assert.That(claudeAgent).IsNotNull();
        await Assert.That(codexAgent).IsNotNull();
        await Assert.That(claudeAgent!.Placement).IsEqualTo(AttachmentPlacement.Worktree);
        await Assert.That(codexAgent!.Placement).IsEqualTo(AttachmentPlacement.DaemonStore);
    }

    /// Spawns a vendor over the local socket handler and returns the agent id off the Attached
    /// frame. The runtime never exits, so the agent is still registered when the assertion runs
    /// instead of being torn down by the fire-and-forget read loop's cleanup.
    static async Task<string> SpawnAndGetAgentId(AgentOrchestrator orch, string vendor, string cwd) {
        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());

        var spawn = FrameCodec.Spawn(vendor, WorkLocation.BorrowedCwd, isPrivate: true, cwd, [], 80, 24);
        await orch.HandleLocalSpawnAsync(spawn, client, default);

        client.WrittenStream.Position = 0;
        var frames = new List<LocalFrame>();
        while (await FrameCodec.ReadAsync(client.WrittenStream, default) is { } f) frames.Add(f);

        var (agentId, _) = FrameCodec.Attached(frames.Single(f => f.Type == FrameType.Attached));
        return agentId;
    }

    /// Never exits and never yields output, so the read loop parks instead of reaching cleanup.
    /// Unblocks on the orchestrator's own teardown, which cancels every live agent's ReadCts.
    sealed class ParkedPtyProcess : IPtyProcess {
        public int  Pid       => 1;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string input) => Task.CompletedTask;
        public Task WriteAsync(byte[] data) => Task.CompletedTask;
        public void Resize(ushort _, ushort __) { }
        public void SendInterrupt() { }
    }
}
