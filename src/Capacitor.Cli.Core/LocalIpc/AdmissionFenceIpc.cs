using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the rename fence frames. snake_case on the wire; every member always emitted.
public sealed record AdmissionFenceAcquireDto(string? ExpectedName);

/// One reply shape for acquire, commit and abort. Pid and InstanceId identify the daemon process the
/// fence belongs to, so a caller can bind it to the process it is about to retire.
public sealed record AdmissionFenceAckDto(bool Ok, string? Reason, string? State, int? Pid, string? InstanceId);

public static class AdmissionFenceWire {
    /// The HelloReply capability that advertises the fence frames.
    public const string Capability = "fence/1";

    /// Prefix of the launch-failure reason a fenced daemon reports to the server.
    public const string RetiringReasonPrefix = "daemon_retiring";

    public const string Held      = "held";
    public const string Committed = "committed";
    public const string Aborted   = "aborted";

    public const string Busy             = "busy";
    /// Another rename holds or committed the fence.
    public const string Fenced           = "fenced";
    public const string IdentityMismatch = "identity_mismatch";
    public const string Malformed        = "malformed";
    public const string CommitFailed     = "commit_failed";
    public const string AbortFailed      = "abort_failed";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AdmissionFenceAcquireDto))]
[JsonSerializable(typeof(AdmissionFenceAckDto))]
public partial class AdmissionFenceIpcJsonContext : JsonSerializerContext;
