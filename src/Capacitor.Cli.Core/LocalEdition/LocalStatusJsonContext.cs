using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalEdition;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(LocalStatusJson))]
public partial class LocalStatusJsonContext : JsonSerializerContext;
