using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record RepoSessionOwnerDto {
    [JsonPropertyName("user_id")]      public required string UserId { get; init; }
    [JsonPropertyName("username")]     public string? Username { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("avatar_url")]   public string? AvatarUrl { get; init; }
}
