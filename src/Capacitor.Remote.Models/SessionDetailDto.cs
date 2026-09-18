using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The members of GET api/sessions/{sessionId}/detail a remote client reads for interrupt
/// reconciliation. Everything else the endpoint returns is ignored.
public sealed record SessionDetailDto {
    [JsonPropertyName("session_id")]        public string? SessionId { get; init; }
    [JsonPropertyName("ended_at")]          public DateTimeOffset? EndedAt { get; init; }
    [JsonPropertyName("last_event_number")] public long LastEventNumber { get; init; } = -1;
    [JsonPropertyName("events")]            public SessionEventDto[]? Events { get; init; }
}
