namespace Capacitor.Cli.Core.Handoff;

/// <summary>The start a hand-off launch carries, as the server relays it. <c>Checkout</c>, <c>Source</c> and
/// <c>DetachReason</c> are the enum names of <see cref="HandoffStart"/>; <c>HeadOwner</c> and <c>HeadRepo</c>
/// are set only for a fork.</summary>
public sealed record HandoffLaunchStart(
    string SourceSessionId,
    string StartSha,
    string Checkout,
    string? Branch,
    string? LocalBranchSha,
    string Source,
    string DetachReason,
    int? PullRequestNumber,
    bool HeadIsFork,
    string? HeadOwner,
    string? HeadRepo
) {
    /// <summary>The start to create the worktree from, or null when an enum name is not one this build knows.</summary>
    public HandoffStart? ToStart() =>
        Enum.TryParse<HandoffSource>(Source, out var source) && Enum.IsDefined(source)
     && Enum.TryParse<HandoffCheckout>(Checkout, out var checkout) && Enum.IsDefined(checkout)
     && Enum.TryParse<HandoffDetachReason>(DetachReason, out var reason) && Enum.IsDefined(reason)
            ? new HandoffStart(source, checkout, StartSha, Branch, reason, LocalBranchSha)
            : null;
}
