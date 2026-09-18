using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One model the server offers for a vendor's launch dropdown, from GET api/agents/model-options.
/// Value is the exact model id carried on the launch wire; Label is the friendly display name.
public sealed record VendorModelOptionDto {
    [JsonPropertyName("value")] public required string Value { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
}
