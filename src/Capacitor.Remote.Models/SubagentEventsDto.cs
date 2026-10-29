using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One page of a subagent's own events. A next start is set while more events follow this page.
public sealed record SubagentEventsDto {
    [JsonPropertyName("last_event_number")] public long LastEventNumber { get; init; } = -1;
    [JsonPropertyName("events")]            public SessionEventDto[]? Events { get; init; }
    [JsonPropertyName("next_from")]         public int? NextFrom { get; init; }
}
