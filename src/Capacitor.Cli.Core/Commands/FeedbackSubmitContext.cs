using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

public sealed record FeedbackSubmitContext(
        [property: JsonPropertyName("source")]         string  Source,
        [property: JsonPropertyName("client_version")] string? ClientVersion,
        [property: JsonPropertyName("os")]             string? Os
    );
