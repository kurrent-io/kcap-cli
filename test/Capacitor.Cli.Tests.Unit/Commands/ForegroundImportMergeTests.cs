using Capacitor.Cli.Commands;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ForegroundImportMergeTests {
    static ForegroundImportOutcome Known(string prefix, int candidates, params string[] succeeded) => new(
        ForegroundCertainty.Complete, succeeded.Length, succeeded.Length, 0, 0, RemainderExists: candidates > succeeded.Length,
        [.. Enumerable.Range(0, candidates).Select(i => $"{prefix}{i}")], succeeded);

    static ForegroundImportOutcome Unknown() =>
        new(ForegroundCertainty.Incomplete, 0, 0, 0, 0, RemainderExists: true, null, []);

    [Test]
    public async Task Concatenates_only_me_first() {
        var (merged, _, _) = ForegroundImportMerge.Merge([Known("m", 2, "m0"), Known("s", 3, "s0", "s1")]);

        await Assert.That(merged.RunCandidateIds!).IsEquivalentTo(["m0", "m1", "s0", "s1", "s2"], CollectionOrdering.Matching);
        await Assert.That(merged.SucceededIds).IsEquivalentTo(["m0", "s0", "s1"], CollectionOrdering.Matching);
        await Assert.That(merged.Succeeded).IsEqualTo(3);
        await Assert.That(merged.Certainty).IsEqualTo(ForegroundCertainty.Complete);
        await Assert.That(merged.RemainderExists).IsTrue();
    }

    /// <summary>The watch's all-complete stop rule would otherwise read half a cohort as all of it.</summary>
    [Test]
    public async Task One_unknown_level_is_partial_exact() {
        var (merged, cohort, _) = ForegroundImportMerge.Merge([Unknown(), Known("s", 2, "s0")]);

        await Assert.That(cohort).IsEqualTo(HandoffCohort.PartialExact);
        await Assert.That(merged.RunCandidateIds!).IsEquivalentTo(["s0", "s1"], CollectionOrdering.Matching);
        await Assert.That(merged.Certainty).IsEqualTo(ForegroundCertainty.Incomplete);
    }

    [Test]
    public async Task All_unknown_is_null_candidates() {
        var (merged, cohort, remaining) = ForegroundImportMerge.Merge([Unknown(), Unknown()]);

        await Assert.That(merged.RunCandidateIds).IsNull();
        await Assert.That(cohort).IsNull();
        await Assert.That(remaining).IsNull();
    }

    /// <summary>A level the answer did not choose never ran, so it is absent rather than unknown.</summary>
    [Test]
    public async Task A_single_level_run_is_exact_not_partial() {
        var (merged, cohort, remaining) = ForegroundImportMerge.Merge([Known("s", 3, "s0")]);

        await Assert.That(cohort).IsNull();
        await Assert.That(merged.RunCandidateIds!.Count).IsEqualTo(3);
        await Assert.That(merged.Certainty).IsEqualTo(ForegroundCertainty.Complete);
        await Assert.That(remaining).IsEqualTo(2);
    }

    [Test]
    public async Task Remaining_counts_failed_selected_sessions() {
        var failedOne = new ForegroundImportOutcome(
            ForegroundCertainty.Complete, 2, 1, 0, 1, RemainderExists: false, ["m0", "m1"], ["m0"]);

        var (_, _, remaining) = ForegroundImportMerge.Merge([failedOne, Known("s", 4, "s0", "s1")]);

        await Assert.That(remaining).IsEqualTo(3);
    }

    [Test]
    public async Task Remaining_is_null_when_any_level_is_unknown() {
        var (_, _, remaining) = ForegroundImportMerge.Merge([Known("m", 4, "m0"), Unknown()]);

        await Assert.That(remaining).IsNull();
    }
}
