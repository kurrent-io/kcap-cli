namespace Capacitor.Cli.Core.Accounts;

/// <summary>A host's accounts as the daemon publishes them, on the local socket and to the server.
/// <see cref="Revision"/> only increases while <see cref="HostId"/> stays the same.</summary>
public sealed record AccountsSnapshotDto(string HostId, long Revision, AccountViewDto[] Accounts, DefaultDirectoryDto[] Defaults);
