using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>What the cron writes beside one walk's pages.</summary>
public sealed record MuseAssistantManifest(
        [property: JsonPropertyName("chat_id")]       string?                      ChatId,
        [property: JsonPropertyName("is_main")]       bool?                        IsMain,
        [property: JsonPropertyName("fetched_at_ms")] long?                        FetchedAtMs,
        [property: JsonPropertyName("pages")]         MuseAssistantManifestPage[]? Pages
    );
