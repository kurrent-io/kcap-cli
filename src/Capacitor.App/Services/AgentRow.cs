using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// One merged row. Key is SOURCE-scoped ("local:{id}" / "remote:{id}" / "pending:{id}" /
/// "server:{sessionId}") so the lanes can never clobber each other; Id is the logical agent id
/// workspaces bind to, which for a server row is the session id itself.
public sealed record AgentRow(
        string Key, AgentOrigin Origin, string Id, string? SessionId, string Kind, string Vendor, string Status,
        DateTime CreatedAt, string? RepoPath, string? Title, string? Model, string? RequesterDisplay,
        string? WorktreePath, string? WorkLocation, string? BorrowedFrom,
        string? MachineBadge, // remote rows: the daemon name; local rows: null
        string RepoGroupKey, string RepoGroupLabel, string CheckoutKey, string CheckoutLabel,
        // Null means unknown (the server registry carries no turn verdict), never "working".
        bool? AwaitingInput = null,
        // The runtime's latest handshake stage on a pending row; null on every published row.
        string? LaunchStage = null,
        // The daemon's count of running subagents; null on a remote or pending row.
        int? LiveSubagents = null,
        // A usage-limit question or notice from the local daemon. Null on a remote or pending row.
        UsageLimitNoticeDto? UsageLimit = null,
        // The select dialog on the agent's terminal. Null on a pending or server row, and from an older server.
        TerminalDialogDto? TerminalDialog = null,
        // Why the local daemon could not resume the agent after a restart; null unless Interrupted.
        string? Interruption = null,
        // The server's owner id for the session; null until the server has listed it.
        string? OwnerUserId = null,
        // The owner's display name or username from the listing, for the initials on another user's row.
        string? OwnerName = null,
        // The owner's picture from the listing, drawn over the initials once it loads.
        string? OwnerAvatarUrl = null,
        // Whether the owner is the signed-in account; null while the server has not said.
        bool? OwnerIsViewer = null,
        DateTime? LastActivityAt = null,
        DateTime? EndedAt = null,
        // The server's stale flag on a live session: no activity for over an hour.
        bool IsStale = false,
        // A live server row no daemon hosts, past the hand-over grace: started in a terminal.
        bool IsTerminalSession = false,
        // The listing's access level for the viewer; below "full" the server grants no chat.
        string? AccessLevel = null,
        // The PR links the listing carried for the session; null while no listing row carried the field.
        RepoSessionPullRequests? ListedPullRequests = null,
        // The account the session runs under. Null from an older daemon and on a pending row.
        string? AccountDirectory = null,
        string? AccountId = null,
        // The branch the listing reported; null below full access and on a daemon row.
        string? Branch = null,
        string? AccountLabel = null) {

    /// A session the server lists as ended; a daemon row's Completed is not this.
    public bool IsEndedSession => Origin == AgentOrigin.Server && Status == "Completed";

    public static AgentRow FromLocal(AgentStatusDto dto, RepoIdentity repo) => new(
        Key: $"local:{dto.Id}", Origin: AgentOrigin.Local, Id: dto.Id, SessionId: dto.SessionId, Kind: dto.Kind,
        Vendor: dto.Vendor, Status: dto.Status, CreatedAt: dto.CreatedAt, RepoPath: dto.RepoPath,
        Title: dto.Title, Model: dto.Model, RequesterDisplay: dto.RequesterDisplay,
        WorktreePath: dto.WorktreePath, WorkLocation: dto.WorkLocation, BorrowedFrom: dto.BorrowedFrom,
        MachineBadge: null, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
        CheckoutKey: ViewModels.SessionRailViewModel.WorktreeKeyFor(dto), CheckoutLabel: "",
        AwaitingInput: dto.AwaitingInput, LiveSubagents: dto.LiveSubagents, UsageLimit: dto.UsageLimit,
        TerminalDialog: dto.TerminalDialog, Interruption: dto.Interruption,
        AccountDirectory: dto.AccountDirectory, AccountId: dto.AccountId, AccountLabel: dto.AccountLabel);

    public static AgentRow FromRemote(AgentInstanceDto dto) {
        var daemonKey = $"{dto.OwnerUserId}/{dto.DaemonName}";
        var repo = RepoIdentityResolver.ForRemote(dto.RepoOwner, dto.RepoName, dto.RepoPath, daemonKey);
        return new AgentRow(
            Key: $"remote:{dto.AgentId}", Origin: AgentOrigin.Remote, Id: dto.AgentId, SessionId: dto.SessionId,
            Kind: "agent", Vendor: dto.Vendor ?? "", Status: CanonicalRemoteStatus(dto.Status), CreatedAt: dto.RegisteredAt,
            RepoPath: dto.RepoPath, Title: dto.Title is { Length: > 0 } ? dto.Title : TitleFromPrompt(dto.Prompt),
            Model: dto.Model, RequesterDisplay: null,
            WorktreePath: null, WorkLocation: null, BorrowedFrom: null,
            MachineBadge: dto.DaemonName, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
            CheckoutKey: $"@{daemonKey}", CheckoutLabel: $"on {dto.DaemonName}",
            TerminalDialog: dto.TerminalDialog is { } dialog
                ? new TerminalDialogDto(dialog.Heading, dialog.Body, dialog.Options, dialog.Selected, dialog.Screen)
                : null,
            OwnerUserId: dto.OwnerUserId,
            AccountDirectory: dto.AccountDirectory, AccountId: dto.AccountId, AccountLabel: dto.AccountLabel);
    }

    /// A row from the server's listing. The status is the daemon vocabulary the rail already reads:
    /// Running for a live session, Completed for an ended one, with IsEndedSession telling the two
    /// kinds of Completed apart. `hostedPresentation` keeps a live row looking hosted while a daemon
    /// row for the same session has just gone, so a hand-over never flashes as a terminal session.
    public static AgentRow FromServer(RepoSessionDto dto, bool hostedPresentation) {
        var active = dto.Status == "active";
        var repo = RepoIdentityResolver.ForRemote(dto.Repo?.Owner, dto.Repo?.Name, dto.Cwd, "server");
        // The listing names only the cwd. A hosted agent's is its worktree, whose path names the
        // checkout that owns it; any other cwd could be a linked worktree just the same, so it is
        // not presented as one.
        var owner = dto.Cwd is { } cwd ? GitRepository.AgentWorktreeOwner(cwd) : null;
        var worktree = owner is not null && owner.Length != dto.Cwd!.Length ? dto.Cwd : null;
        return new(
            Key: $"server:{dto.SessionId}", Origin: AgentOrigin.Server, Id: dto.SessionId, SessionId: dto.SessionId,
            Kind: "session", Vendor: dto.Vendor ?? "", Status: active ? "Running" : "Completed",
            CreatedAt: dto.StartedAt.UtcDateTime, RepoPath: owner, Title: dto.Title, Model: null, RequesterDisplay: null,
            WorktreePath: worktree, WorkLocation: null, BorrowedFrom: null,
            MachineBadge: null, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
            CheckoutKey: "@server", CheckoutLabel: "",
            OwnerUserId: dto.Owner?.UserId, OwnerName: dto.Owner?.DisplayName ?? dto.Owner?.Username,
            OwnerAvatarUrl: dto.Owner?.AvatarUrl,
            LastActivityAt: dto.LastActivityAt.UtcDateTime, EndedAt: dto.EndedAt?.UtcDateTime,
            IsStale: dto.Stale, IsTerminalSession: active && !hostedPresentation, AccessLevel: dto.AccessLevel,
            ListedPullRequests: dto.PullRequests, Branch: dto.Branch);
    }

    static string CanonicalRemoteStatus(string status) => status.ToLowerInvariant() switch {
        "starting" => "Starting",
        "running" => "Running",
        "idle" => "Idle",
        "completed" => "Completed",
        "failed" => "Failed",
        _ => status,
    };

    /// A launch the local daemon is still starting: no session, no worktree yet, and it opens as
    /// a local workspace because the daemon will publish it as one.
    public static AgentRow FromPending(PendingLaunchDto dto, RepoIdentity repo) => new(
        Key: $"pending:{dto.Id}", Origin: AgentOrigin.Pending, Id: dto.Id, SessionId: null, Kind: dto.Kind ?? "agent",
        Vendor: dto.Vendor, Status: "Starting", CreatedAt: dto.CreatedAt, RepoPath: dto.RepoPath,
        Title: dto.Title, Model: null, RequesterDisplay: null,
        WorktreePath: null, WorkLocation: null, BorrowedFrom: null,
        MachineBadge: null, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
        CheckoutKey: PlatformPaths.Normalize(dto.RepoPath ?? ""), CheckoutLabel: "",
        LaunchStage: dto.Stage);

    /// The app's own stand-in for a launch the server accepted before the daemon reports it.
    public static AgentRow Placeholder(
            string agentId, string vendor, string repoPath, string? title, string? model, DateTime createdAt, RepoIdentity repo) =>
        FromPending(new PendingLaunchDto(agentId, vendor, repoPath, title, createdAt, null), repo)
            with { Model = string.IsNullOrEmpty(model) ? null : model };

    internal static string? TitleFromPrompt(string? prompt) {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var line = prompt.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length <= 80 ? line : line[..80];
    }
}
