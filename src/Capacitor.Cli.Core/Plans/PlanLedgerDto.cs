using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Plans;

/// The declared task list riding `GET /api/sessions/{id}/plan-artifacts` as `ledger`. Absent on a
/// server without the plan ledger and on a session with nothing declared. `IsComplete` is false and
/// `WithheldContributions` counts the rows when contributions from sessions the viewer cannot see
/// were excluded.
public sealed record PlanLedgerDto {
    List<PlanLedgerTaskDto> _tasks = [];

    [JsonPropertyName("plan_id")]                public string?                 PlanId                { get; init; }
    [JsonPropertyName("tasks")]                  public List<PlanLedgerTaskDto> Tasks                 { get => _tasks; init => _tasks = value ?? []; }
    [JsonPropertyName("completed")]              public int                     Completed             { get; init; }
    [JsonPropertyName("total")]                  public int                     Total                 { get; init; }
    [JsonPropertyName("total_known")]            public bool                    TotalKnown            { get; init; }
    [JsonPropertyName("is_complete")]            public bool                    IsComplete            { get; init; } = true;
    [JsonPropertyName("withheld_contributions")] public int                     WithheldContributions { get; init; }
}
