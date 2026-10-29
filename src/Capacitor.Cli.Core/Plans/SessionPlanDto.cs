using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Plans;

/// One plan on `GET /api/sessions/{id}/plans`. `IsCurrent` marks the plan the requested session
/// last wrote to; a continued session that has not written yet has none.
public sealed record SessionPlanDto {
    [JsonPropertyName("plan_id")]    public string                  PlanId    { get; init; } = "";
    [JsonPropertyName("is_current")] public bool                    IsCurrent { get; init; }
    [JsonPropertyName("documents")]  public List<PlanDocumentDto>   Documents { get; init; } = [];
    [JsonPropertyName("tasks")]      public List<PlanLedgerTaskDto> Tasks     { get; init; } = [];
}
