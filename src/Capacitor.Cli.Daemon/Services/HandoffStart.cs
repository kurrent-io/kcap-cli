namespace Capacitor.Cli.Daemon.Services;

/// <summary>What a hand-off continues: the ended session's branch and, when it has one, the pull request
/// whose head to start from.</summary>
public sealed record HandoffTarget(string? Branch, int? PullRequestNumber, bool HeadIsFork);

public enum HandoffSource { Branch, ForkPullRequest, DefaultBranch }

public enum HandoffCheckout { CreateBranch, CheckoutBranch, FastForwardBranch, Detached }

public enum HandoffDetachReason { None, CheckedOutElsewhere, LocalBranchDiverged, ForkHead, BranchAbsent }

/// <summary>Where a hand-off worktree starts. <paramref name="Branch"/> is the branch to push to for
/// <see cref="HandoffSource.Branch"/> (detached or not), the pull request's head branch for a fork, and
/// origin's default branch otherwise. <paramref name="LocalBranchSha"/> is the local branch tip seen at
/// resolution, null when there was none; creation refuses if it has moved since.</summary>
public sealed record HandoffStart(
    HandoffSource Source, HandoffCheckout Checkout, string Sha, string? Branch,
    HandoffDetachReason DetachReason, string? LocalBranchSha);

/// <summary>Either a start or why origin could not be read. An unreadable origin is never reported as
/// an absent branch.</summary>
public sealed record HandoffResolution(HandoffStart? Start, string? FetchError);
