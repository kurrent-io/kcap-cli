using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One selectable option of an ACP permission or elicitation. OptionId is the identity that goes
/// back to the agent; Label is display only and may collide across options. A multi-select
/// elicitation stamps the same MinSelections/MaxSelections pair on every option; read the first
/// option carrying a complete pair and never combine partial stamps.
public sealed record AcpInteractionOption {
    [JsonPropertyName("option_id")]      public required string OptionId { get; init; }
    [JsonPropertyName("label")]          public required string Label { get; init; }
    [JsonPropertyName("description")]    public string? Description { get; init; }
    [JsonPropertyName("kind")]           public string? Kind { get; init; }
    [JsonPropertyName("min_selections")] public int? MinSelections { get; init; }
    [JsonPropertyName("max_selections")] public int? MaxSelections { get; init; }
}
