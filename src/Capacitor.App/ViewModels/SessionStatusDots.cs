using Avalonia.Media;
using Avalonia.Media.Immutable;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// Status → dot brush for session surfaces (Home cards, the rail). ImmutableSolidColorBrush,
/// not SolidColorBrush: these are built on the daemon client's pump thread and an immutable
/// brush has no thread affinity, which is also what makes the instances shareable.
public static class SessionStatusDots {
    static readonly ImmutableSolidColorBrush RunningDot  = new(Color.Parse(StatusColors.Connected));
    static readonly ImmutableSolidColorBrush StartingDot = new(Color.Parse(StatusColors.InProgress));
    static readonly ImmutableSolidColorBrush FailedDot   = new(Color.Parse(StatusColors.Disrupted));
    static readonly ImmutableSolidColorBrush NeutralDot  = new(Color.Parse(StatusColors.Unavailable));
    static readonly ImmutableSolidColorBrush WaitingDot  = new(Color.Parse(StatusColors.Waiting));

    // Running/Starting/Failed are the daemon's own open vocabulary (AgentOrchestrator); anything
    // else (Completed, or a value this build has never heard of) reads as neutral.
    public static IBrush For(string status) => status switch {
        "Running"  => RunningDot,
        "Starting" => StartingDot,
        "Failed"   => FailedDot,
        _          => NeutralDot,
    };

    /// Waiting-on-user overrides the process word: a live agent whose turn is idle is not Connected.
    public static IBrush For(string status, bool waitsOnUser) => waitsOnUser ? WaitingDot : For(status);

    public static IBrush For(AgentStatusDto dto) => For(dto.Status, WaitsOnUser(dto) || UsageLimitNoticeDto.IsQuestion(dto.UsageLimit));

    public static IBrush For(AgentRow row) => For(row.Status, WaitsOnUser(row) || UsageLimitNoticeDto.IsQuestion(row.UsageLimit));

    /// The daemon's finished-turn verdict, for an agent the user can answer: a flow participant
    /// between rounds waits on the flow, so nothing here may describe it as waiting on the user.
    public static bool WaitsOnUser(AgentStatusDto dto) =>
        dto.AwaitingInput == true && !AgentActionService.IsProtectedKind(dto.Kind);

    /// The needs-you pip's rule from the dto alone (a pending ask is the other source), held
    /// beside the dot vocabulary so the two can never disagree.
    public static bool NeedsAttention(AgentStatusDto dto) =>
        dto.Status == "Failed" || WaitsOnUser(dto) || UsageLimitNoticeDto.IsQuestion(dto.UsageLimit);

    /// The merged-row twins of the two rules above, for rail rows from either lane.
    public static bool WaitsOnUser(AgentRow row) =>
        row.AwaitingInput == true && !AgentActionService.IsProtectedKind(row.Kind);

    public static bool NeedsAttention(AgentRow row) =>
        row.Status == "Failed" || WaitsOnUser(row) || UsageLimitNoticeDto.IsQuestion(row.UsageLimit);

    /// The one busy verdict every surface reads: a live agent that is either mid-turn or still has
    /// subagents the daemon counts. The wait flag keeps its meaning beside it — a parent that
    /// stopped with agents running reads as waiting on the user and busy at once.
    public static bool IsWorking(string status, bool? awaitingInput, int? liveSubagents) =>
        status == "Running" && (awaitingInput == false || liveSubagents > 0);

    /// Display text for the status: the daemon's own word, except for the one state its
    /// vocabulary does not spell, a live agent whose turn is over.
    public static string Label(AgentStatusDto dto) =>
        UsageLimitNoticeDto.IsQuestion(dto.UsageLimit) ? dto.UsageLimit!.Summary
        : WaitsOnUser(dto) ? "Waiting for input" : dto.Status;

    public static string Label(AgentRow row) =>
        UsageLimitNoticeDto.IsQuestion(row.UsageLimit) ? row.UsageLimit!.Summary
        : WaitsOnUser(row) ? "Waiting for input" : row.Status;

    /// Process is gone — Completed/Failed stay in the snapshot until teardown removes the agent.
    public static bool IsTerminal(string? status) => status is "Completed" or "Failed";
}
