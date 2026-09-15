using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Ordered: a session claimed on more than one lane resolves to the lowest.
public enum AgentOrigin { Local, Remote, Pending }

/// One merged row. Key is SOURCE-scoped ("local:{id}" / "remote:{id}" / "pending:{id}") so the
/// lanes can never clobber each other; Id is the logical agent id workspaces bind to.
public sealed record AgentRow(
        string Key, AgentOrigin Origin, string Id, string? SessionId, string Kind, string Vendor, string Status,
        DateTime CreatedAt, string? RepoPath, string? Title, string? Model, string? RequesterDisplay,
        string? WorktreePath, string? WorkLocation, string? BorrowedFrom,
        string? MachineBadge, // remote rows: the daemon name; local rows: null
        string RepoGroupKey, string RepoGroupLabel, string CheckoutKey, string CheckoutLabel,
        // Null means unknown (the server registry carries no turn verdict), never "working".
        bool? AwaitingInput = null,
        // The runtime's latest handshake stage on a pending row; null on every published row.
        string? LaunchStage = null) {

    public static AgentRow FromLocal(AgentStatusDto dto, RepoIdentity repo) => new(
        Key: $"local:{dto.Id}", Origin: AgentOrigin.Local, Id: dto.Id, SessionId: dto.SessionId, Kind: dto.Kind,
        Vendor: dto.Vendor, Status: dto.Status, CreatedAt: dto.CreatedAt, RepoPath: dto.RepoPath,
        Title: dto.Title, Model: dto.Model, RequesterDisplay: dto.RequesterDisplay,
        WorktreePath: dto.WorktreePath, WorkLocation: dto.WorkLocation, BorrowedFrom: dto.BorrowedFrom,
        MachineBadge: null, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
        CheckoutKey: ViewModels.SessionRailViewModel.WorktreeKeyFor(dto), CheckoutLabel: "",
        AwaitingInput: dto.AwaitingInput);

    public static AgentRow FromRemote(AgentInstanceDto dto) {
        var daemonKey = $"{dto.OwnerUserId}/{dto.DaemonName}";
        var repo = RepoIdentityResolver.ForRemote(dto.RepoOwner, dto.RepoName, dto.RepoPath, daemonKey);
        return new(
            Key: $"remote:{dto.AgentId}", Origin: AgentOrigin.Remote, Id: dto.AgentId, SessionId: dto.SessionId,
            Kind: "agent", Vendor: dto.Vendor ?? "", Status: dto.Status, CreatedAt: dto.RegisteredAt,
            RepoPath: dto.RepoPath, Title: TitleFromPrompt(dto.Prompt), Model: dto.Model, RequesterDisplay: null,
            WorktreePath: null, WorkLocation: null, BorrowedFrom: null,
            MachineBadge: dto.DaemonName, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
            CheckoutKey: $"@{daemonKey}", CheckoutLabel: $"on {dto.DaemonName}");
    }

    /// A launch the local daemon is still starting: no session, no worktree yet, and it opens as
    /// a local workspace because the daemon will publish it as one.
    public static AgentRow FromPending(PendingLaunchDto dto, RepoIdentity repo) => new(
        Key: $"pending:{dto.Id}", Origin: AgentOrigin.Pending, Id: dto.Id, SessionId: null, Kind: "agent",
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
