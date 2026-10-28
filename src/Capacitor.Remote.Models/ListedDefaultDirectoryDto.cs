using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// A vendor's default (no account chosen) config directory on a daemon.
public sealed record ListedDefaultDirectoryDto {
    [JsonPropertyName("vendor")]        public required string Vendor { get; init; }
    [JsonPropertyName("directory")]     public required string Directory { get; init; }
    [JsonPropertyName("directory_key")] public required string DirectoryKey { get; init; }
}
