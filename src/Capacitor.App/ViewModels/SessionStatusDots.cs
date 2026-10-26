using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// The one session status every surface reads: a single kind, word, and tip.
public static class SessionStatusDots {
    /// The daemon's finished-turn verdict, for an agent the user can answer: a flow participant
    /// between rounds waits on the flow, so nothing here may describe it as waiting on the user.
    public static bool WaitsOnUser(AgentStatusDto dto) =>
        dto.AwaitingInput == true && AgentActionService.AcceptsTypedInput(dto.Kind);

    /// Failed, waiting on the user, a usage-limit question, or a terminal dialog. A pending
    /// permission is the other source.
    public static bool NeedsAttention(AgentStatusDto dto) =>
        dto.Status is "Failed" or "Interrupted" || WaitsOnUser(dto) || UsageLimitNoticeDto.IsQuestion(dto.UsageLimit)
     || dto.TerminalDialog is not null;

    /// The merged-row twins of the two rules above, for rail rows from either lane.
    public static bool WaitsOnUser(AgentRow row) =>
        row.AwaitingInput == true && AgentActionService.AcceptsTypedInput(row.Kind);

    public static bool NeedsAttention(AgentRow row) =>
        row.Status is "Failed" or "Interrupted" || WaitsOnUser(row) || UsageLimitNoticeDto.IsQuestion(row.UsageLimit)
     || row.TerminalDialog is not null;

    /// Running, and either mid-turn or with subagents the daemon still counts or background
    /// commands the app saw start. A parent that is also waiting still counts as working; the
    /// wait is a line on the status tip.
    public static bool IsWorking(string status, bool? awaitingInput, int? liveSubagents, int liveCommands = 0) =>
        status == "Running" && (awaitingInput == false || liveSubagents > 0 || liveCommands > 0);

    /// The short word every session surface shows. Detail lives on <see cref="Present"/>.
    public static string Label(AgentStatusDto dto) =>
        Present(dto.Status, dto.AwaitingInput, WaitsOnUser(dto), dto.LiveSubagents,
            pending: false, UsageLimitSummary(dto.UsageLimit), launchStage: null, elapsed: null,
            terminalDialog: dto.TerminalDialog is not null, interruption: dto.Interruption, agentId: dto.Id).Label;

    public static string Label(AgentRow row) => ForRow(row, pending: false).Label;

    public static AgentStatusPresentation ForRow(AgentRow row, bool pending, bool answerExpected = false, int liveCommands = 0) =>
        Present(
            row.Status, row.AwaitingInput, WaitsOnUser(row), row.LiveSubagents, pending,
            UsageLimitSummary(row.UsageLimit),
            row.Origin == AgentOrigin.Pending ? LaunchStages.Label(row.LaunchStage) : null,
            elapsed: null,
            requester: row.RequesterDisplay, borrowedFrom: row.BorrowedFrom, answerExpected: answerExpected,
            model: row.Model is { Length: > 0 } known ? HostedHarnessCatalog.ModelLabelFor(row.Vendor, known) : null,
            harness: string.IsNullOrEmpty(row.Vendor) ? null : HostedHarnessCatalog.LabelFor(row.Vendor),
            ended: row.IsEndedSession,
            liveCommands: liveCommands,
            terminalDialog: row.TerminalDialog is not null, interruption: row.Interruption, agentId: row.Id);

    /// First match wins, so two surfaces cannot draw different marks for the same facts.
    /// <paramref name="usageLimitSummary"/> is set only for a question the user must answer.
    public static AgentStatusPresentation Present(
            string status, bool? awaitingInput, bool waitsOnUser, int? liveSubagents, bool pending,
            string? usageLimitSummary, string? launchStage, string? elapsed,
            string? requester = null, string? borrowedFrom = null, bool answerExpected = false,
            string? model = null, string? harness = null, bool ended = false, int liveCommands = 0,
            bool terminalDialog = false, string? interruption = null, string? agentId = null,
            bool turnAttested = false) {
        var kind =
            status == "Failed" ? AgentStatusKind.Failed
            : answerExpected ? AgentStatusKind.Answer
            : pending || usageLimitSummary is not null || terminalDialog ? AgentStatusKind.NeedsYou
            : status == "Interrupted" ? AgentStatusKind.Interrupted
            : status == "Starting" ? AgentStatusKind.Starting
            : status == "Resuming" ? AgentStatusKind.Resuming
            : IsWorking(status, awaitingInput, liveSubagents, liveCommands) ? AgentStatusKind.Working
            : waitsOnUser || status == "Idle" ? AgentStatusKind.Idle
            : status == "Completed" ? AgentStatusKind.Done
            : AgentStatusKind.Other;
        var label = kind switch {
            AgentStatusKind.Failed      => "Failed",
            AgentStatusKind.Answer      => "Answer",
            AgentStatusKind.NeedsYou    => "Needs you",
            AgentStatusKind.Interrupted => "Interrupted",
            AgentStatusKind.Starting    => "Starting",
            AgentStatusKind.Resuming    => "Resuming",
            AgentStatusKind.Working     => "Working",
            AgentStatusKind.Idle        => "Idle",
            AgentStatusKind.Done        => "Done",
            _                           => status,
        };
        if (string.IsNullOrEmpty(label)) return AgentStatusPresentation.None;

        // Elapsed and a "Starting …" stage already name the status, so they are the value
        // rather than a second line under the short word.
        var sentence =
            kind == AgentStatusKind.Working && !string.IsNullOrEmpty(elapsed) ? elapsed
            : kind == AgentStatusKind.Starting && launchStage is { } stage && stage.StartsWith("Starting", StringComparison.Ordinal) ? stage
            : kind == AgentStatusKind.Answer ? "An answer is expected"
            : kind == AgentStatusKind.Resuming ? "Resuming after a daemon restart"
            : kind == AgentStatusKind.Interrupted ? "Not resumed after a daemon restart"
            : label;
        var facts = new List<AgentStatusFact> { new(sentence, "Status") };
        Add(facts, harness, "Harness");
        Add(facts, model, "Model");
        Add(facts, usageLimitSummary, "Usage limit");
        if (kind == AgentStatusKind.Interrupted) {
            Add(facts, interruption, "Reason");
            Add(facts, $"Retry with `kcap agent resume{(agentId is null ? "" : " " + agentId)}`, or stop it to discard it.");
        }
        if (terminalDialog) Add(facts, "Waiting for you in Terminal");
        if (pending && kind != AgentStatusKind.Answer) Add(facts, "Pending response");
        if (waitsOnUser && kind is not AgentStatusKind.Idle and not AgentStatusKind.Answer)
            Add(facts, "Waiting for input.");
        if (liveSubagents is int live and > 0)
            Add(facts, $"{live} subagent{(live == 1 ? "" : "s")} running");
        if (liveCommands > 0)
            Add(facts, $"{liveCommands} command{(liveCommands == 1 ? "" : "s")} running");
        if (kind == AgentStatusKind.Starting && sentence == "Starting") Add(facts, launchStage, "Launch");
        Add(facts, requester, "Requester");
        Add(facts, borrowedFrom, "Borrowed from");
        return new AgentStatusPresentation(kind, label, FormatTip(facts),
            kind is AgentStatusKind.Working or AgentStatusKind.Starting or AgentStatusKind.Resuming, facts) {
            Ended = ended, InForegroundTurn = kind == AgentStatusKind.Working && turnAttested,
        };
    }

