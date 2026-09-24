using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(CaptureRepairCapabilitiesResponse))]
[JsonSerializable(typeof(PrepareCaptureRepairRequest))]
[JsonSerializable(typeof(UploadCaptureRepairBatchRequest))]
[JsonSerializable(typeof(CompleteCaptureRepairRequest))]
[JsonSerializable(typeof(PrepareCaptureRepairResponse))]
[JsonSerializable(typeof(CaptureRepairResponse))]
[JsonSerializable(typeof(CaptureRepairError))]
internal partial class CaptureRepairJsonContext : JsonSerializerContext;
