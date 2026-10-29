using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Accounts;

public sealed record AccountRegistry {
    [JsonPropertyName("version")]  public int                          Version  { get; init; } = 1;
    [JsonPropertyName("revision")] public long                         Revision { get; init; }
    [JsonPropertyName("accounts")] public IReadOnlyList<VendorAccount> Accounts { get; init; } = [];
}
