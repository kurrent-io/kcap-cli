using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record ListedCreditsDto {
    [JsonPropertyName("has_credits")] public bool HasCredits { get; init; }
    [JsonPropertyName("unlimited")]   public bool Unlimited { get; init; }
    [JsonPropertyName("balance")]     public string? Balance { get; init; }
}
