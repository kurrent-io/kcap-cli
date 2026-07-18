namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// gate for unattended (LaunchKind.ReviewFlow) launches. A vendor whose
/// launcher can't run without a human in the loop must be refused BEFORE a worktree
/// is created — otherwise the agent spawns and hangs forever on a permission prompt
/// no one will answer. Pure so it's unit-testable without the full orchestrator.
/// </summary>
internal static class UnattendedLaunchPolicy {
    /// <summary>User-facing rejection message when an unattended launch can't proceed;
    /// <c>null</c> when the launch may continue.</summary>
    public static string? RejectionReason(IHostedAgentLauncher launcher, bool isReviewFlow) =>
        RejectionReason(launcher.Vendor, launcher.SupportsUnattended, isReviewFlow);

    /// <summary>
    /// Vendor-agnostic overload: the orchestrator now selects by
    /// <see cref="IHostedAgentRuntimeFactory"/> rather than <see cref="IHostedAgentLauncher"/>, so
    /// this takes the vendor token and its <c>SupportsUnattended</c> flag directly instead of a
    /// launcher instance.
    /// </summary>
    public static string? RejectionReason(string vendor, bool supportsUnattended, bool isReviewFlow) =>
        isReviewFlow && !supportsUnattended
            ? $"Vendor '{vendor}' cannot host an unattended review-flow agent (its launcher has no unattended mode)."
            : null;
}
