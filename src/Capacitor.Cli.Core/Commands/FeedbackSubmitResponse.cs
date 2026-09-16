using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

public sealed record FeedbackSubmitResponse {
    [JsonPropertyName("reporter_email")] public string ReporterEmail { get; init; } = "";
}
