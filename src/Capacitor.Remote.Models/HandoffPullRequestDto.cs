using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>The pull request a hand-off source session recorded. <c>head_is_fork</c> is true only for a verified
/// fork and false for the session's repository itself; null when the head could not be classified.</summary>
public sealed record HandoffPullRequestDto(
    [property: JsonPropertyName("number")]       int     Number,
    [property: JsonPropertyName("title")]        string? Title,
    [property: JsonPropertyName("state")]        string? State,
    [property: JsonPropertyName("head_ref")]     string? HeadRef,
    [property: JsonPropertyName("head_sha")]     string? HeadSha,
    [property: JsonPropertyName("head_owner")]   string? HeadOwner,
    [property: JsonPropertyName("head_repo")]    string? HeadRepo,
    [property: JsonPropertyName("head_is_fork")] bool?   HeadIsFork);
