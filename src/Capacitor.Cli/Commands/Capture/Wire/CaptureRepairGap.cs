using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record CaptureRepairGap(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("source_line")] int SourceLine,
    [property: JsonPropertyName("call_id")] string? CallId);
