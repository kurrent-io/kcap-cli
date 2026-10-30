using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The server's session summary row.
public sealed record ActivitySessionDto {
    [JsonPropertyName("session_id")]    public required string SessionId { get; init; }
    [JsonPropertyName("slug")]          public string? Slug { get; init; }
    [JsonPropertyName("title")]         public string? Title { get; init; }
    [JsonPropertyName("vendor")]        public string? Vendor { get; init; }
    [JsonPropertyName("cwd")]           public string? Cwd { get; init; }
    /// "active" or "ended".
    [JsonPropertyName("status")]        public required string Status { get; init; }
    [JsonPropertyName("started_at")]    public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("ended_at")]      public DateTimeOffset? EndedAt { get; init; }
    [JsonPropertyName("last_event_at")] public DateTimeOffset? LastEventAt { get; init; }
    [JsonPropertyName("repo_branch")]   public string? RepoBranch { get; init; }
    [JsonPropertyName("repo_owner")]    public string? RepoOwner { get; init; }
    [JsonPropertyName("repo_name")]     public string? RepoName { get; init; }
    [JsonPropertyName("owner_user_id")] public string? OwnerUserId { get; init; }
    [JsonPropertyName("repositories")]  public ActivitySessionRepositoryDto[]? Repositories { get; init; }
}
