namespace Capacitor.Cli.Commands;

/// <summary>Whether setup offers the eval-watch handoff, and why not. Rows are evaluated in order
/// and the first match wins; the import's own outcome outranks agent eligibility so retry advice is
/// only ever given when a retry is warranted. No plan gate: the skill reads evals through a route
/// every plan can read, and this is the first thing a new — usually Free — workspace sees.</summary>
internal sealed record HandoffDecision(bool Offered, HandoffSuppressedReason? Reason) {
    public static HandoffDecision Decide(
            ForegroundImportOutcome outcome, BackgroundImportStatus background,
            int eligibleVendors, int detectedVendors) {
        if (outcome.Certainty == ForegroundCertainty.Incomplete && outcome.Succeeded == 0)
            return new(false, HandoffSuppressedReason.ImportFailed);
        if (background == BackgroundImportStatus.Failed && outcome.Succeeded == 0)
            return new(false, HandoffSuppressedReason.ImportFailed);
        if (outcome.RunCandidateIds is { Count: 0 })
            return new(false, HandoffSuppressedReason.NoNewSessions);
        if (outcome.Succeeded == 0 && background == BackgroundImportStatus.NotNeeded)
            return new(false, HandoffSuppressedReason.NothingLanded);
        if (eligibleVendors == 0 && detectedVendors > 0)
            return new(false, HandoffSuppressedReason.SkillNotInstalled);
        if (detectedVendors == 0)
            return new(false, HandoffSuppressedReason.NoAgentDetected);

        return new(true, null);
    }
}
