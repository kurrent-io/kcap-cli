using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// A server with ACP id encoding on reports a hosted agent's `sess-1` as its token, which still confirms the worker.
public class LooseEndClaimToolResultsTests {
    const string Token = "senc1vptdh45epitvqf2ovpurmdnpg3c";

    [Test]
    [Arguments("sess-1", false)]
    [Arguments(Token, false)]
    [Arguments("sess-2", true)]
    public async Task A_completion_confirms_the_worker_by_its_raw_or_encoded_id(string reported, bool isError) {
        var body = $$"""{"state":"closed","claim_id":"c1","loose_end_id":"end","session_id":"{{reported}}"}""";

        var (_, error) = LooseEndClaimToolResults.RenderCompletion(200, body, "c1", "sess-1");

        await Assert.That(error).IsEqualTo(isError);
    }

    [Test]
    [Arguments(Token, true)]
    [Arguments("sess-2", false)]
    public async Task A_worker_filtered_list_confirms_claims_held_under_the_encoded_id(string reported, bool confirms) {
        var body = $$"""{"items":[{"claims":[{"claim_id":"c1","status":"in_progress","session_id":"{{reported}}"}]}]}""";

        await Assert.That(LooseEndClaimToolResults.ConfirmsWorker(body, "sess-1")).IsEqualTo(confirms);
    }
}
