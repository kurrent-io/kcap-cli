using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:Capacitor.Cli.Core.Resume", Justification = "Resume is the domain term; no consumer language needs it as a namespace keyword.")]

namespace Capacitor.Cli.Core.Resume;

public sealed record ResumeLedgerEntry(
    int SchemaVersion,
    string AgentId,
    string DaemonId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Vendor,
    string Transport,
    string? SessionId,
    string? VendorSession,
    ResumeLaunchDescriptor Launch,
    ResumeState State,
    int Attempts,
    string? LastError,
    string? PredecessorInstanceId,
    DateTimeOffset? ResumingAt,
    string? DaemonInstanceId = null,
    // Agents this one continued, whose attachment directories its prompt still names; the boot sweep
    // keeps them for as long as this entry lives.
    IReadOnlyList<string>? InheritedAgentIds = null,
    // What a client needs to open the agent's terminal and transcript while it has no process: the
    // runtime's own answers, null in an entry written before the runtime started or by an older daemon.
    bool? HasTerminal = null,
    string? TranscriptFormat = null,
    // When the runtime now recorded started; each launch and each resume stamps its own, so a client can
    // tell the runtime it attached to from a successor. Null before the runtime starts.
    DateTimeOffset? StartedAt = null,
    // Whether the agent was waiting for input when its daemon stopped it. Null in an entry written by an older
    // daemon, which reads as interrupted mid-turn.
    bool? AwaitingInput = null
) {
    public const int CurrentSchemaVersion = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResumeLedgerEntry))]
public partial class ResumeLedgerJsonContext : JsonSerializerContext;
