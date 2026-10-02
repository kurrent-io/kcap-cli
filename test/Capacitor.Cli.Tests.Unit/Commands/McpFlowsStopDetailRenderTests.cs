using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A reaped reviewer's round reads <c>participant_died</c> — the exact text drivers match — and
/// the daemon's code for why it ended the reviewer rides beside it as <c>stop_detail</c>. Asserted
/// through each real formatter, the polled path most of all, since that is the one a driver reads.</summary>
public class McpFlowsStopDetailRenderTests {
    const string BlockingRound =
        """{"flow_run_id":"f1","round_id":"r1","status":"unclear","result_kind":"unclear","result_text":"participant_died","result_detail":"pi_reviewer_turn_timeout"}""";
    const string PolledRound =
        """{"flow_run_id":"f1","status":"running","round_number":2,"round_result_kind":"unclear","round_result_text":"participant_died","round_result_detail":"pi_reviewer_turn_timeout"}""";
    const string StatusRun =
        """{"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t","last_result_kind":"unclear","last_result_text":"participant_died","round_result_detail":"pi_reviewer_turn_timeout"}""";
    const string CrashedRound =
        """{"flow_run_id":"f1","round_id":"r1","status":"unclear","result_kind":"unclear","result_text":"participant_died"}""";

    [Test] public async Task The_blocking_round_shows_the_stop_code_and_keeps_the_text() {
        var text = McpFlowsServer.FormatRoundResponse(BlockingRound);
        await Assert.That(text).Contains("stop_detail: pi_reviewer_turn_timeout");
        await Assert.That(text).EndsWith("\nparticipant_died");
    }

    [Test] public async Task The_polled_round_shows_the_stop_code_and_keeps_the_text() {
        var text = McpFlowsServer.FormatPolledRoundResult(JsonNode.Parse(PolledRound)!.AsObject(), "f1");
        await Assert.That(text).Contains("stop_detail: pi_reviewer_turn_timeout");
        await Assert.That(text).EndsWith("\nparticipant_died");
    }

    [Test] public async Task The_status_shows_the_stop_code() =>
        await Assert.That(McpFlowsServer.FormatStatusResponse(StatusRun)).Contains("stop_detail: pi_reviewer_turn_timeout");

    [Test] public async Task A_death_without_a_code_prints_no_stop_detail_line() =>
        await Assert.That(McpFlowsServer.FormatRoundResponse(CrashedRound)).DoesNotContain("stop_detail");
}
