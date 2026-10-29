using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record ListedBlockedMarkerDto {
    [JsonPropertyName("blocked_at")]  public DateTimeOffset? BlockedAt { get; init; }
    [JsonPropertyName("cleared_at")]  public DateTimeOffset? ClearedAt { get; init; }
    [JsonPropertyName("constraints")] public ListedBlockConstraintDto[]? Constraints { get; init; }
}
