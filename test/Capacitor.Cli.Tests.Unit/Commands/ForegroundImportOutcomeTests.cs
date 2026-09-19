using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ForegroundImportOutcomeTests {
    static readonly ImportRunSelection Sel = new(["a", "b", "c", "rest"], ["a", "b", "c"], RemainderExists: true);
    static ImportCommand.ImportRunOutcome Outcome(ImportRunPartition? p) => new(FakeImportRunner.ZeroCounts, 0, p);

    [Test]
    public async Task Fault_before_selection_is_incomplete_with_no_candidates_and_a_remainder() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(1, null, null, new InvalidOperationException("boom")));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Incomplete);
        await Assert.That(o.RunCandidateIds).IsNull();
        await Assert.That(o.RemainderExists).IsTrue();
        await Assert.That(o.Selected + o.Succeeded + o.Skipped + o.Failed).IsEqualTo(0);
    }

    [Test]
    public async Task Fault_after_selection_keeps_the_candidates_and_zero_counts() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(1, Sel, null, new InvalidOperationException("boom")));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Incomplete);
        await Assert.That(o.RunCandidateIds).IsEquivalentTo(Sel.RunCandidateIds);
        await Assert.That(o.Selected).IsEqualTo(3);
        await Assert.That(o.Succeeded).IsEqualTo(0);
        await Assert.That(o.SucceededIds).IsEmpty();
    }

    [Test]
    public async Task Completed_pass_partitions_every_selected_id() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(0, Sel, Outcome(new(["a"], ["b"], ["c"])), null));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Complete);
        await Assert.That(o.Selected).IsEqualTo(o.Succeeded + o.Skipped + o.Failed);
        await Assert.That(o.SucceededIds).IsEquivalentTo(["a"]);
    }

    [Test]
    public async Task Outcome_without_selection_is_incomplete() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(0, null, Outcome(null), null));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Incomplete);
    }

    [Test]
    public async Task Empty_selection_and_partition_are_complete_with_nothing_to_do() {
        var o = ForegroundImportOutcome.From(new SetupImportRun(0, ImportRunSelection.Empty, Outcome(ImportRunPartition.Empty), null));

        await Assert.That(o.Certainty).IsEqualTo(ForegroundCertainty.Complete);
        await Assert.That(o.RunCandidateIds).IsEmpty();
        await Assert.That(o.RemainderExists).IsFalse();
    }
}
