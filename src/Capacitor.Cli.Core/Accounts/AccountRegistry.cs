using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Accounts;

public sealed record AccountRegistry {
    [JsonPropertyName("version")]  public int                          Version  { get; init; } = 1;
    [JsonPropertyName("revision")] public long                         Revision { get; init; }
    [JsonPropertyName("accounts")] public IReadOnlyList<VendorAccount> Accounts { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AccountRegistry))]
[JsonSerializable(typeof(HostIdentity))]
internal partial class AccountRegistryJsonContext : JsonSerializerContext;

internal sealed record HostIdentity([property: JsonPropertyName("host_id")] string HostId);
