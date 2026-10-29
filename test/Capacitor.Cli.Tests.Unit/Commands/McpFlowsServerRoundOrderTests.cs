using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerRoundOrderTests {
    static JsonObject Call(string tool, string? flowRunId, string? participant = null) {
        var args = new JsonObject();
        if (flowRunId is not null) args["flow_run_id"] = flowRunId;
        if (participant is not null) args["participant"] = participant;

        return new JsonObject { ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args } };
    }

    [Test]
    [Arguments("submit_review_round", "f1", null, "f1\nreviewer")]
    [Arguments("send_to_participant", "f1", "reviewer", "f1\nreviewer")]
    [Arguments("send_to_participant", "f1", "author", "f1\nauthor")]
    [Arguments("submit_review_round", "f2", null, "f2\nreviewer")]
    [Arguments("send_to_participant", "f1", null, null)]
    [Arguments("submit_review_round", null, null, null)]
    [Arguments("get_flow_status", "f1", null, null)]
    [Arguments("start_review_flow", "f1", null, null)]
    public async Task A_round_is_ordered_per_flow_and_participant_and_nothing_else_is(
            string tool, string? flowRunId, string? participant, string? expected) {
        await Assert.That(McpFlowsServer.RoundOrderKey(Call(tool, flowRunId, participant))).IsEqualTo(expected);
    }
}
