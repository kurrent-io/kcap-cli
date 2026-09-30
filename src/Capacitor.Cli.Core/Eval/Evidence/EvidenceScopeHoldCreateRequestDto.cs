using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Posted to <c>/evidence-scope/holds</c>; mirrors the server's <c>EvidenceScopeHoldCreateRequest</c>.</summary>
public sealed record EvidenceScopeHoldCreateRequestDto {
    [JsonPropertyName("request_id")]       public required string RequestId       { get; init; }
    [JsonPropertyName("continuations")]    public required bool   Continuations   { get; init; }
    [JsonPropertyName("delegates")]        public required bool   Delegates       { get; init; }
    [JsonPropertyName("adopted_children")] public required bool   AdoptedChildren { get; init; }
}
