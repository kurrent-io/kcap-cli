using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

/// Last path segment of a repo path, shared by every surface that names a repository. The status
/// wire says which path is a worktree, so a path's shape is never read as evidence of one.
public static class RepoLabel {
    static readonly char[] WindowsSeparators = ['\\', '/'];

    public static string Leaf(string? repoPath) => repoPath switch {
        null                                                               => "—",
        _ when !OperatingSystem.IsWindows() && IsWindowsShaped(repoPath) => WindowsLeaf(repoPath),
        _                                                                  => PlatformPaths.Leaf(repoPath),
    };

    /// A remote daemon advertises paths in its own OS's form, and this host's path rules cannot split a
    /// Windows one. A Unix path always starts with '/', so a backslash inside one stays part of its name.
    static bool IsWindowsShaped(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
     || path is [_, ':', ..] && char.IsAsciiLetter(path[0]) && (path.Length == 2 || path[2] is '\\' or '/');

    // A drive or share root has no leaf, as Path.GetFileName reports on Windows.
    static string WindowsLeaf(string path) {
        var trimmed  = path.TrimEnd(WindowsSeparators);
        var segments = trimmed.Split(WindowsSeparators, StringSplitOptions.RemoveEmptyEntries);
        var isRoot   = trimmed.StartsWith(@"\\", StringComparison.Ordinal) ? segments.Length <= 2 : segments.Length <= 1;

        return isRoot ? "" : segments[^1];
    }
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

// StopEnabled: false while AgentActionService.StopsInFlight contains Key. Kind is the wire
// KindText spelling (agent|review|review-flow) — carried through so the Stop click handler can
// pass it to AgentActionService.RequestStop, which decides protected-ness. Origin (default
// Local) is threaded through to the same RequestStop call so a remote entry's stop reaches the
// server lane rather than the local socket.
public sealed record TrayAgentEntry(string Id, string Label, string Kind, bool StopEnabled, AgentOrigin Origin = AgentOrigin.Local) {
    /// What the menu items carry as their command parameter, and how this entry's stop is named in
    /// AgentActionService.StopsInFlight: a local and a remote entry can share an agent id, and only
    /// this tells the two apart.
    public string Key => AgentActionService.StopKey(Origin, Id);
}
public sealed record TrayPauseItem(bool Enabled, bool Checked);

/// The server lane's contribution to the tray verdict: live remote agents (twin-suppressed
/// rows excluded already), whether the lane is up, and any remote sessions the owner has been
/// asked to attend to (a permission/question on that session) — SessionsNeedingAttention counts
/// AttentionEntries, never derived independently.
public readonly record struct RemoteTraySummary(
    int RemoteLiveAgents, bool LaneConnected, int SessionsNeedingAttention = 0,
    IReadOnlyList<TrayAgentEntry>? AttentionEntries = null);
// UpdateItemLabel: the coordinator's current label for the tray's single update item, or null
// while it should not show at all.
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause, int PendingConsent, bool ShimInstallVisible = false,
    string? UpdateItemLabel = null) {
    const int ToolTipLimit = 127;

    /// The tray icon's tooltip, which screen readers also read as its name. Windows keeps only
    /// the first 127 characters of a notification-area tooltip.
    public string ToolTip {
        get {
            var text = $"kcap — {Header}";
            return text.Length <= ToolTipLimit ? text : text[..(ToolTipLimit - 1)] + "…";
        }
    }
}
