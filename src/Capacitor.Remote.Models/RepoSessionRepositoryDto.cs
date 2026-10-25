using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A listed session's primary repository; Owner and Name are null when the server holds no label
/// for the hash.
public sealed record RepoSessionRepositoryDto {
    [JsonPropertyName("hash")]  public required string Hash { get; init; }
    [JsonPropertyName("owner")] public string? Owner { get; init; }
    [JsonPropertyName("name")]  public string? Name { get; init; }
}
