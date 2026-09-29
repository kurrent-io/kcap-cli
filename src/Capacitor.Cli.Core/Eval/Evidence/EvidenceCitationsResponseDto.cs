using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceCitationsResponseDto {
    [JsonPropertyName("scope_version")] public required string                         ScopeVersion { get; init; }
    [JsonPropertyName("citations")]     public          List<EvidenceCitationEntryDto> Citations    { get; init; } = [];
}
