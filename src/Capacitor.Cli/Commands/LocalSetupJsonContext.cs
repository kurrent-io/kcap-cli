using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocalSetupLine))]
partial class LocalSetupJsonContext : JsonSerializerContext;
