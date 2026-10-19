using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class FlowRoundGatesTests {
    static JsonObject Args(string flowRunId, string? participant = null) {
        var args = new JsonObject { ["flow_run_id"] = flowRunId };
        if (participant is not null) args["participant"] = participant;
        return args;
    }

    [Test]
    public async Task A_second_round_for_the_same_participant_waits_for_the_first() {
        var gates = new FlowRoundGates();
        var first = await gates.EnterAsync("submit_review_round", Args("f1"));

        var second = gates.EnterAsync("send_to_participant", Args("f1", "reviewer"));
        await Task.Delay(50);
        await Assert.That(second.IsCompleted).IsFalse();

        first!.Dispose();
        using var entered = await second;
        await Assert.That(entered).IsNotNull();
    }

    [Test]
    [Arguments("send_to_participant", "f1", "author")]
    [Arguments("submit_review_round", "f2", null)]
    [Arguments("get_flow_status", "f1", null)]
    public async Task Another_participant_flow_or_tool_does_not_wait(string tool, string flowRunId, string? participant) {
        var gates = new FlowRoundGates();
        using var held = await gates.EnterAsync("submit_review_round", Args("f1"));

        var other = gates.EnterAsync(tool, Args(flowRunId, participant));

        await Assert.That(other.IsCompleted).IsTrue();
        (await other)?.Dispose();
    }
}
