using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record CaptureRepairError([property: JsonPropertyName("error")] string Error);
