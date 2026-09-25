using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record PrepareCaptureRepairResponse(
    [property: JsonPropertyName("repair_id")] string RepairId,
    [property: JsonPropertyName("phase")] string Phase);
