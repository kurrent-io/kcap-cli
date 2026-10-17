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
    string? DaemonInstanceId = null
) {
    public const int CurrentSchemaVersion = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResumeLedgerEntry))]
public partial class ResumeLedgerJsonContext : JsonSerializerContext;
