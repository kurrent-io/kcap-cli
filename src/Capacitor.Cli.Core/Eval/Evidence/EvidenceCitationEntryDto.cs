using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceCitationEntryDto {
    [JsonPropertyName("ref")]    public required string  Ref    { get; init; }
    [JsonPropertyName("state")]  public required string  State  { get; init; }
    [JsonPropertyName("digest")] public          string? Digest { get; init; }
    [JsonPropertyName("code")]   public          string? Code   { get; init; }
}
