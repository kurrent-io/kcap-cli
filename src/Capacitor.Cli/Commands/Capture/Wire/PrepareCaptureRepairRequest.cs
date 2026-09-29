using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record PrepareCaptureRepairRequest {
    [JsonPropertyName("sources")] public CaptureRepairSourceRequest[] Sources { get; init; } = [];
    [JsonPropertyName("dry_run")] public bool DryRun { get; init; }
}
