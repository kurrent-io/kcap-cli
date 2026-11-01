using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Handoff;

namespace Capacitor.App.Services;

/// Model "", Effort null, PermissionMode null, CodexPosture null and AccountId null all mean
/// "vendor default" — the wire's own conventions (the server rejects a null model; the daemon
/// treats whitespace as no request).
public sealed record LaunchRequest(
    string DaemonName, string RepoPath, string Vendor, string? Prompt,
    string Model = "", string? Effort = null, string? PermissionMode = null,
    IReadOnlyList<string>? AttachmentIds = null, CodexLaunchPosture? CodexPosture = null,
    HandoffLaunchStart? Handoff = null,
    ResumeLaunchPayload? Resume = null,
    string? AccountId = null);

/// An in-place resume of an ended session: the server reads the store it was recorded under itself.
public sealed record ResumeLaunchPayload(
    [property: JsonPropertyName("source_session_id")] string SourceSessionId,
    [property: JsonPropertyName("directory")]         string Directory);

/// Unauthorized marks a server 401 — the caller routes it to sign-in instead of rendering the
/// raw transport message.
public sealed record LaunchOutcome(bool Started, string? AgentId, string? Error, bool Unauthorized = false);

/// Starting a session goes through the SERVER, not the local socket: the local Spawn frame
/// resolves against the daemon's PTY launchers (claude and codex only), while the server's
/// RequestLaunchAgentV2 reaches every vendor through the runtime factories.
public interface ILaunchClient {
    Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct);

    /// The launch capabilities the server lists, such as <c>handoff/1</c>. Empty when the server
    /// answered without them, including a server that lacks the method; null when there was no answer
    /// (no live connection, or the call failed). Never throws.
    Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct);
}

/// The RequestLaunchAgentV2 hub argument, serialized by SignalR through reflection. Member names
/// must match LaunchAgentRequestV2's properties — the hub binds by name.
// Explicit snake_case on every member: the names survive whatever naming policy the hub options
// carry, and a key that drifts from the server record binds null there without an error.
public sealed record LaunchAgentRequestV2Payload {
    [JsonPropertyName("daemon_name")]           public required string   DaemonName          { get; init; }
    [JsonPropertyName("prompt")]                public          string?  Prompt              { get; init; }
    [JsonPropertyName("model")]                 public required string   Model               { get; init; }
    [JsonPropertyName("effort")]                public          string?  Effort              { get; init; }
    [JsonPropertyName("repo_path")]             public required string   RepoPath            { get; init; }
    [JsonPropertyName("tools")]                 public          string[]? Tools              { get; init; }
    [JsonPropertyName("attachment_ids")]        public          string[]? AttachmentIds      { get; init; }
    [JsonPropertyName("visibility")]            public          string?  Visibility          { get; init; }
    [JsonPropertyName("grants")]                public          object[]? Grants             { get; init; }
    [JsonPropertyName("vendor")]                public required string   Vendor              { get; init; }
    [JsonPropertyName("codex_posture")]         public          CodexLaunchPosture? CodexPosture { get; init; }
    [JsonPropertyName("acp_permission_preset")] public          string?  AcpPermissionPreset { get; init; }
    // Omitted when null, so an unchosen mode leaves the payload an older server expects untouched.
    [JsonPropertyName("permission_mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionMode { get; init; }
    // Omitted when null: only a hand-off carries the block, and an older server never sees the key.
    [JsonPropertyName("handoff"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HandoffLaunchStart? Handoff { get; init; }
    // Omitted when null: only a resume carries the block, and an older server never sees the key.
    [JsonPropertyName("resume"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResumeLaunchPayload? Resume { get; init; }
    // Omitted when null: a launch that names no account leaves the payload an older server
    // expects untouched.
    [JsonPropertyName("account_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountId { get; init; }
}

/// The RequestLaunchAgentV2 argument, split from the transport so its shape is testable.
public static class LaunchPayload {
    public static LaunchAgentRequestV2Payload For(LaunchRequest r) => new() {
        DaemonName = r.DaemonName,
        Prompt     = string.IsNullOrWhiteSpace(r.Prompt) ? null : r.Prompt,
        Model      = r.Model.Trim(),   // "" = vendor default; the server rejects null
        Effort     = string.IsNullOrWhiteSpace(r.Effort) ? null : r.Effort,
        RepoPath   = r.RepoPath,
        Vendor     = r.Vendor,
        // Null, not an empty array: the server reads an empty array as "these ids, none of them".
        AttachmentIds = r.AttachmentIds is { Count: > 0 } ids ? [.. ids] : null,
        PermissionMode = string.IsNullOrWhiteSpace(r.PermissionMode) ? null : r.PermissionMode,
        CodexPosture   = r.CodexPosture,
        Handoff        = r.Handoff,
        Resume         = r.Resume,
        AccountId      = string.IsNullOrWhiteSpace(r.AccountId) ? null : r.AccountId,
    };
}
