using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Accounts.Readings;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ReadingFile))]
internal partial class ReadingsJsonContext : JsonSerializerContext;
