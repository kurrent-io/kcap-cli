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
        var             fake = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: true);
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", fake);

        await orch.HandleSendRawInputForTest(Cmd("nope", Convert.ToBase64String("x"u8)));

        await Assert.That(fake.RawInputs).IsEmpty();
    }

    /// <summary>Teardown keeps the agent registered while it disposes the runtime, so each of these
    /// states is reachable by a keystroke that arrives during it.</summary>
    static (AgentOrchestrator Orch, FakeHostedAgentRuntime Fake, AgentInstance Agent) BuildClosing(string state) {
        var orch  = Build();
        var fake  = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: true);
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", fake);

        switch (state) {
            case "cleanup": AgentOrchestratorHarness.BeginCleanup(agent); break;
            case "reap":    AgentOrchestratorHarness.ClaimReap(agent); break;
            default:        fake.ExitGate.SetResult(); break;
        }

        return (orch, fake, agent);
    }

    [Test]
    [Arguments("cleanup")]
    [Arguments("reap")]
    [Arguments("exited")]
    public async Task Writes_no_raw_input_to_an_agent_that_is_closing(string state) {
        var (orch, fake, agent) = BuildClosing(state);
        await using var _ = orch;

        await orch.HandleSendRawInputForTest(Cmd(agent.Id, Convert.ToBase64String("ls\r"u8)));

        await Assert.That(fake.RawInputs).IsEmpty();
    }

    [Test]
    [Arguments("cleanup")]
    [Arguments("reap")]
    [Arguments("exited")]
    public async Task Sends_no_special_key_to_an_agent_that_is_closing(string state) {
        var (orch, fake, agent) = BuildClosing(state);
        await using var _ = orch;

        await orch.HandleSendSpecialKeyForTest(agent.Id, "escape");

        await Assert.That(fake.SpecialKeys).IsEmpty();
    }

    [Test]
    public async Task Sends_a_special_key_to_a_live_agent() {
        await using var orch  = Build();
        var             fake  = new FakeHostedAgentRuntime("claude", emitsTerminalOutput: true);
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a-1", fake);

        await orch.HandleSendSpecialKeyForTest(agent.Id, "escape");

        await Assert.That(fake.SpecialKeys).IsEquivalentTo(["escape"]);
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
