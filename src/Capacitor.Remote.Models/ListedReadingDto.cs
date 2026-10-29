using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// An account's latest usage reading, for the generation it names.
public sealed record ListedReadingDto {
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("windows")]    public ListedUsageWindowDto[]? Windows { get; init; }
    [JsonPropertyName("plan")]       public string? Plan { get; init; }
    [JsonPropertyName("credits")]    public ListedCreditsDto? Credits { get; init; }
    [JsonPropertyName("blocked")]    public ListedBlockedMarkerDto? Blocked { get; init; }
    /// When plan and credits were observed; an older server omits it.
    [JsonPropertyName("metadata_at")] public DateTimeOffset? MetadataAt { get; init; }
}
