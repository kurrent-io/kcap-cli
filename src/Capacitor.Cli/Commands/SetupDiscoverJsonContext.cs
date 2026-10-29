using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SetupDiscoverJson))]
public partial class SetupDiscoverJsonContext : JsonSerializerContext;
