using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>The whole lifecycle payload of the session a chat currently has open, so a replay needs
/// nothing but this record.</summary>
public sealed record MuseAssistantOpenSession(
        [property: JsonPropertyName("session_id")]    string SessionId,
        [property: JsonPropertyName("first_seq")]     long   FirstSeq,
        [property: JsonPropertyName("started_at_ms")] long   StartedAtMs,
        [property: JsonPropertyName("is_main")]       bool   IsMain,
        [property: JsonPropertyName("home_dir")]      string HomeDir,
        [property: JsonPropertyName("phase")]         string Phase,
        [property: JsonPropertyName("ended_at_ms")]   long?  EndedAtMs
    );
