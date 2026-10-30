using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli;

/// <summary>The SessionStart nudges every harness composes in one place: work items, plans, waiting
/// with a wake-up, the line that states the session id when neither work items nor plans does, and
/// relative worktree links for the session's repository.</summary>
static class SessionNudges {
    public static string? Resolve(HarnessId harness, string? sessionId, Profile? profile, HarnessRegistry harnesses,
                                  PlanEntitlements plan, SessionStartAudience audience, string? cwd, UserHome home,
                                  string? codexConfigPath = null) {
        var workItems = WorkItemsNudgeEmitter.Resolve(harness, sessionId, profile?.DisableWorkItemsNudge is true, harnesses, plan, codexConfigPath, audience);
        var plans     = PlansNudgeEmitter.Resolve(harness, sessionId, profile?.DisablePlansNudge is true, harnesses, codexConfigPath, audience);
        var wakeUps   = WakeUpNudgeEmitter.Resolve(harness, harnesses, audience, codexConfigPath);
        var worktrees = RelativeWorktreesNudgeEmitter.Resolve(
            cwd, profile?.DisableRelativeWorktreesNudge is true || !audience.GetsDriverGuidance, home);

        return HarnessNudgeEmitter.Combine(
            workItems, plans, wakeUps,
            SessionIdLineEmitter.Resolve(harness, sessionId, harnesses, workItems, plans, codexConfigPath, audience),
            worktrees);
    }
}
