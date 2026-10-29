using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceBodyDescriptorDto {
    [JsonPropertyName("field")]   public required string Field   { get; init; }
    [JsonPropertyName("ordinal")] public          int?   Ordinal { get; init; }
    [JsonPropertyName("bytes")]   public          long   Bytes   { get; init; }
    [JsonPropertyName("ref")]     public required string Ref     { get; init; }
}
