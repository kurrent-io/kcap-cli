using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

public sealed record MuseAssistantManifestPage(
        [property: JsonPropertyName("file")]   string? File   = null,
        [property: JsonPropertyName("cursor")] string? Cursor = null
    );
