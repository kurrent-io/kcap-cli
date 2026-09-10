using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One canonical event as the detail endpoint presents it. The payload is proto3 JSON with
/// snake_case field names, mirrored under both `payload` and `data`; readers take `payload`
/// and fall back to `data`.
public sealed record SessionEventDto {
    [JsonPropertyName("event_type")]   public required string EventType { get; init; }
    [JsonPropertyName("event_number")] public long EventNumber { get; init; } = -1;
    [JsonPropertyName("payload")]      public JsonElement? Payload { get; init; }
    [JsonPropertyName("data")]         public JsonElement? Data { get; init; }
    [JsonIgnore] public JsonElement? Body => Payload is { ValueKind: JsonValueKind.Object } p ? p : Data;
}
