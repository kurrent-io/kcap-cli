using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core;

/// <summary>
/// Wire twin of the server's <c>ObservedCommit</c>. <see cref="Owner"/>/<see cref="RepoName"/>
/// are null when the repository has no remote kcap can parse.
/// </summary>
public sealed record ObservedCommit {
    [JsonPropertyName("sha")]       public required string  Sha      { get; init; }
    [JsonPropertyName("owner")]     public          string? Owner    { get; init; }
    [JsonPropertyName("repo_name")] public          string? RepoName { get; init; }
    [JsonPropertyName("branch")]    public          string? Branch   { get; init; }
    [JsonPropertyName("message")]   public required string  Message  { get; init; }
}
