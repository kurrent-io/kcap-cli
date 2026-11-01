using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Setup.Intelligence;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IntelligenceCliLine))]
public partial class IntelligenceCliJsonContext : JsonSerializerContext;
