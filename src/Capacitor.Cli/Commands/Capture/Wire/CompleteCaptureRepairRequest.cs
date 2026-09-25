using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed class CompleteCaptureRepairRequest {
    [JsonPropertyName("sources")] public CaptureRepairSourceEndRequest[] Sources { get; set; } = [];
}
