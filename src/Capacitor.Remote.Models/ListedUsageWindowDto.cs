using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One rate-limit window. A null ConfirmedAt means no fresh vendor response has confirmed the values.
public sealed record ListedUsageWindowDto {
    [JsonPropertyName("limit_id")]       public required string LimitId { get; init; }
    [JsonPropertyName("window_minutes")] public int WindowMinutes { get; init; }
    [JsonPropertyName("used_percent")]   public double UsedPercent { get; init; }
    [JsonPropertyName("resets_at")]      public DateTimeOffset? ResetsAt { get; init; }
    [JsonPropertyName("confirmed_at")]   public DateTimeOffset? ConfirmedAt { get; init; }
}
