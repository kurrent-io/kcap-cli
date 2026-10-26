using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One daemon's vendor accounts as the server serves them (hub GetDaemonAccounts and the
/// DaemonAccountsChanged push). HostId and Revision identify the host registry the accounts were
/// read from.
public sealed record DaemonAccountsDto {
    [JsonPropertyName("daemon_name")] public required string DaemonName { get; init; }
    [JsonPropertyName("host_id")]     public required string HostId { get; init; }
    [JsonPropertyName("revision")]    public long Revision { get; init; }
    [JsonPropertyName("accounts")]    public ListedAccountDto[]? Accounts { get; init; }
    [JsonPropertyName("defaults")]    public ListedDefaultDirectoryDto[]? Defaults { get; init; }
}
