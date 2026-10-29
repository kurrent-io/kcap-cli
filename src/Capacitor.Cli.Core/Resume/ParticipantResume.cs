namespace Capacitor.Cli.Core.Resume;

/// <summary>What a flow participant's resume needs past the interactive launch descriptor. The
/// certification carries no connection id or advertised CLI version: both belong to the connection the
/// participant was launched over.</summary>
public sealed record ParticipantResume(
    LaunchKind Kind,
    string[]? McpAllowlist,
    string[]? Tools,
    ReviewerCertificationRequirement? Certification,
    ulong ActivitySeq,
    ulong AgeMs,
    string? LastRoundInput,
    DateTimeOffset? LastRoundAt
);
