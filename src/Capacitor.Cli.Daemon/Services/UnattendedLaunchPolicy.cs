namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1124: gate for unattended (LaunchKind.ReviewFlow) launches. A vendor whose
/// launcher can't run without a human in the loop must be refused BEFORE a worktree
/// is created — otherwise the agent spawns and hangs forever on a permission prompt
/// no one will answer. Pure so it's unit-testable without the full orchestrator.
/// </summary>
internal static class UnattendedLaunchPolicy {
    /// <summary>User-facing rejection message when an unattended launch can't proceed;
    /// <c>null</c> when the launch may continue.</summary>
    public static string? RejectionReason(IHostedAgentLauncher launcher, bool isReviewFlow) =>
        isReviewFlow && !launcher.SupportsUnattended
            ? $"Vendor '{launcher.Vendor}' cannot host an unattended review-flow agent (its launcher has no unattended mode)."
            : null;
}
