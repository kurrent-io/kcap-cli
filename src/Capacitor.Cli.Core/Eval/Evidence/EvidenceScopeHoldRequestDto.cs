using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Names a held scope by its token, to renew or release it; mirrors the server's <c>EvidenceScopeHoldRequest</c>.</summary>
public sealed record EvidenceScopeHoldRequestDto {
    [JsonPropertyName("token")] public required string Token { get; init; }
}
