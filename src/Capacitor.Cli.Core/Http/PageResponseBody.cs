using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record PageResponseBody(
    [property: JsonPropertyName("version")] int         Version,
    [property: JsonPropertyName("payload")] JsonElement Payload);
