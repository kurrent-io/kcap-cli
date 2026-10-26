using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One vendor account registered on a daemon's host.
public sealed record ListedAccountDto {
    [JsonPropertyName("id")]              public required string Id { get; init; }
    [JsonPropertyName("vendor")]          public required string Vendor { get; init; }
    [JsonPropertyName("directory")]       public required string Directory { get; init; }
    [JsonPropertyName("directory_key")]   public required string DirectoryKey { get; init; }
    [JsonPropertyName("label")]           public required string Label { get; init; }
    [JsonPropertyName("recording_state")] public required string RecordingState { get; init; }
}
