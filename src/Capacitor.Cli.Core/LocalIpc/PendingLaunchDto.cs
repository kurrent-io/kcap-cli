namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>
/// One in-flight launch in the daemon status payload: its identity as the launch request named it,
/// and the runtime's latest handshake stage (an open vocabulary stamped by the runtime, null
/// before the first stage).
/// </summary>
public sealed record PendingLaunchDto(
    string Id, string Vendor, string? RepoPath, string? Title, DateTime CreatedAt, string? Stage);
