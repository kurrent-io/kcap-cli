using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record CaptureRepairSourceEndRequest(
    [property: JsonPropertyName("source")] CaptureRepairSourceRequest Source,
    [property: JsonPropertyName("last_batch_ordinal")] int LastBatchOrdinal,
    [property: JsonPropertyName("last_line_number")] int LastLineNumber);
