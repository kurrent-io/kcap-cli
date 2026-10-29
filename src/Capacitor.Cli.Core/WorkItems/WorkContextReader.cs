namespace Capacitor.Cli.Core.WorkItems;

public enum WorkContextReadKind { Ready, SessionUnknown, SignedOut, NotInPlan, Unreachable }

/// One read of a session's work context, totalized. <see cref="WorkContextReadKind.Ready"/> with a
/// null <see cref="Primary"/> is the no-work-item state; <see cref="ItemFailed"/>,
/// <see cref="TopologyFailed"/> and <see cref="SummaryFailed"/> degrade a section without failing
/// the read. <see cref="Item"/> is served for the primary and may carry a different id than it.
/// <see cref="Viewer"/> is read only when asked for; <see cref="ViewerFailed"/> means no usable
/// answer arrived, so the caller should ask again: no response, a 2xx without a body, a 408, 429 or
/// 5xx. Any other refusal, such as an older server's 404, is an answer.
public sealed record WorkContextRead(
        WorkContextReadKind                         Kind,
        IReadOnlyList<SessionWorkItemAssignmentDto> Assignments,
        SessionWorkItemAssignmentDto?               Primary,
        WorkItemDto?                                Item,
        WorkItemTopologyDto?                        Topology,
        SessionSummaryDto?                          Summary,
        bool                                        ItemFailed,
        bool                                        TopologyFailed,
        bool                                        SummaryFailed,
        string?                                     Detail) {
    public ViewerIdentityDto? Viewer       { get; init; }
    public bool               ViewerFailed { get; init; }

    public static WorkContextRead Of(WorkContextReadKind kind, string? detail = null) =>
        new(kind, [], null, null, null, null, false, false, false, detail);
}

public static class WorkContextReader {
    public const string PlanGateError = "work_items_not_in_plan";

    /// Assignments and summary run concurrently; the item and topology reads follow the assignments
    /// together, so both overlap the summary rather than queueing behind it. Every call started is
    /// awaited before anything is classified. A final 401 anywhere signs the read out: the retry
    /// handler has already spent the refresh, and only that outcome makes the source drop its client.
    public static Task<WorkContextRead> ReadAsync(IWorkContextChannel channel, string sessionId, CancellationToken ct) =>
        ReadAsync(channel, sessionId, readViewer: false, ct);

    public static async Task<WorkContextRead> ReadAsync(IWorkContextChannel channel, string sessionId, bool readViewer, CancellationToken ct) {
        var summaryTask = channel.GetSessionSummaryAsync(sessionId, ct);
        var viewerTask  = readViewer ? channel.GetViewerIdentityAsync(ct) : null;
        var assignments = await channel.GetSessionAssignmentsAsync(sessionId, ct).ConfigureAwait(false);

        var rows    = assignments.Succeeded ? assignments.Body! : [];
        var primary = rows.FirstOrDefault(r => r.IsPrimary) ?? rows.FirstOrDefault();
        var itemTask     = primary is null ? null : channel.GetWorkItemAsync(primary.WorkItemId, ct);
        var topologyTask = primary is null ? null : channel.GetTopologyAsync(primary.WorkItemId, ct);

        var summary  = await summaryTask.ConfigureAwait(false);
        var item     = itemTask is null ? null : await itemTask.ConfigureAwait(false);
        var topology = topologyTask is null ? null : await topologyTask.ConfigureAwait(false);
        var viewer   = viewerTask is null ? null : await viewerTask.ConfigureAwait(false);

        if (assignments.StatusCode == 401 || summary.StatusCode == 401 || item?.StatusCode == 401 || topology?.StatusCode == 401
         || viewer?.StatusCode == 401)
            return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        switch (assignments) {
            case { Succeeded: true }: break;
            case { StatusCode: >= 200 and < 300 }: return WorkContextRead.Of(WorkContextReadKind.Unreachable, "malformed response");
            case { StatusCode: 404 }: return WorkContextRead.Of(WorkContextReadKind.SessionUnknown);
            default:
                return PlanGated(assignments, summary) ?? WorkContextRead.Of(WorkContextReadKind.Unreachable, StatusDetail(assignments.StatusCode));
        }

        if (item is not null && PlanGated(item, summary) is { } gatedItem) return gatedItem;
        if (topology is not null && PlanGated(topology, summary) is { } gated) return gated;

        return new WorkContextRead(
            WorkContextReadKind.Ready, rows, primary,
            item is { Succeeded: true } ? item.Body : null,
            topology is { Succeeded: true } ? topology.Body : null,
            summary.Succeeded ? summary.Body : null,
            ItemFailed: item is { Succeeded: false },
            TopologyFailed: topology is { Succeeded: false },
            SummaryFailed: !summary.Succeeded,
            Detail: null) {
            Viewer       = viewer is { Succeeded: true } ? viewer.Body : null,
            ViewerFailed = viewer is { StatusCode: 0 or 408 or 429 or >= 500 } or { StatusCode: >= 200 and < 300, Body: null },
        };
    }

    /// Every work-item route shares the plan gate, and a plan change between the calls must not
    /// leave the pane ready on retained data. The summary is not gated, so it rides along.
    static WorkContextRead? PlanGated<T>(WorkContextOutcome<T> outcome, WorkContextOutcome<SessionSummaryDto> summary) where T : class =>
        IsPlanGate(outcome.StatusCode, outcome.Error)
            ? WorkContextRead.Of(WorkContextReadKind.NotInPlan, outcome.Error!.Message) with {
                Summary = summary.Succeeded ? summary.Body : null, SummaryFailed = !summary.Succeeded,
            }
            : null;

    /// <summary>Whether a work-item route refused the call because the tenant's plan lacks Work Items.</summary>
    public static bool IsPlanGate(int status, WorkItemErrorDto? error) => status == 403 && error?.Error == PlanGateError;

    static string StatusDetail(int status) => status == 0 ? "no response" : $"status {status}";
}
