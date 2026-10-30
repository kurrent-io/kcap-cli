using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli;

/// <summary>
/// The SessionStart "waiting" nudge: a hosted agent with a follow-up due later schedules a wake-up through
/// kcap-flows instead of waiting in its own shell. Only a hosted agent gets it, because <c>schedule_wakeup</c>
/// refuses a terminal session and a flow participant.
/// </summary>
static class WakeUpNudgeEmitter {
    public const string Nudge =
        "## Waiting\n" +
        "This session is a hosted agent. When its next step is due later, with nothing to do until then (CI or the " +
        "reviewers' findings after a push, or a request to check back in a few minutes), call `schedule_wakeup` from " +
        "kcap-flows with what to check, then end your turn. Do not wait with `sleep`, a background command or a " +
        "polling loop: kcap cannot see those, and they end with your process.";

    /// <param name="codexConfigPath">The availability gate's test seam; null in production.</param>
    public static string? Resolve(HarnessId harness, HarnessRegistry harnesses, SessionStartAudience? audience,
                                  string? codexConfigPath = null) {
        if (audience is not { IsHosted: true, IsFlowParticipant: false }) return null;

        return McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-flows", codexConfigPath, audience) ? Nudge : null;
    }
}
