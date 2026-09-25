namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Mirrors the server's omission vocabulary: <see cref="V1"/> for <c>coverage-v1</c>, <see cref="All"/> for <c>coverage-v2</c>.</summary>
public static class EvalOmissionKinds {
    public const string TurnIndexElided         = "turn_index_elided";
    public const string TurnsNotFetched         = "turns_not_fetched";
    public const string ToolResultTruncated     = "tool_result_truncated";
    public const string FieldElided             = "field_elided";
    public const string EntriesElided           = "entries_elided";
    public const string PlanArtifactTruncated   = "plan_artifact_truncated";
    public const string PlanArtifactUnavailable = "plan_artifact_unavailable";
    public const string PlanArtifactDropped     = "plan_artifact_dropped";
    public const string TraceTailTrimmed        = "trace_tail_trimmed";
    public const string ScopeIncomplete         = "scope_incomplete";
    public const string SourcesNotConsulted     = "sources_not_consulted";
    public const string PagesNotFetched         = "pages_not_fetched";
    public const string BodiesNotFetched        = "bodies_not_fetched";

    public static readonly IReadOnlySet<string> V1 = new HashSet<string>(StringComparer.Ordinal) {
        TurnIndexElided, TurnsNotFetched, ToolResultTruncated, FieldElided, EntriesElided,
        PlanArtifactTruncated, PlanArtifactUnavailable, PlanArtifactDropped, TraceTailTrimmed
    };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(V1, StringComparer.Ordinal) {
        ScopeIncomplete, SourcesNotConsulted, PagesNotFetched, BodiesNotFetched
    };
}
