using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceBodyChunkDto {
    [JsonPropertyName("reference")]   public required string Reference  { get; init; }
    [JsonPropertyName("field")]       public required string Field      { get; init; }
    [JsonPropertyName("ordinal")]     public          int?   Ordinal    { get; init; }
    [JsonPropertyName("encoding")]    public required string Encoding   { get; init; }
    [JsonPropertyName("content")]     public required string Content    { get; init; }
    [JsonPropertyName("next_offset")] public          long?  NextOffset { get; init; }
}
