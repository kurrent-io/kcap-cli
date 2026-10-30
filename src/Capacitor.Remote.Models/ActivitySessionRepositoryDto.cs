using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record ActivitySessionRepositoryDto {
    [JsonPropertyName("repo_hash")]  public required string RepoHash { get; init; }
    [JsonPropertyName("owner")]      public string? Owner { get; init; }
    [JsonPropertyName("repo_name")]  public string? RepoName { get; init; }
    [JsonPropertyName("branch")]     public string? Branch { get; init; }
    [JsonPropertyName("is_primary")] public bool IsPrimary { get; init; }
}
