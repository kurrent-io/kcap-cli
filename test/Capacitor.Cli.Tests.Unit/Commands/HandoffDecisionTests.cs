using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HandoffDecisionTests {
    static ForegroundImportOutcome O(ForegroundCertainty c = ForegroundCertainty.Complete, int selected = 5, int succeeded = 5, int skipped = 0, int failed = 0,
                                      bool remainder = true, bool knownCandidates = true, int candidates = 10) =>
        new(c, selected, succeeded, skipped, failed, remainder,
            knownCandidates ? Enumerable.Range(0, candidates).Select(i => $"c{i}").ToList() : null,
            Enumerable.Range(0, succeeded).Select(i => $"c{i}").ToList());

    static HandoffDecision Decide(ForegroundImportOutcome o, BackgroundImportStatus bg = BackgroundImportStatus.Running,
                                  bool analytics = true, int eligible = 2, int detected = 2) =>
        HandoffDecision.Decide(o, bg, analytics, eligible, detected);

    [Test] public async Task Row1_incomplete_with_nothing_landed_is_import_failed() =>
        await Assert.That(Decide(O(ForegroundCertainty.Incomplete, succeeded: 0)).Reason).IsEqualTo(HandoffSuppressedReason.ImportFailed);

    [Test] public async Task Row2_failed_background_with_nothing_landed_is_import_failed() =>
        await Assert.That(Decide(O(succeeded: 0, failed: 5), bg: BackgroundImportStatus.Failed).Reason).IsEqualTo(HandoffSuppressedReason.ImportFailed);

    [Test] public async Task Row3_empty_known_candidates_is_no_new_sessions_whatever_the_background() =>
        await Assert.That(Decide(O(selected: 0, succeeded: 0, candidates: 0), bg: BackgroundImportStatus.Running).Reason).IsEqualTo(HandoffSuppressedReason.NoNewSessions);

    [Test] public async Task Row4_all_skipped_with_nothing_left_is_nothing_landed() =>
        await Assert.That(Decide(O(succeeded: 0, skipped: 5, remainder: false), bg: BackgroundImportStatus.NotNeeded).Reason).IsEqualTo(HandoffSuppressedReason.NothingLanded);

    [Test] public async Task Row5_cached_denial_over_a_good_pass_is_analytics_not_in_plan() =>
        await Assert.That(Decide(O(), analytics: false).Reason).IsEqualTo(HandoffSuppressedReason.AnalyticsNotInPlan);

    [Test] public async Task Row6_no_eligible_vendor_with_one_detected_is_skill_not_installed() =>
        await Assert.That(Decide(O(), eligible: 0, detected: 1).Reason).IsEqualTo(HandoffSuppressedReason.SkillNotInstalled);

    [Test] public async Task Row7_no_vendor_detected_is_no_agent_detected() =>
        await Assert.That(Decide(O(), eligible: 0, detected: 0).Reason).IsEqualTo(HandoffSuppressedReason.NoAgentDetected);

    [Test] public async Task Row8_offers_on_a_success_or_a_running_background() {
        await Assert.That(Decide(O()).Offered).IsTrue();
        await Assert.That(Decide(O(succeeded: 0, skipped: 5), bg: BackgroundImportStatus.Running).Offered).IsTrue();
        await Assert.That(Decide(O(succeeded: 0, skipped: 5), bg: BackgroundImportStatus.ExitedZero).Offered).IsTrue();
    }

    [Test] public async Task Import_outcome_outranks_the_plan_gate() {
        await Assert.That(Decide(O(ForegroundCertainty.Incomplete, succeeded: 0), analytics: false).Reason).IsEqualTo(HandoffSuppressedReason.ImportFailed);
        await Assert.That(Decide(O(selected: 0, succeeded: 0, candidates: 0), analytics: false).Reason).IsEqualTo(HandoffSuppressedReason.NoNewSessions);
    }

    [Test] public async Task Unknown_candidates_with_a_running_background_are_offered() =>
        await Assert.That(Decide(O(ForegroundCertainty.Incomplete, succeeded: 2, knownCandidates: false)).Offered).IsTrue();
}
