using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record PublishArtefactVersionBody([property: JsonPropertyName("html")] string Html);
