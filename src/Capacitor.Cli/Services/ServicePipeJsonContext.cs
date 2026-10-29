using System.Text.Json.Serialization;

namespace Capacitor.Cli.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServicePipeRequest))]
[JsonSerializable(typeof(ServicePipeResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
partial class ServicePipeJsonContext : JsonSerializerContext;
