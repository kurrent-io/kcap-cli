using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli;

/// <summary>The SessionStart fragments that are functions of the session id alone: the work-items
/// nudge, the plans nudge, and the line that states the id when neither nudge does.</summary>
static class SessionNudges {
    public static string? Resolve(HarnessId harness, string? sessionId, Profile? profile,
                                  HarnessRegistry harnesses, PlanEntitlements plan, string? codexConfigPath = null) {
        var workItems = WorkItemsNudgeEmitter.Resolve(harness, sessionId, profile?.DisableWorkItemsNudge is true, harnesses, plan, codexConfigPath);
        var plans     = PlansNudgeEmitter.Resolve(harness, sessionId, profile?.DisablePlansNudge is true, harnesses, codexConfigPath);

        return HarnessNudgeEmitter.Combine(
            workItems, plans,
            SessionIdLineEmitter.Resolve(harness, sessionId, harnesses, workItems, plans, codexConfigPath));
    }
}
