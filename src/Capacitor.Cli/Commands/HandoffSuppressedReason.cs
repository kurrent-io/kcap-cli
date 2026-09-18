namespace Capacitor.Cli.Commands;

internal enum HandoffSuppressedReason { ImportFailed, NoNewSessions, NothingLanded, AnalyticsNotInPlan, SkillNotInstalled, NoAgentDetected }

internal static class HandoffSuppressedReasonExtensions {
    public static string Wire(this HandoffSuppressedReason reason) => reason switch {
        HandoffSuppressedReason.ImportFailed       => "import_failed",
        HandoffSuppressedReason.NoNewSessions      => "no_new_sessions",
        HandoffSuppressedReason.NothingLanded      => "nothing_landed",
        HandoffSuppressedReason.AnalyticsNotInPlan => "analytics_not_in_plan",
        HandoffSuppressedReason.SkillNotInstalled  => "skill_not_installed",
        HandoffSuppressedReason.NoAgentDetected    => "no_agent_detected",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
