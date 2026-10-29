using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>What the cron writes beside one walk's pages. Every member defaults to null so a missing
/// field is a validation message, not a deserialization failure under required-constructor-parameter rules.</summary>
public sealed record MuseAssistantManifest(
        [property: JsonPropertyName("chat_id")]       string?                      ChatId      = null,
        [property: JsonPropertyName("is_main")]       bool?                        IsMain      = null,
        [property: JsonPropertyName("fetched_at_ms")] long?                        FetchedAtMs = null,
        [property: JsonPropertyName("pages")]         MuseAssistantManifestPage[]? Pages       = null
    );
