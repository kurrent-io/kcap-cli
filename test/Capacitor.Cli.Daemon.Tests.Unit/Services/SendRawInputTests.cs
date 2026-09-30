using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class SendRawInputTests {
    static SendRawInputCommand Cmd(string agentId, string data) => new(agentId, data, Guid.NewGuid());

    static AgentOrchestrator Build() =>
        AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task Delivers_decoded_bytes_to_the_runtime() {
        await using var orch  = Build();
        var             fake  = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: true);
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", fake);

        await orch.HandleSendRawInputForTest(Cmd(agent.Id, Convert.ToBase64String("ls\r"u8)));

        await Assert.That(fake.RawInputs.Count).IsEqualTo(1);
        await Assert.That(fake.RawInputs[0]).IsEquivalentTo("ls\r"u8.ToArray());
    }

    [Test]
    public async Task Ignores_a_private_agent() {
        await using var orch  = Build();
        var             fake  = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: true);
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", fake, isPrivate: true);

        await orch.HandleSendRawInputForTest(Cmd(agent.Id, Convert.ToBase64String("ls\r"u8)));

        await Assert.That(fake.RawInputs).IsEmpty();
    }

    [Test]
    public async Task Ignores_an_unknown_agent() {
        await using var orch = Build();

        await orch.HandleSendRawInputForTest(Cmd("nope", Convert.ToBase64String("x"u8)));
    }

    [Test]
    public async Task Drops_undecodable_base64_without_throwing() {
        await using var orch  = Build();
        var             fake  = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: true);
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", fake);

        await orch.HandleSendRawInputForTest(Cmd(agent.Id, "%%%"));

        await Assert.That(fake.RawInputs).IsEmpty();
    }

    [Test]
    public async Task Swallows_NotSupported_from_a_non_pty_runtime() {
        await using var orch  = Build();
        var             fake  = new FakeHostedAgentRuntime("cursor", emitsTerminalOutput: false) { ThrowOnRawInput = true };
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", fake);

        await orch.HandleSendRawInputForTest(Cmd(agent.Id, Convert.ToBase64String("x"u8)));

        await Assert.That(fake.RawInputs).IsEmpty();
    }

    [Test]
    public async Task A_submit_clears_awaiting_input() {
        var clock = new AgentActivityClock(TimeProvider.System);
        clock.SetAwaitingInput(true);

        await using var orch  = Build();
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", new FakeHostedAgentRuntime("claude", true), activityClock: clock);

        await orch.HandleSendRawInputForTest(Cmd(agent.Id, Convert.ToBase64String("\r"u8)));

        await Assert.That(clock.AwaitingInput).IsFalse();
    }

    [Test]
    public async Task Keystrokes_without_a_submit_leave_awaiting_input_set() {
        var clock = new AgentActivityClock(TimeProvider.System);
        clock.SetAwaitingInput(true);

        await using var orch  = Build();
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", new FakeHostedAgentRuntime("claude", true), activityClock: clock);

        await orch.HandleSendRawInputForTest(Cmd(agent.Id, Convert.ToBase64String("ls"u8)));

        await Assert.That(clock.AwaitingInput).IsTrue();
    }
}
