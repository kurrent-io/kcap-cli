using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Accounts;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AccountRegistry))]
[JsonSerializable(typeof(HostIdentity))]
internal partial class AccountRegistryJsonContext : JsonSerializerContext;
