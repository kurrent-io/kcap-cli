namespace Capacitor.Cli.Core.Policy;

/// <summary>
/// <c>POST /api/policy/judge</c>. The action and the inline snapshot are the hook-route records
/// verbatim: the server deserializes them with the hook contract types, so a rename here breaks both
/// routes at once.
/// </summary>
/// <param name="Refusals">Required on every request: the server answers 400 without it.</param>
/// <param name="Snapshot">Sent until the spool has delivered the session's snapshot; must name the
/// request's own session and snapshot id.</param>
public sealed record PolicyJudgeRequestV1(
    string SessionId, string? AgentId, string Vendor, string Seam, string SnapshotId, string EngineVersion,
    PolicyActionV1 Action, PolicyJudgeTurnSetV1? Turns, PolicyJudgeRefusalsV1 Refusals,
    PolicySnapshotUploadV1? Snapshot, int BudgetMs);
