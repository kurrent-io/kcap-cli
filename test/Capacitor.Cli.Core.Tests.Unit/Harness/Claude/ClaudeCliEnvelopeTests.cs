using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// <summary>A dropped error envelope keeps its subtype: a spend cap becomes its own failure kind, a turn cap is told
/// apart by subtype, and the legacy mapping still reads both as chat_error.</summary>
public class ClaudeCliEnvelopeTests {
    const string SpendEnvelope = """{"type":"result","subtype":"error_max_budget_usd","is_error":true,"result":"","num_turns":9,"total_cost_usd":1.01}""";
    const string TurnsEnvelope = """{"type":"result","subtype":"error_max_turns","is_error":true,"result":"","num_turns":52}""";

    [Test]
    public async Task A_spend_cap_envelope_is_spend_budget_with_its_subtype_kept() {
        var outcome = ClaudeCliRunner.ClassifyFailure(SpendEnvelope, nonZeroExit: true);
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.SpendBudget);
        await Assert.That(outcome.Subtype).IsEqualTo("error_max_budget_usd");
        await Assert.That(outcome.Result).IsNull();
    }

    [Test]
    public async Task A_turn_cap_envelope_keeps_its_subtype_on_a_process_failure() {
        var outcome = ClaudeCliRunner.ClassifyFailure(TurnsEnvelope, nonZeroExit: true);
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Subtype).IsEqualTo("error_max_turns");
        await Assert.That(ClaudeCliRunner.ClassifyFailure("not json", nonZeroExit: false).Failure).IsEqualTo(ClaudeCliFailure.OutputUnparseable);
    }

    [Test]
    public async Task The_legacy_mapping_reads_both_caps_as_chat_error_and_the_evidence_mapping_names_them() {
        var spend = ClaudeCliRunner.ClassifyFailure(SpendEnvelope, true);
        var turns = ClaudeCliRunner.ClassifyFailure(TurnsEnvelope, true);

        await Assert.That(EvalService.LegacyFailureCode(spend.Failure!.Value)).IsEqualTo(EvalFailureCodes.ChatError);
        await Assert.That(EvalService.LegacyFailureCode(turns.Failure!.Value)).IsEqualTo(EvalFailureCodes.ChatError);
        await Assert.That(EvalService.LegacyFailureCode(ClaudeCliFailure.Timeout)).IsEqualTo(EvalFailureCodes.JudgeTimeout);
        await Assert.That(EvalService.LegacyFailureCode(ClaudeCliFailure.OutputUnparseable)).IsEqualTo(EvalFailureCodes.VerdictParseFailed);

        await Assert.That(EvalService.EvidenceFailureCode(spend)).IsEqualTo(EvalFailureCodes.SpendBudget);
        await Assert.That(EvalService.EvidenceFailureCode(turns)).IsEqualTo(EvalFailureCodes.IterationCap);
        await Assert.That(EvalService.EvidenceFailureCode(new ClaudeCliOutcome(null, ClaudeCliFailure.Timeout))).IsEqualTo(EvalFailureCodes.JudgeTimeout);
        await Assert.That(EvalService.EvidenceFailureCode(new ClaudeCliOutcome(null, ClaudeCliFailure.OutputUnparseable))).IsEqualTo(EvalFailureCodes.VerdictParseFailed);
        await Assert.That(EvalService.EvidenceFailureCode(new ClaudeCliOutcome(null, ClaudeCliFailure.ProcessFailure))).IsEqualTo(EvalFailureCodes.ChatError);
    }
}
