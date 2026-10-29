namespace Capacitor.Cli;

/// <summary>Where a claimed session reports: the vendor and server its hooks post to, and the transcript
/// and working directory a session-end names, and the hosted agent and daemon epoch it ran under.</summary>
sealed record ClaimOrigin(
    string? Vendor, string? Server, string? TranscriptPath, string? Cwd, string? AgentId = null, string? DaemonEpoch = null);
