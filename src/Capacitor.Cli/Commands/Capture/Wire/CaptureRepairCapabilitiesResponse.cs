using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record CaptureRepairCapabilitiesResponse([property: JsonPropertyName("protocol_version")] int ProtocolVersion);
