using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>What a hand-off of an ended session starts from. <c>session_commits</c> is null when the server's
/// commit scan failed and empty when the session made none.</summary>
public sealed record HandoffContextDto(
    [property: JsonPropertyName("session_id")]      string                 SessionId,
    [property: JsonPropertyName("repo_owner")]      string?                RepoOwner,
    [property: JsonPropertyName("repo_name")]       string?                RepoName,
    [property: JsonPropertyName("branch")]          string?                Branch,
    [property: JsonPropertyName("pull_request")]    HandoffPullRequestDto? PullRequest,
    [property: JsonPropertyName("session_commits")] IReadOnlyList<string>? SessionCommits);
