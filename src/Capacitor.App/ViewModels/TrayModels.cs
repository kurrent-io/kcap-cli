using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

/// Last path segment of a repo path, shared by every surface that names a repository. The status
/// wire says which path is a worktree, so a path's shape is never read as evidence of one.
public static class RepoLabel {
    public static string Leaf(string? repoPath) => repoPath is null ? "—" : PlatformPaths.Leaf(repoPath);
}

/// The checkout a session is presented under, shared by the rail's grouping and the workspace
/// subtitle so the two cannot disagree: the checkout a reviewer borrowed comes first, so a
/// snapshot reviewer sits beside the session it reviews rather than under its private copy.
public static class CheckoutLabel {
    /// Null from an older daemon, whose RepoPath is the checkout.
    public static string? CheckoutPathFor(AgentStatusDto dto) => dto.BorrowedFrom ?? dto.WorktreePath;

    public static bool IsMain(string checkout, string repoRoot) =>
        PlatformPaths.Comparer.Equals(checkout, repoRoot);

    public static string Format(string checkout, string repoRoot) =>
        IsMain(checkout, repoRoot) ? "main checkout" : PlatformPaths.Leaf(checkout);
}

// StopEnabled: false while AgentActionService.StopsInFlight contains Id. Kind is the wire
// KindText spelling (agent|review|review-flow) — carried through so the Stop click handler can
// pass it to AgentActionService.RequestStop, which decides protected-ness (decision 5).
public sealed record TrayAgentEntry(string Id, string Label, string Kind, bool StopEnabled);
public sealed record TrayPauseItem(bool Enabled, bool Checked);

/// The server lane's contribution to the tray verdict: live remote agents (twin-suppressed
/// rows excluded already) and whether the lane is up.
public readonly record struct RemoteTraySummary(int RemoteLiveAgents, bool LaneConnected);
// ShimInstallVisible (spec §5): "Install command-line tool…" tray-item visibility — trailing
// with a default so every existing positional/object-initializer call site stays valid.
// UpdateItemLabel: the coordinator's current label for the tray's single update item, or null
// while it should not show at all.
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause, int PendingConsent, bool ShimInstallVisible = false,
    string? UpdateItemLabel = null);
