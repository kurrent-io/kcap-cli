using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// What the chat pane knows about its session, whichever lane it came from. FeedKey names the
/// transcript to read — a file path locally, the session id remotely — and a change of key
/// rebuilds the rows. Ended is the lane's verdict that nothing more will arrive.
public sealed record ChatSessionInfo(
        string Status, string StatusLabel, string Vendor, string? Model, string? Root, bool? AwaitingInput, bool Ended,
        string ReadOnlyNotice, string? FeedKey,
        // The daemon's count of running subagents; null on the remote lane and from an older daemon.
        int? LiveSubagents = null) {
    public bool WaitsOnUser => StatusLabel == "Waiting for input";

    /// The daemon dropped the agent before this pane ever saw it.
    public static readonly ChatSessionInfo Gone = new("Completed", "Completed", "", null, null, null, true, "", null);

    public static ChatSessionInfo FromLocal(AgentStatusDto dto, bool ended) => new(
        dto.Status, SessionStatusDots.Label(dto), dto.Vendor, dto.Model,
        // Tool paths are relative to the checkout the agent runs in. An older daemon sends only
        // RepoPath: the repository for a primary, whose worktree beneath it ToolDetail strips, or
        // the borrowed checkout for a reviewer.
        dto.WorktreePath ?? dto.RepoPath, dto.AwaitingInput,
        ended || SessionStatusDots.IsTerminal(dto.Status), ChatTabViewModel.ParticipantNotice(dto), dto.TranscriptPath, dto.LiveSubagents);

    public static ChatSessionInfo FromRemote(AgentRow row, bool ended) => new(
        row.Status, SessionStatusDots.Label(row), row.Vendor, row.Model,
        row.RepoPath, row.AwaitingInput, ended || SessionStatusDots.IsTerminal(row.Status), "", row.SessionId);
}
