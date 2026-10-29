using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Accounts;

/// Every field is always emitted, so this context must never gain a DefaultIgnoreCondition.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AccountsSnapshotDto))]
public partial class AccountsSnapshotJsonContext : JsonSerializerContext;
