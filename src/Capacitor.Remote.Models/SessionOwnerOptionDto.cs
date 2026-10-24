using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One owner the server lists sessions for. IsMe is resolved on the server; Ids holds the
/// canonical id and every alias merged into it, so a client compares against the set, never
/// against one spelling.
public sealed record SessionOwnerOptionDto {
    [JsonPropertyName("user_id")]      public required string UserId { get; init; }
    [JsonPropertyName("username")]     public string? Username { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("avatar_url")]   public string? AvatarUrl { get; init; }
    [JsonPropertyName("is_me")]        public bool IsMe { get; init; }
    [JsonPropertyName("ids")]          public string[]? Ids { get; init; }
}
