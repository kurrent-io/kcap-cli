using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the in-place resume probe. snake_case on the wire. AccountId and ConfigDir
/// are the session's recorded store; a null ConfigDir is an unknown store.
public sealed record ResumeProbeRequestDto(
    string SessionId,
    string Vendor,
    string Directory,
    string? AccountId,
    string? ConfigDir);

/// Null Refusal: the daemon can resume the session here.
public sealed record ResumeProbeReplyDto(string? Refusal);

public static class ResumeInPlaceWire {
    /// The HelloReply capability that advertises the ResumeProbe handler and the daemon's
    /// LaunchAgentCommand.ResumeInPlace launches.
    public const string Capability = "resume-in-place/1";
}

/// Why the daemon will not resume a session in place; also the LaunchFailed reasons of a refused launch.
public static class ResumeInPlaceRefusals {
    public const string Invalid             = "resume_invalid";
    public const string UnsupportedVendor   = "resume_unsupported_vendor";
    public const string SourceActive        = "resume_source_active";
    public const string DirectoryMissing    = "resume_directory_missing";
    public const string DirectoryNotAllowed = "resume_directory_not_allowed";
    public const string StoreChanged        = "resume_store_changed";
    public const string Failed              = "resume_failed";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ResumeProbeRequestDto))]
[JsonSerializable(typeof(ResumeProbeReplyDto))]
public partial class ResumeInPlaceIpcJsonContext : JsonSerializerContext;
