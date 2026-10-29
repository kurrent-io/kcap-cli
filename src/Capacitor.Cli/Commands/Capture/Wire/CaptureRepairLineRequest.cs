using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record CaptureRepairLineRequest(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("redacted_json")] string RedactedJson,
    [property: JsonPropertyName("original_utf16_length")] int OriginalUtf16Length);
