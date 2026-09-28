using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// The one session status every surface reads: a single kind, word, and tip.
public static class SessionStatusDots {
    /// The daemon's finished-turn verdict, for an agent the user can answer: a flow participant
    /// between rounds waits on the flow, so nothing here may describe it as waiting on the user.
    public static bool WaitsOnUser(AgentStatusDto dto) =>
        dto.AwaitingInput == true && !AgentActionService.IsProtectedKind(dto.Kind);

    /// Failed, waiting on the user, or a usage-limit question. A pending permission is the other source.
    public static bool NeedsAttention(AgentStatusDto dto) =>
        dto.Status == "Failed" || WaitsOnUser(dto) || UsageLimitNoticeDto.IsQuestion(dto.UsageLimit);

    /// The merged-row twins of the two rules above, for rail rows from either lane.
    public static bool WaitsOnUser(AgentRow row) =>
        row.AwaitingInput == true && !AgentActionService.IsProtectedKind(row.Kind);

    public static bool NeedsAttention(AgentRow row) =>
        row.Status == "Failed" || WaitsOnUser(row) || UsageLimitNoticeDto.IsQuestion(row.UsageLimit);

    /// Running, and either mid-turn or with subagents the daemon still counts. A parent that is
    /// also waiting still counts as working; the wait is a line on the status tip.
    public static bool IsWorking(string status, bool? awaitingInput, int? liveSubagents) =>
        status == "Running" && (awaitingInput == false || liveSubagents > 0);

    /// The short word every session surface shows. Detail lives on <see cref="Present"/>.
    public static string Label(AgentStatusDto dto) =>
        Present(dto.Status, dto.AwaitingInput, WaitsOnUser(dto), dto.LiveSubagents,
            pending: false, UsageLimitSummary(dto.UsageLimit), launchStage: null, elapsed: null, sessionId: null).Label;

    public static string Label(AgentRow row) => ForRow(row, pending: false).Label;

    public static AgentStatusPresentation ForRow(AgentRow row, bool pending) =>
        Present(
            row.Status, row.AwaitingInput, WaitsOnUser(row), row.LiveSubagents, pending,
            UsageLimitSummary(row.UsageLimit),
            row.Origin == AgentOrigin.Pending ? LaunchStages.Label(row.LaunchStage) : null,
            elapsed: null, sessionId: row.SessionId ?? row.Id,
            row.RequesterDisplay, row.BorrowedFrom is null ? null : $"borrowed {row.BorrowedFrom}");

    /// First match wins, so two surfaces cannot draw different marks for the same facts.
    /// <paramref name="usageLimitSummary"/> is set only for a question the user must answer.
    public static AgentStatusPresentation Present(
            string status, bool? awaitingInput, bool waitsOnUser, int? liveSubagents, bool pending,
            string? usageLimitSummary, string? launchStage, string? elapsed, string? sessionId,
            params string?[] more) {
        var kind =
            status == "Failed" ? AgentStatusKind.Failed
            : pending || usageLimitSummary is not null ? AgentStatusKind.NeedsYou
            : status == "Starting" ? AgentStatusKind.Starting
            : IsWorking(status, awaitingInput, liveSubagents) ? AgentStatusKind.Working
            : waitsOnUser ? AgentStatusKind.Idle
            : status == "Completed" ? AgentStatusKind.Done
            : AgentStatusKind.Other;
        var label = kind switch {
            AgentStatusKind.Failed   => "Failed",
            AgentStatusKind.NeedsYou => "Needs you",
            AgentStatusKind.Starting => "Starting",
            AgentStatusKind.Working  => "Working",
            AgentStatusKind.Idle     => "Idle",
            AgentStatusKind.Done     => "Done",
            _                        => status,
        };
        if (string.IsNullOrEmpty(label)) return AgentStatusPresentation.None;

        var sentence = kind switch {
            AgentStatusKind.Idle     => "Idle. Waiting for input.",
            AgentStatusKind.NeedsYou => "Needs you.",
            AgentStatusKind.Working  => "Working.",
            AgentStatusKind.Starting => "Starting.",
            AgentStatusKind.Failed   => "Failed.",
            AgentStatusKind.Done     => "Done.",
            _                        => $"{label}.",
        };
        var lines = new List<string> { sentence };
        if (!string.IsNullOrEmpty(usageLimitSummary)) lines.Add(usageLimitSummary);
        if (pending) lines.Add("Pending response");
        if (waitsOnUser && kind != AgentStatusKind.Idle) lines.Add("Waiting for input.");
        if (liveSubagents is int live and > 0)
            lines.Add($"{live} subagent{(live == 1 ? "" : "s")} running");
        if (kind == AgentStatusKind.Starting && !string.IsNullOrEmpty(launchStage)) lines.Add(launchStage);
        if (kind == AgentStatusKind.Working && !string.IsNullOrEmpty(elapsed)) lines.Add(elapsed);
        if (!string.IsNullOrEmpty(sessionId)) lines.Add(sessionId);
        foreach (var line in more)
            if (!string.IsNullOrEmpty(line)) lines.Add(line);
        return new AgentStatusPresentation(kind, label, string.Join('\n', lines),
            kind is AgentStatusKind.Working or AgentStatusKind.Starting);
    }

    /// Dominant surfaced status for a collapsed worktree. Null when every child is settled or
    /// only carrying the daemon's own word. The accessible name counts the dominant kind.
    public static AgentStatusPresentation? Rollup(IEnumerable<AgentRow> rows, IReadOnlySet<string> pending) {
        var presented = rows
            .Select(row => (Row: row, Status: ForRow(row, pending.Contains(row.Id))))
            .Where(item => item.Status.HasLabel)
            .OrderBy(item => (int)item.Status.Kind)
            .ThenBy(item => item.Row.Id, StringComparer.Ordinal)
            .ToList();
        var surfaced = presented.Where(item => item.Status.Kind is not AgentStatusKind.Done and not AgentStatusKind.Other).ToList();
        if (surfaced.Count == 0) return null;
        var dominant = surfaced[0].Status.Kind;
        var count = surfaced.Count(item => item.Status.Kind == dominant);
        var name = RollupName(dominant, count);
        var lines = new List<string> { name };
        foreach (var item in presented) {
            var who = string.IsNullOrEmpty(item.Row.Title) ? item.Row.Id : item.Row.Title;
            lines.Add($"{who} — {item.Status.Tip.Replace('\n', ' ')}");
        }
        return surfaced[0].Status with { Tip = string.Join('\n', lines) };
    }

    static string RollupName(AgentStatusKind kind, int count) {
        var sessions = count == 1 ? "session" : "sessions";
        var state = kind switch {
            AgentStatusKind.Failed   => "failed",
            AgentStatusKind.NeedsYou => count == 1 ? "needs you" : "need you",
            AgentStatusKind.Starting => "starting",
            AgentStatusKind.Working  => "working",
            AgentStatusKind.Idle     => "idle",
            _                        => "active",
        };
        return $"{count} {sessions} {state}";
    }

    static string? UsageLimitSummary(UsageLimitNoticeDto? notice) =>
        UsageLimitNoticeDto.IsQuestion(notice) ? notice!.Summary : null;

    /// Process is gone — Completed/Failed stay in the snapshot until teardown removes the agent.
    public static bool IsTerminal(string? status) => status is "Completed" or "Failed";
}
