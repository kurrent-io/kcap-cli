using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One row of the server's session listing. Below full access the server blanks Branch, Cwd and
/// LastPrompt rather than refusing the row.
public sealed record RepoSessionDto {
    [JsonPropertyName("session_id")]        public required string SessionId { get; init; }
    [JsonPropertyName("slug")]              public string? Slug { get; init; }
    [JsonPropertyName("title")]             public string? Title { get; init; }
    [JsonPropertyName("owner")]             public RepoSessionOwnerDto? Owner { get; init; }
    [JsonPropertyName("vendor")]            public string? Vendor { get; init; }
    /// "active" or "ended".
    [JsonPropertyName("status")]            public required string Status { get; init; }
    /// "overview", "activity" or "full".
    [JsonPropertyName("access_level")]      public required string AccessLevel { get; init; }
    [JsonPropertyName("stale")]             public bool Stale { get; init; }
    [JsonPropertyName("started_at")]        public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("ended_at")]          public DateTimeOffset? EndedAt { get; init; }
    [JsonPropertyName("last_activity_at")]  public required DateTimeOffset LastActivityAt { get; init; }
    [JsonPropertyName("primary_repo_hash")] public string? PrimaryRepoHash { get; init; }
    [JsonPropertyName("is_primary")]        public bool IsPrimary { get; init; }
    [JsonPropertyName("branch")]            public string? Branch { get; init; }
    [JsonPropertyName("cwd")]               public string? Cwd { get; init; }
    [JsonPropertyName("last_prompt")]       public string? LastPrompt { get; init; }
    /// Null when the server does not send the field at all; withheld below full access.
    [JsonPropertyName("pull_requests")]     public RepoSessionPullRequests? PullRequests { get; init; }
    [JsonPropertyName("repo")]              public RepoSessionRepositoryDto? Repo { get; init; }
}
