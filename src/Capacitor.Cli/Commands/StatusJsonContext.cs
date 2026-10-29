using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StatusJson))]
public partial class StatusJsonContext : JsonSerializerContext;
