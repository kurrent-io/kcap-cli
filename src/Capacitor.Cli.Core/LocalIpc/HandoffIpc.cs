using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Handoff;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the hand-off probe frames. snake_case on the wire. Branch is already the
/// target branch the app chose; the daemon maps nothing and copies HeadOwner/HeadRepo into the
/// reply's start.
public sealed record HandoffProbeRequestDto(
    string RepoPath,
    string RepoOwner,
    string RepoName,
    string? Branch,
    int? PullRequestNumber,
    bool HeadIsFork,
    string? HeadOwner,
    string? HeadRepo,
    IReadOnlyList<string>? SessionCommits);

/// At most one of Refusal, FetchError and Start is set. Start.SourceSessionId is empty; the
/// caller fills it. Completeness is a HandoffCompleteness name.
public sealed record HandoffProbeReplyDto(
    string? Refusal,
    string? FetchError,
    HandoffLaunchStart? Start,
    string? Completeness,
    IReadOnlyList<string>? MissingCommits);

public static class HandoffRefusals {
    public const string RepoNotAllowed = "repo_not_allowed";
    public const string RepoMismatch   = "repo_mismatch";
    public const string InvalidTarget  = "invalid_target";
}

public static class HandoffWire {
    /// The HelloReply capability that advertises the HandoffProbe handler.
    public const string Capability = "handoff/1";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HandoffProbeRequestDto))]
[JsonSerializable(typeof(HandoffProbeReplyDto))]
public partial class HandoffIpcJsonContext : JsonSerializerContext;
