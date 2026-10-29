using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A page of the session listing. Total counts the whole scope, not the page.
public sealed record RepoSessionsResponseDto {
    [JsonPropertyName("items")]       public required RepoSessionDto[] Items { get; init; }
    [JsonPropertyName("total")]       public int Total { get; init; }
    [JsonPropertyName("limit")]       public int Limit { get; init; }
    [JsonPropertyName("offset")]      public int Offset { get; init; }
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
}
