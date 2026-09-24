using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceScopeManifestDto {
    [JsonPropertyName("scope_version")]      public required string                  ScopeVersion      { get; init; }
    [JsonPropertyName("root_session_id")]    public required string                  RootSessionId     { get; init; }
    [JsonPropertyName("complete")]           public          bool                    Complete          { get; init; }
    [JsonPropertyName("incomplete_reasons")] public          List<string>            IncompleteReasons { get; init; } = [];
    [JsonPropertyName("sources")]            public          List<EvidenceSourceDto> Sources           { get; init; } = [];
    [JsonPropertyName("next_cursor")]        public          string?                 NextCursor        { get; init; }
    [JsonPropertyName("token")]              public required string                  Token             { get; init; }
    // Absent from a server that predates the wire lifetime; the scope client then assumes the 30-minute artifact.
    [JsonPropertyName("issued_at")]          public          DateTimeOffset?         IssuedAt          { get; init; }
    [JsonPropertyName("expires_at")]         public          DateTimeOffset?         ExpiresAt         { get; init; }
}
