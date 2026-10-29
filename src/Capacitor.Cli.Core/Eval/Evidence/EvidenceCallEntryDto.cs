using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceCallEntryDto {
    [JsonPropertyName("ordinal")]        public          int                        Ordinal       { get; init; }
    [JsonPropertyName("tool")]           public          string?                    Tool          { get; init; }
    [JsonPropertyName("arguments")]      public          JsonElement?               Arguments     { get; init; }
    [JsonPropertyName("arguments_body")] public          EvidenceBodyDescriptorDto? ArgumentsBody { get; init; }
    [JsonPropertyName("call_body")]      public required EvidenceBodyDescriptorDto  CallBody      { get; init; }
}
