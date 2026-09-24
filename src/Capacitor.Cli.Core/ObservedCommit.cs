using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core;

/// <summary>Wire twin of the server's <c>ObservedCommit</c>: a commit the watcher saw land.
/// <see cref="Owner"/>/<see cref="RepoName"/> are null when git could not place it, and
/// <see cref="Message"/> is then only the subject the commit printed.</summary>
public sealed record ObservedCommit {
    [JsonPropertyName("sha")]       public required string  Sha      { get; init; }
    [JsonPropertyName("owner")]     public          string? Owner    { get; init; }
    [JsonPropertyName("repo_name")] public          string? RepoName { get; init; }
    [JsonPropertyName("branch")]    public          string? Branch   { get; init; }
    [JsonPropertyName("message")]   public required string  Message  { get; init; }
}
