using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Exercises the actual production value-flags list (EvalCommand.ValueFlags) that Program.cs's eval
// case passes to ResolveSessionId — not a locally duplicated copy — so a future flag added to one
// but not the other still fails here. --baseline-out must be declared value-bearing there, or its
// path argument gets mistaken for the sessionId (and, depending on position, the real sessionId
// gets skipped as if it were that path's value).
public class EvalBaselineOutArgTests {
    [Test]
    public async Task Baseline_out_value_is_not_mistaken_for_session_id() {
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--baseline-out", "out.json", "sess-abc"],
            valueFlags: EvalCommand.ValueFlags
        );

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    [Test]
    public async Task Baseline_out_beside_chain_does_not_swallow_the_next_positional() {
        var id = ArgParsing.ResolveSessionId(
            ["eval", "sess-abc", "--chain", "--baseline-out", "out.json"],
            valueFlags: EvalCommand.ValueFlags
        );

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    [Test]
    public async Task Baseline_out_after_the_positional_is_skipped() {
        var id = ArgParsing.ResolveSessionId(
            ["eval", "sess-xyz", "--baseline-out", "out.json"],
            valueFlags: EvalCommand.ValueFlags
        );

        await Assert.That(id).IsEqualTo("sess-xyz");
    }

    [Test]
    public async Task Baseline_out_combined_with_skip_does_not_swallow_the_positional() {
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--skip", "efficiency", "--baseline-out", "out.json", "sess-combo"],
            valueFlags: EvalCommand.ValueFlags
        );

        await Assert.That(id).IsEqualTo("sess-combo");
    }

    [Test]
    public async Task Baseline_out_with_a_path_passes_validation() {
        await Assert.That(EvalCommand.ValidateValueFlags(["eval", "sess-abc", "--baseline-out", "out.json"])).IsNull();
    }

    [Test]
    public async Task Baseline_out_as_the_last_token_is_rejected_not_silently_skipped() {
        var error = EvalCommand.ValidateValueFlags(["eval", "sess-abc", "--baseline-out"]);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("--baseline-out requires a value");
    }

    [Test]
    public async Task Baseline_out_swallowing_the_next_flag_is_rejected() {
        var error = EvalCommand.ValidateValueFlags(["eval", "sess-abc", "--baseline-out", "--chain"]);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("--baseline-out requires a value (got '--chain')");
    }

    [Test]
    public async Task Empty_questions_selection_still_passes_validation() {
        await Assert.That(EvalCommand.ValidateValueFlags(["eval", "sess-abc", "--questions", ""])).IsNull();
    }
}
