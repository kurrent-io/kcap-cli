using System.Text.Json.Serialization;

namespace Capacitor.Cli.Capture;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CaptureLossMarker))]
[JsonSerializable(typeof(HeldLine))]
internal partial class CaptureJsonContext : JsonSerializerContext;
