using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>What was written at a path: the hash of the file as written, and the document it
/// served. A receipt is the only thing that authorises deleting bytes.</summary>
public sealed record SkillReceipt {
    [JsonPropertyName("file_hash")] public required string        FileHash { get; init; }
    [JsonPropertyName("document")]  public required SkillDocument Document { get; init; }

    public bool Matches(string fileHash) => string.Equals(FileHash, fileHash, StringComparison.Ordinal);
}
