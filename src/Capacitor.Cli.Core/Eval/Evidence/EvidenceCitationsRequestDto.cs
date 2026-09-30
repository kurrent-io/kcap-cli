using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceCitationsRequestDto {
    [JsonPropertyName("token")] public required string       Token { get; init; }
    [JsonPropertyName("refs")]  public required List<string> Refs  { get; init; }
}
