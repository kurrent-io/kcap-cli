using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The AgentSession frame: a hosted agent's MCP servers learn the session their agent is bound
/// to, exactly as the server files it.</summary>
public class LocalAgentSessionTests {
    static AgentOrchestrator Build() =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    static async Task<string> Ask(AgentOrchestrator orch, string agentId) {
        using var ms = new MemoryStream();
        await orch.HandleLocalAgentSessionAsync(agentId, ms, CancellationToken.None);
        ms.Position = 0;
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.AgentSessionReply);

        return frame.Text;
    }

    /// <summary>A vendor's own id is sent as written: reshaping it would name a session the server has
    /// never stored.</summary>
    [Test]
    public async Task A_live_agent_answers_its_canonical_acp_session() {
        await using var orch = Build();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "live", new FakeAcpRuntime { AcpSessionId = "ses_01JC7QY3VZ8K" });

        await Assert.That(await Ask(orch, "live")).IsEqualTo("ses_01JC7QY3VZ8K");
    }

    [Test]
    public async Task A_resumed_agent_answers_its_ledger_session_over_its_runtimes() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "resumed", new FakeAcpRuntime { AcpSessionId = "ses_runtime" });
        agent.SessionId = "ses_ledger";

        await Assert.That(await Ask(orch, "resumed")).IsEqualTo("ses_ledger");
    }

    [Test]
    public async Task An_unknown_agent_answers_nothing() {
        await using var orch = Build();

        await Assert.That(await Ask(orch, "nobody")).IsEmpty();
    }
}
