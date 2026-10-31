using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A transcript-lane hit. Matched spans in Snippet sit between U+0002 and U+0003.
public sealed record TranscriptSearchHitDto {
    [JsonPropertyName("session_id")]         public required string SessionId { get; init; }
    [JsonPropertyName("snippet")]            public required string Snippet { get; init; }
    [JsonPropertyName("slug")]               public string? Slug { get; init; }
    [JsonPropertyName("title")]              public string? Title { get; init; }
    [JsonPropertyName("event_index")]        public long EventIndex { get; init; }
    [JsonPropertyName("speaker")]            public string? Speaker { get; init; }
    [JsonPropertyName("timestamp")]          public string? Timestamp { get; init; }
    [JsonPropertyName("owner_user_id")]      public string? OwnerUserId { get; init; }
    [JsonPropertyName("owner_user")]         public string? OwnerUser { get; init; }
    [JsonPropertyName("owner_display_name")] public string? OwnerDisplayName { get; init; }
    [JsonPropertyName("owner_avatar_url")]   public string? OwnerAvatarUrl { get; init; }
    [JsonPropertyName("repo_hash")]          public string? RepoHash { get; init; }
    [JsonPropertyName("repo_owner")]         public string? RepoOwner { get; init; }
    [JsonPropertyName("repo_name")]          public string? RepoName { get; init; }
    [JsonPropertyName("started_at")]         public string? StartedAt { get; init; }
    [JsonPropertyName("ended_at")]           public string? EndedAt { get; init; }
    [JsonPropertyName("agent_id")]           public string? AgentId { get; init; }
    [JsonPropertyName("turn_index")]         public int? TurnIndex { get; init; }
    [JsonPropertyName("kind")]               public string? Kind { get; init; }
}
