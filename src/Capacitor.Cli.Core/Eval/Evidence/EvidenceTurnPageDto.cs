using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceTurnPageDto {
    [JsonPropertyName("scope_version")] public required string                     ScopeVersion { get; init; }
    [JsonPropertyName("source_id")]     public required string                     SourceId     { get; init; }
    [JsonPropertyName("turns")]         public          List<EvidenceTurnEntryDto> Turns        { get; init; } = [];
    [JsonPropertyName("next_cursor")]   public          string?                    NextCursor   { get; init; }
}
