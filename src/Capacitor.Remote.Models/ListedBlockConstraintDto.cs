using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A null ResetsAt is the unknown constraint, which never lapses by time.
public sealed record ListedBlockConstraintDto {
    [JsonPropertyName("limit_id")]       public string? LimitId { get; init; }
    [JsonPropertyName("window_minutes")] public int? WindowMinutes { get; init; }
    [JsonPropertyName("resets_at")]      public DateTimeOffset? ResetsAt { get; init; }
    [JsonPropertyName("signaled_at")]    public DateTimeOffset SignaledAt { get; init; }
}
