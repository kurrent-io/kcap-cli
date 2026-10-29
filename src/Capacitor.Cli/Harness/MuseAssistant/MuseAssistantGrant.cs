using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

public sealed record MuseAssistantGrant(
        [property: JsonPropertyName("id")]            string Id,
        [property: JsonPropertyName("boundary")]      string Boundary,
        [property: JsonPropertyName("after_seq")]     long?  AfterSeq,
        [property: JsonPropertyName("granted_at_ms")] long   GrantedAtMs,
        [property: JsonPropertyName("revoked_at_ms")] long?  RevokedAtMs
    ) {
    [JsonIgnore] public bool Revoked => RevokedAtMs is not null;

    /// <summary>Items at or below this seq are outside the grant; null means every item is inside.</summary>
    [JsonIgnore] public long? Floor => Boundary == MuseAssistantGrantBoundaries.AfterSeq ? AfterSeq : null;
}
