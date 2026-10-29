using System.Text.Json.Serialization;

namespace Capacitor.Cli.Service.Control;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
public partial class ControlJsonContext : JsonSerializerContext;
