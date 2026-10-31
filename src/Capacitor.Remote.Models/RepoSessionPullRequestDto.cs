using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A PR a listed session links to, as the server last recorded it. No GitHub read backs it, so it
/// carries no access window. State is open, draft, merged or closed, and null where the server
/// holds no lifecycle for the PR.
public sealed record RepoSessionPullRequestDto {
    [JsonPropertyName("repo_hash")] public required string RepoHash { get; init; }
    [JsonPropertyName("owner")]     public required string Owner { get; init; }
    [JsonPropertyName("repo_name")] public required string RepoName { get; init; }
    [JsonPropertyName("number")]    public required int Number { get; init; }
    [JsonPropertyName("url")]       public string? Url { get; init; }
    [JsonPropertyName("title")]     public string? Title { get; init; }
    [JsonPropertyName("state")]     public string? State { get; init; }
    [JsonPropertyName("merged_at")] public DateTimeOffset? MergedAt { get; init; }
    [JsonPropertyName("closed_at")] public DateTimeOffset? ClosedAt { get; init; }
}
