using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A session-lane hit. Matched spans in Snippet sit between U+0002 and U+0003.
public sealed record SessionSearchHitDto {
    [JsonPropertyName("session_id")]         public required string SessionId { get; init; }
    [JsonPropertyName("slug")]               public required string Slug { get; init; }
    [JsonPropertyName("title")]              public required string Title { get; init; }
    [JsonPropertyName("started_at")]         public required string StartedAt { get; init; }
    [JsonPropertyName("matched_field")]      public required string MatchedField { get; init; }
    [JsonPropertyName("snippet")]            public required string Snippet { get; init; }
    [JsonPropertyName("repo_owner")]         public string? RepoOwner { get; init; }
    [JsonPropertyName("repo_name")]          public string? RepoName { get; init; }
    [JsonPropertyName("pr_number")]          public int? PrNumber { get; init; }
    [JsonPropertyName("pr_title")]           public string? PrTitle { get; init; }
    [JsonPropertyName("owner_user")]         public string? OwnerUser { get; init; }
    [JsonPropertyName("owner_avatar_url")]   public string? OwnerAvatarUrl { get; init; }
    [JsonPropertyName("owner_user_id")]      public string? OwnerUserId { get; init; }
    [JsonPropertyName("owner_display_name")] public string? OwnerDisplayName { get; init; }
    [JsonPropertyName("repo_hash")]          public string? RepoHash { get; init; }
    [JsonPropertyName("ended_at")]           public string? EndedAt { get; init; }
}
