using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The machine and daemon a session was recorded under, its registered account and the physical vendor
/// config directory. A null ConfigDir is an unknown store, never the default one.
public sealed record RepoSessionRecordingDto {
    [JsonPropertyName("machine_id")]  public string? MachineId { get; init; }
    [JsonPropertyName("daemon_name")] public string? DaemonName { get; init; }
    [JsonPropertyName("account_id")]  public string? AccountId { get; init; }
    [JsonPropertyName("config_dir")]  public string? ConfigDir { get; init; }
}
