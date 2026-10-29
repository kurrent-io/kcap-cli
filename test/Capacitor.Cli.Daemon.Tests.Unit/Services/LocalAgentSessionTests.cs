using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The AgentSession frame: a hosted agent's MCP servers learn the session their agent is bound
/// to, exactly as the daemon reports it, once the server holds it.</summary>
public class LocalAgentSessionTests {
    readonly FakeTimeProvider _time = new();

    AgentOrchestrator Build() =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            timeProvider: _time);

    static void Bind(AgentInstance agent) => AgentOrchestratorHarness.BindTranscript(agent);

    static async Task<string> Read(MemoryStream ms) {
        ms.Position = 0;
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.AgentSessionReply);

        return frame.Text;
    }

    static async Task<string> Ask(AgentOrchestrator orch, string agentId) {
        using var ms = new MemoryStream();
        await orch.HandleLocalAgentSessionAsync(agentId, ms, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        return await Read(ms);
    }

    /// <summary>A vendor's own id is sent as written: reshaping it would name a session the daemon never
    /// reported.</summary>
    [Test]
    public async Task A_bound_agent_answers_its_canonical_acp_session() {
        await using var orch = Build();
        Bind(AgentOrchestratorHarness.SeedAcpAgent(orch, "live", new FakeAcpRuntime { AcpSessionId = "ses_01JC7QY3VZ8K" }));

        await Assert.That(await Ask(orch, "live")).IsEqualTo("ses_01JC7QY3VZ8K");
    }

    [Test]
    public async Task A_resumed_agent_answers_its_ledger_session_over_its_runtimes() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "resumed", new FakeAcpRuntime { AcpSessionId = "ses_runtime" });
        agent.SessionId = "ses_ledger";
        Bind(agent);

        await Assert.That(await Ask(orch, "resumed")).IsEqualTo("ses_ledger");
    }

    [Test]
    public async Task An_unknown_agent_answers_nothing() {
        await using var orch = Build();

        await Assert.That(await Ask(orch, "nobody")).IsEmpty();
    }

    /// <summary>Advances the clock in poll-sized steps until <paramref name="answer"/> completes or
    /// <paramref name="budget"/> of clock time has passed. Each step yields real time, because the handler
    /// re-arms its delay on a thread-pool continuation.</summary>
    async Task Drive(Task answer, TimeSpan budget) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        for (var advanced = TimeSpan.Zero; advanced < budget && !answer.IsCompleted && DateTime.UtcNow < deadline;
             advanced += TimeSpan.FromMilliseconds(100)) {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }
    }

    /// <summary>An ACP agent's first turn runs before its transcript is bound, so a tool call can ask
    /// first; the answer waits for the bind rather than naming a session the server does not hold. Both
    /// phases stay well inside the wait, so the answer can only have come from the bind.</summary>
    [Test]
    public async Task A_launching_agent_is_answered_once_its_session_is_bound() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "launching", new FakeAcpRuntime { AcpSessionId = "ses_01JC7QY3VZ8K" });
        using var ms = new MemoryStream();

        var answer = orch.HandleLocalAgentSessionAsync("launching", ms, CancellationToken.None);
        await Drive(answer, TimeSpan.FromSeconds(2));
        await Assert.That(answer.IsCompleted).IsFalse();

        Bind(agent);
        await Drive(answer, TimeSpan.FromSeconds(2));

        await Assert.That(answer.IsCompleted).IsTrue();
        await Assert.That(await Read(ms)).IsEqualTo("ses_01JC7QY3VZ8K");
    }

    /// <summary>A bind that never comes, as in a server outage, holds the tool call no longer than the
    /// wait, and names no session the caller would then keep; it asks again on its next call.</summary>
    [Test]
    public async Task An_agent_never_bound_is_answered_nothing_when_the_wait_runs_out() {
        await using var orch = Build();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "unbound", new FakeAcpRuntime { AcpSessionId = "ses_01JC7QY3VZ8K" });
        using var ms = new MemoryStream();

        var answer = orch.HandleLocalAgentSessionAsync("unbound", ms, CancellationToken.None);
        await Drive(answer, AgentOrchestrator.AgentSessionBindWait + TimeSpan.FromSeconds(1));

        await Assert.That(answer.IsCompleted).IsTrue();
        await Assert.That(await Read(ms)).IsEmpty();
    }

    /// <summary>A failed or refused bind settles the question at once: the server will not hold that
    /// session, so the answer is nothing, without waiting.</summary>
    [Test]
    public async Task A_failed_bind_is_answered_nothing_at_once() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "failed", new FakeAcpRuntime { AcpSessionId = "ses_01JC7QY3VZ8K" });
        agent.TranscriptBound = false;

        await Assert.That(await Ask(orch, "failed")).IsEmpty();
    }
}
