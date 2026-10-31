using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// Answer to the server's search route. The lane that was not asked for is null.
public sealed record SearchResponseDto {
    [JsonPropertyName("sessions")]    public SessionSearchHitDto[]? Sessions { get; init; }
    [JsonPropertyName("transcripts")] public TranscriptSearchHitDto[]? Transcripts { get; init; }
    [JsonPropertyName("took_ms")]     public long TookMs { get; init; }
}
