using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record UploadCaptureRepairBatchRequest {
    [JsonPropertyName("source")] public CaptureRepairSourceRequest? Source { get; init; }
    [JsonPropertyName("batch_ordinal")] public int BatchOrdinal { get; init; }
    [JsonPropertyName("lines")] public CaptureRepairLineRequest[] Lines { get; init; } = [];
}
