using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceEventPageDto {
    [JsonPropertyName("scope_version")] public required string                      ScopeVersion { get; init; }
    [JsonPropertyName("source_id")]     public required string                      SourceId     { get; init; }
    [JsonPropertyName("entries")]       public          List<EvidenceEventEntryDto> Entries      { get; init; } = [];
    [JsonPropertyName("next_cursor")]   public          string?                     NextCursor   { get; init; }
}