    static void Add(List<AgentStatusFact> facts, string? text, string? caption = null) {
        if (!string.IsNullOrEmpty(text)) facts.Add(new(text, caption));
    }

    /// Blank line between facts, caption under the value. The first line stays the sentence.
    static string FormatTip(IReadOnlyList<AgentStatusFact> facts) {
        var lines = new List<string>();
        foreach (var fact in facts) {
            if (lines.Count > 0) lines.Add("");
            lines.Add(fact.Text);
            if (fact.HasCaption) lines.Add(fact.Caption!);
        }
        return string.Join('\n', lines);
    }

    /// Dominant surfaced status for a collapsed session group. Null when every child is settled or
    /// only carrying the daemon's own word. The accessible name counts the dominant kind.
    public static AgentStatusPresentation? Rollup(
            IEnumerable<AgentRow> rows, IReadOnlySet<string> pending, IReadOnlySet<string>? answering = null,
            IReadOnlyDictionary<string, int>? commands = null) {
        var presented = rows
            .Select(row => (Row: row, Status: ForRow(row, pending.Contains(row.Id), answering?.Contains(row.Id) ?? false,
                commands?.GetValueOrDefault(row.Key) ?? 0)))
            .Where(item => item.Status.HasLabel)
            .OrderBy(item => (int)item.Status.Kind)
            .ThenBy(item => item.Row.Id, StringComparer.Ordinal)
            .ToList();
        var surfaced = presented.Where(item => item.Status.Kind is not AgentStatusKind.Done and not AgentStatusKind.Other).ToList();
        if (surfaced.Count == 0) return null;
        var dominant = surfaced[0].Status.Kind;
        var count = surfaced.Count(item => item.Status.Kind == dominant);
        var name = RollupName(dominant, count);
        var facts = new List<AgentStatusFact> { new(name, "Status") };
        foreach (var item in presented) {
            var who = string.IsNullOrEmpty(item.Row.Title) ? item.Row.Id : item.Row.Title;
            var sentence = item.Status.Facts.Count > 0 ? item.Status.Facts[0].Text : item.Status.Label;
            string? detail = null;
            if (item.Status.Facts.Count > 1)
                detail = string.Join('\n', item.Status.Facts.Skip(1).Select(fact =>
                    fact.HasCaption ? $"{fact.Caption}\n{fact.Text}" : fact.Text));
            facts.Add(new($"{who} — {sentence}", detail));
        }
        return surfaced[0].Status with { Tip = FormatTip(facts), Facts = facts };
    }

    static string RollupName(AgentStatusKind kind, int count) {
        var sessions = count == 1 ? "session" : "sessions";
        var state = kind switch {
            AgentStatusKind.Failed      => "failed",
            AgentStatusKind.Answer      => count == 1 ? "needs an answer" : "need an answer",
            AgentStatusKind.NeedsYou    => count == 1 ? "needs you" : "need you",
            AgentStatusKind.Interrupted => "interrupted",
            AgentStatusKind.Starting    => "starting",
            AgentStatusKind.Resuming    => "resuming",
            AgentStatusKind.Working     => "working",
            AgentStatusKind.Idle        => "idle",
            _                           => "active",
        };
        return $"{count} {sessions} {state}";
    }

    static string? UsageLimitSummary(UsageLimitNoticeDto? notice) =>
        UsageLimitNoticeDto.IsQuestion(notice) ? notice!.Summary : null;

    /// Process is gone — Completed/Failed stay in the snapshot until teardown removes the agent.
    public static bool IsTerminal(string? status) => status is "Completed" or "Failed";
}
