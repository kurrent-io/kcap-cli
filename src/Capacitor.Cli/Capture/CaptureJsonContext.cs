using System.Text.Json.Serialization;

namespace Capacitor.Cli.Capture;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CaptureLossMarker))]
internal partial class CaptureJsonContext : JsonSerializerContext;
