using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ImportDiscoveryReport))]
internal partial class ImportDiscoveryReportJson : JsonSerializerContext;
